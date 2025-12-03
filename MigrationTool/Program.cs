using Microsoft.Extensions.Configuration;
using MySqlConnector;
using System.Diagnostics;
using System.Text.RegularExpressions;

// Load configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var connectionString = configuration.GetConnectionString("StagingDatabase");
var outputPath = configuration["OutputPath"] ?? "./migration-output";

if (string.IsNullOrEmpty(connectionString))
{
    Console.WriteLine("ERROR: Connection string not found");
    return 1;
}

Console.WriteLine("EspoCRM ID Migration Tool");
Console.WriteLine("=========================\n");
Console.WriteLine($"Output: {outputPath}\n");

// Menu
Console.WriteLine("Stages:");
Console.WriteLine("  1. Generate ID mapping SQL");
Console.WriteLine("  2. Dump and transform schema");
Console.WriteLine("  3. Dump data (7 large + 811 batch)");
Console.WriteLine("  4. Transform dumps");
Console.WriteLine("  5. Run all stages");
Console.WriteLine();
Console.Write("Select (1-5): ");

var choice = Console.ReadLine();

return choice switch
{
    "1" => await Stage1_GenerateMapping(),
    "2" => await Stage2_SchemaMigration(),
    "3" => await Stage3_DumpData(),
    "4" => await Stage4_TransformDumps(),
    "5" => await RunAll(),
    _ => 1
};

async Task<int> Stage1_GenerateMapping()
{
    Console.WriteLine("\n=== Stage 1: ID Mapping ===\n");

    Directory.CreateDirectory(outputPath);
    var sqlFile = Path.Combine(outputPath, "01_create_id_mapping.sql");

    using var writer = new StreamWriter(sqlFile);

    await writer.WriteLineAsync("-- Stage 1: ID Mapping Table");
    await writer.WriteLineAsync("-- Generated: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("CREATE DATABASE IF NOT EXISTS espocrm_migration;");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("CREATE TABLE IF NOT EXISTS espocrm_migration.id_mapping (");
    await writer.WriteLineAsync("  old_id VARCHAR(17) PRIMARY KEY,");
    await writer.WriteLineAsync("  new_id BIGINT UNSIGNED NOT NULL,");
    await writer.WriteLineAsync("  INDEX idx_new_id (new_id)");
    await writer.WriteLineAsync(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("TRUNCATE TABLE espocrm_migration.id_mapping;");
    await writer.WriteLineAsync();

    // Query information_schema to get table list
    using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    using var cmd = new MySqlCommand(@"
        SELECT TABLE_NAME
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = 'espocrm'
        AND COLUMN_NAME = 'id'
        AND DATA_TYPE = 'varchar'
        AND CHARACTER_MAXIMUM_LENGTH = 17
        ORDER BY TABLE_NAME", conn);

    var tables = new List<string>();
    using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
    }

    await writer.WriteLineAsync($"-- Generating mappings for {tables.Count} tables");
    await writer.WriteLineAsync();

    foreach (var table in tables)
    {
        await writer.WriteLineAsync($"INSERT INTO espocrm_migration.id_mapping (old_id, new_id)");
        await writer.WriteLineAsync($"SELECT id, UUID_SHORT() FROM espocrm.`{table}` WHERE id IS NOT NULL;");
        await writer.WriteLineAsync();
    }

    Console.WriteLine($"✓ Generated: {sqlFile}\n");
    Console.WriteLine("Execute: mysql -u espocrm_migration -p < " + sqlFile);
    Console.WriteLine("Monitor: mysql -u espocrm_migration -p -e 'SHOW PROCESSLIST'\n");

    return 0;
}

async Task<int> Stage2_SchemaMigration()
{
    Console.WriteLine("\n=== Stage 2: Schema Migration ===\n");

    var connParts = ParseConnectionString(connectionString);
    Directory.CreateDirectory(outputPath);

    var tempFile = Path.Combine(outputPath, "temp_schema.sql");
    var finalFile = Path.Combine(outputPath, "02_schema_migration.sql");

    // Dump schema
    Console.WriteLine("Dumping schema...");
    var dumpArgs = $"-h {connParts["host"]} -P {connParts["port"]} -u {connParts["user"]} -p{connParts["password"]} --no-data --skip-lock-tables --no-tablespaces --set-gtid-purged=OFF espocrm";

    var psi = new ProcessStartInfo
    {
        FileName = "mysqldump",
        Arguments = dumpArgs,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    using (var process = Process.Start(psi))
    {
        var output = await process!.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"ERROR: {error}");
            return 1;
        }

        await File.WriteAllTextAsync(tempFile, output);
    }

    Console.WriteLine($"✓ Dumped ({new FileInfo(tempFile).Length / 1024 / 1024} MB)\n");

    // Transform
    Console.WriteLine("Transforming varchar(17) → bigint...");
    var schema = await File.ReadAllTextAsync(tempFile);

    // Transform varchar(17) columns to bigint
    var pattern = @"`(id|[a-z_]+_id)` varchar\(17\)( CHARACTER SET [^\s]+ COLLATE [^\s,]+)?";
    var transformed = Regex.Replace(schema, pattern, "`$1` bigint unsigned");
    var count = Regex.Matches(schema, pattern).Count;

    // Fix DEFAULT '' for bigint columns (empty string not valid for bigint)
    transformed = Regex.Replace(transformed, @"bigint unsigned NOT NULL DEFAULT ''", "bigint unsigned NOT NULL DEFAULT 0");
    transformed = Regex.Replace(transformed, @"bigint unsigned DEFAULT ''", "bigint unsigned DEFAULT NULL");

    await File.WriteAllTextAsync(finalFile, transformed);
    File.Delete(tempFile);

    Console.WriteLine($"✓ Transformed {count} columns");
    Console.WriteLine($"✓ Saved: {finalFile}\n");
    Console.WriteLine("Execute: mysql -u espocrm_migration -p espocrm_migration < " + finalFile + "\n");

    return 0;
}

async Task<int> Stage3_DumpData()
{
    Console.WriteLine("\n=== Stage 3: Data Dumps ===\n");

    var connParts = ParseConnectionString(connectionString);
    Directory.CreateDirectory(outputPath);

    var largeTables = new[] {
        "app_log_record", "action_history_record", "attachment",
        "note", "email_email_account", "entity_user", "email"
    };

    var baseArgs = $"-h {connParts["host"]} -P {connParts["port"]} -u {connParts["user"]} -p{connParts["password"]} --no-create-info --complete-insert --skip-extended-insert --skip-lock-tables --no-tablespaces --set-gtid-purged=OFF";

    // Dump 7 large tables
    Console.WriteLine("Dumping 7 large tables...\n");
    for (int i = 0; i < largeTables.Length; i++)
    {
        var table = largeTables[i];
        var file = Path.Combine(outputPath, $"03_{table}.sql");
        Console.Write($"[{i + 1}/7] {table}... ");

        // Use shell redirection to avoid loading huge dumps into memory
        var cmd = $"mysqldump {baseArgs} espocrm {table} > \"{file}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{cmd}\"",
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        await process!.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(file))
        {
            Console.WriteLine("ERROR");
            continue;
        }

        Console.WriteLine($"{new FileInfo(file).Length / 1024 / 1024} MB");
    }

    // Dump remaining tables
    Console.WriteLine("\nDumping remaining 811 tables...");
    var ignore = string.Join(" ", largeTables.Select(t => $"--ignore-table=espocrm.{t}"));
    var batchFile = Path.Combine(outputPath, "03_batch_tables.sql");

    // Use shell redirection
    var batchCmd = $"mysqldump {baseArgs} {ignore} espocrm > \"{batchFile}\"";

    var batchPsi = new ProcessStartInfo
    {
        FileName = "/bin/bash",
        Arguments = $"-c \"{batchCmd}\"",
        UseShellExecute = false
    };

    using (var process = Process.Start(batchPsi))
    {
        await process!.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(batchFile))
        {
            Console.WriteLine("ERROR");
            return 1;
        }
    }

    Console.WriteLine($"✓ Batch ({new FileInfo(batchFile).Length / 1024 / 1024} MB)\n");
    Console.WriteLine("✓ Stage 3 Complete\n");

    return 0;
}

async Task<int> Stage4_TransformDumps()
{
    Console.WriteLine("\n=== Stage 4: Transform Dumps ===\n");

    // Step 1: Load id_mapping dictionary from database
    Console.WriteLine("Loading ID mapping from database...");

    using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    var mapping = new Dictionary<string, long>();
    using (var cmd = new MySqlCommand("SELECT old_id, new_id FROM espocrm_migration.id_mapping", conn))
    {
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            mapping[reader.GetString(0)] = reader.GetInt64(1);
        }
    }

    Console.WriteLine($"✓ Loaded {mapping.Count:N0} mappings\n");

    // Step 2: Find all dump files
    var dumpFiles = Directory.GetFiles(outputPath, "03_*.sql").OrderBy(f => f).ToArray();

    if (dumpFiles.Length == 0)
    {
        Console.WriteLine("ERROR: No dump files found. Run Stage 3 first.");
        return 1;
    }

    Console.WriteLine($"Found {dumpFiles.Length} dump files to transform\n");

    // Step 3: Transform each dump file
    var idPattern = new Regex(@"'([0-9a-f]{17})'");

    foreach (var dumpFile in dumpFiles)
    {
        var fileName = Path.GetFileName(dumpFile);
        var outputFile = Path.Combine(outputPath, fileName.Replace("03_", "04_").Replace(".sql", ".transformed.sql"));

        Console.WriteLine($"Transforming {fileName}...");

        // Count lines for progress
        Console.Write("  Counting lines... ");
        var totalLines = File.ReadLines(dumpFile).Count();
        Console.WriteLine($"{totalLines:N0}");

        // Transform with progress
        using var reader = new StreamReader(dumpFile);
        using var writer = new StreamWriter(outputFile);

        var currentLine = 0;
        var lastProgressUpdate = 0;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;

            currentLine++;

            // Replace all varchar(17) IDs with bigint from mapping
            var transformed = idPattern.Replace(line, match =>
            {
                var oldId = match.Groups[1].Value;
                return mapping.TryGetValue(oldId, out var newId) ? $"'{newId}'" : match.Value;
            });

            await writer.WriteLineAsync(transformed);

            // Update progress every 1%
            var progress = (currentLine * 100) / totalLines;
            if (progress > lastProgressUpdate)
            {
                lastProgressUpdate = progress;
                Console.Write($"\r  Progress: [{currentLine:N0} / {totalLines:N0}] {progress}%");
            }
        }

        Console.WriteLine($"\r  ✓ Complete: {new FileInfo(outputFile).Length / 1024 / 1024} MB");
    }

    Console.WriteLine($"\n✓ Stage 4 Complete - {dumpFiles.Length} files transformed\n");

    return 0;
}

async Task<int> RunAll()
{
    Console.WriteLine("\n=== Running All Stages ===\n");
    Console.WriteLine("Not implemented yet\n");
    return 1;
}

Dictionary<string, string> ParseConnectionString(string connStr)
{
    return connStr.Split(';')
        .Select(p => p.Split('='))
        .Where(p => p.Length == 2)
        .ToDictionary(
            p => p[0].Trim().ToLower() switch
            {
                "server" => "host",
                "uid" => "user",
                "pwd" => "password",
                _ => p[0].Trim().ToLower()
            },
            p => p[1].Trim(),
            StringComparer.OrdinalIgnoreCase
        );
}
