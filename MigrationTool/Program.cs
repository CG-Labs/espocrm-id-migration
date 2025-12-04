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
Console.WriteLine("  4b. Patch transformed files (fix missed FKs)");
Console.WriteLine("  5. Import transformed data");
Console.WriteLine("  6. Benchmark queries (varchar vs bigint)");
Console.WriteLine("  7. Run all stages");
Console.WriteLine();
Console.Write("Select (1-7 or 4b): ");

var choice = Console.ReadLine();

return choice switch
{
    "1" => await Stage1_GenerateMapping(),
    "2" => await Stage2_SchemaMigration(),
    "3" => await Stage3_DumpData(),
    "4" => await Stage4_TransformDumps(),
    "4b" => await Stage4b_PatchTransformedFiles(),
    "5" => await Stage5_ImportData(),
    "6" => await Stage6_BenchmarkQueries(),
    "7" => await RunAll(),
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
    await writer.WriteLineAsync("-- Using INSERT IGNORE to skip duplicates (idempotent)");
    await writer.WriteLineAsync();

    // Query information_schema to get ALL varchar(17) columns (id and *_id)
    using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    using var cmd = new MySqlCommand(@"
        SELECT DISTINCT TABLE_NAME, COLUMN_NAME
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = 'espocrm'
        AND COLUMN_NAME REGEXP '^(id|[a-z_]+_id)$'
        AND DATA_TYPE = 'varchar'
        AND CHARACTER_MAXIMUM_LENGTH = 17
        ORDER BY TABLE_NAME, COLUMN_NAME", conn);

    var tableColumns = new List<(string table, string column)>();
    using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            tableColumns.Add((reader.GetString(0), reader.GetString(1)));
        }
    }

    await writer.WriteLineAsync($"-- Generating mappings from {tableColumns.Count} varchar(17) columns across all tables");
    await writer.WriteLineAsync();

    foreach (var (table, column) in tableColumns)
    {
        await writer.WriteLineAsync($"-- {table}.{column}");
        await writer.WriteLineAsync($"INSERT IGNORE INTO espocrm_migration.id_mapping (old_id, new_id)");
        await writer.WriteLineAsync($"SELECT DISTINCT `{column}`, UUID_SHORT() FROM espocrm.`{table}` WHERE `{column}` IS NOT NULL;");
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
    var urlPattern = new Regex(@"/#[A-Za-z]+/view/([0-9a-f]{17})");
    var queryStringPattern = new Regex(@"(entryPoint=[^&""]+&(?:amp;)?id=)([0-9a-f]{17})");

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

            // Also replace URL patterns like /#Entity/view/ID
            transformed = urlPattern.Replace(transformed, match =>
            {
                var oldId = match.Groups[1].Value;
                if (mapping.TryGetValue(oldId, out var newId))
                {
                    return match.Value.Replace(oldId, newId.ToString());
                }
                return match.Value;
            });

            // Replace query string patterns like ?entryPoint=attachment&amp;id=ID
            transformed = queryStringPattern.Replace(transformed, match =>
            {
                var oldId = match.Groups[2].Value;
                if (mapping.TryGetValue(oldId, out var newId))
                {
                    return match.Groups[1].Value + newId.ToString();
                }
                return match.Value;
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

async Task<int> Stage4b_PatchTransformedFiles()
{
    Console.WriteLine("\n=== Stage 4b: Patch Transformed Files ===\n");

    // Load complete id_mapping
    Console.WriteLine("Loading complete ID mapping...");
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

    // Find transformed files
    var transformedFiles = Directory.GetFiles(outputPath, "04_*.transformed.sql").OrderBy(f => f).ToArray();

    if (transformedFiles.Length == 0)
    {
        Console.WriteLine("ERROR: No transformed files found");
        return 1;
    }

    Console.WriteLine($"Patching {transformedFiles.Length} transformed files...\n");

    var idPattern = new Regex(@"'([0-9a-f]{17})'");
    var urlPattern = new Regex(@"/#[A-Za-z]+/view/([0-9a-f]{17})");
    var queryStringPattern = new Regex(@"(entryPoint=[^&""]+&(?:amp;)?id=)([0-9a-f]{17})");

    foreach (var file in transformedFiles)
    {
        var fileName = Path.GetFileName(file);
        Console.Write($"Patching {fileName}... ");

        var tempFile = file + ".patch";

        // Stream read, replace remaining varchar IDs, write
        using var reader = new StreamReader(file);
        using var writer = new StreamWriter(tempFile);

        int replacements = 0;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;

            var transformed = idPattern.Replace(line, match =>
            {
                var oldId = match.Groups[1].Value;
                if (mapping.TryGetValue(oldId, out var newId))
                {
                    replacements++;
                    return $"'{newId}'";
                }
                return match.Value;
            });

            // Also replace URL patterns
            transformed = urlPattern.Replace(transformed, match =>
            {
                var oldId = match.Groups[1].Value;
                if (mapping.TryGetValue(oldId, out var newId))
                {
                    replacements++;
                    return match.Value.Replace(oldId, newId.ToString());
                }
                return match.Value;
            });

            // Replace query string patterns
            transformed = queryStringPattern.Replace(transformed, match =>
            {
                var oldId = match.Groups[2].Value;
                if (mapping.TryGetValue(oldId, out var newId))
                {
                    replacements++;
                    return match.Groups[1].Value + newId.ToString();
                }
                return match.Value;
            });

            await writer.WriteLineAsync(transformed);
        }

        reader.Close();
        writer.Close();

        // Replace original with patched
        File.Delete(file);
        File.Move(tempFile, file);

        Console.WriteLine($"✓ {replacements:N0} varchar IDs replaced");
    }

    Console.WriteLine($"\n✓ Patch complete\n");

    return 0;
}

async Task<int> Stage5_ImportData()
{
    Console.WriteLine("\n=== Stage 5: Import Transformed Data ===\n");

    var connParts = ParseConnectionString(connectionString);

    // Find all transformed dump files
    var transformedFiles = Directory.GetFiles(outputPath, "04_*.transformed.sql").OrderBy(f => f).ToArray();

    if (transformedFiles.Length == 0)
    {
        Console.WriteLine("ERROR: No transformed files found. Run Stage 4 first.");
        return 1;
    }

    Console.WriteLine($"Found {transformedFiles.Length} transformed files to import\n");

    // Import each file
    for (int i = 0; i < transformedFiles.Length; i++)
    {
        var file = transformedFiles[i];
        var fileName = Path.GetFileName(file);

        Console.WriteLine($"[{i + 1}/{transformedFiles.Length}] Importing {fileName}...");
        Console.WriteLine($"  Size: {new FileInfo(file).Length / 1024 / 1024} MB");

        // Import using mysql CLI with shell redirection and increased packet size
        var importCmd = $"mysql -h {connParts["host"]} -P {connParts["port"]} -u {connParts["user"]} -p{connParts["password"]} --max-allowed-packet=1G espocrm_migration < \"{file}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{importCmd}\"",
            UseShellExecute = false,
            RedirectStandardError = true
        };

        var startTime = DateTime.Now;
        using var process = Process.Start(psi);

        // Monitor progress by checking SHOW PROCESSLIST every 10 seconds
        while (!process!.HasExited)
        {
            await Task.Delay(10000);

            var elapsed = (DateTime.Now - startTime).TotalMinutes;
            Console.Write($"\r  Running... {elapsed:F1} minutes elapsed");
        }

        await process.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"\n  ERROR: Import failed");
            Console.WriteLine(error);
            return 1;
        }

        var totalTime = (DateTime.Now - startTime).TotalMinutes;
        Console.WriteLine($"\r  ✓ Complete in {totalTime:F1} minutes                    ");
    }

    Console.WriteLine($"\n✓ Stage 5 Complete - All files imported\n");

    return 0;
}

async Task<int> Stage6_BenchmarkQueries()
{
    Console.WriteLine("\n=== Stage 6: Benchmark Queries ===\n");

    var slowLogPath = Path.Combine(outputPath, "mysql_slow_query.log");

    if (!File.Exists(slowLogPath))
    {
        Console.WriteLine($"ERROR: Slow query log not found at {slowLogPath}");
        return 1;
    }

    // Parse slow query log
    Console.WriteLine("Parsing slow query log...");

    var queries = new List<(double queryTime, string query)>();
    var lines = File.ReadAllLines(slowLogPath);

    double currentQueryTime = 0;
    for (int i = 0; i < lines.Length; i++)
    {
        if (lines[i].StartsWith("# Query_time:"))
        {
            var parts = lines[i].Split(' ');
            currentQueryTime = double.Parse(parts[2]);
        }
        else if (lines[i].StartsWith("SELECT") || lines[i].StartsWith("UPDATE") || lines[i].StartsWith("DELETE"))
        {
            var query = lines[i];
            // Continue reading multi-line queries
            while (i + 1 < lines.Length && !lines[i + 1].StartsWith("#") && !string.IsNullOrWhiteSpace(lines[i + 1]))
            {
                i++;
                query += " " + lines[i];
            }

            if (currentQueryTime >= 10.0) // Only queries 10s+
            {
                queries.Add((currentQueryTime, query));
            }
        }
    }

    Console.WriteLine($"✓ Found {queries.Count} slow queries (>10s)\n");

    if (queries.Count == 0)
    {
        Console.WriteLine("No queries to benchmark");
        return 0;
    }

    // Load ID mapping for transformation
    Console.WriteLine("Loading ID mapping...");
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

    // Transform and benchmark each query
    var idPattern = new Regex(@"'([0-9a-f]{17})'");
    var results = new List<string>();

    for (int i = 0; i < queries.Count; i++)
    {
        var (originalTime, query) = queries[i];

        Console.WriteLine($"[{i + 1}/{queries.Count}] Benchmarking query (original VARCHAR time: {originalTime:F2}s)...");

        // Transform query varchar IDs to bigint
        var transformedQuery = idPattern.Replace(query, match =>
        {
            var oldId = match.Groups[1].Value;
            return mapping.TryGetValue(oldId, out var newId) ? $"'{newId}'" : match.Value;
        });

        // Benchmark on espocrm_migration (bigint) only
        var bigintTime = await BenchmarkQuery("espocrm_migration", transformedQuery);

        var improvement = bigintTime > 0 ? ((originalTime - bigintTime) / originalTime * 100) : 0;

        Console.WriteLine($"  VARCHAR (original): {originalTime:F2}s | BIGINT: {bigintTime:F2}s | Improvement: {improvement:F1}%\n");

        results.Add($"Query {i + 1}: VARCHAR {originalTime:F2}s → BIGINT {bigintTime:F2}s ({improvement:F1}% improvement)");
    }

    // Save results
    var reportPath = Path.Combine(outputPath, "benchmark_results.txt");
    await File.WriteAllLinesAsync(reportPath, results);

    Console.WriteLine($"✓ Benchmark complete - results saved to {reportPath}\n");

    return 0;
}

async Task<double> BenchmarkQuery(string database, string query)
{
    using var conn = new MySqlConnection(connectionString.Replace("Database=espocrm", $"Database={database}"));
    await conn.OpenAsync();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var cmd = new MySqlCommand(query, conn);
    cmd.CommandTimeout = 120;

    try
    {
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { } // Consume all rows
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }
    catch
    {
        return -1; // Query failed
    }
}

async Task<int> RunAll()
{
    Console.WriteLine("\n=== Running All Stages ===\n");

    Console.WriteLine("Stage 1: Generate ID mapping SQL...");
    if (await Stage1_GenerateMapping() != 0) return 1;

    Console.WriteLine("\nStage 2: Schema migration...");
    if (await Stage2_SchemaMigration() != 0) return 1;

    Console.WriteLine("\nStage 3: Data dumps...");
    if (await Stage3_DumpData() != 0) return 1;

    Console.WriteLine("\nStage 4: Transform dumps...");
    if (await Stage4_TransformDumps() != 0) return 1;

    Console.WriteLine("\nStage 5: Import data...");
    if (await Stage5_ImportData() != 0) return 1;

    Console.WriteLine("\n✓ All Stages Complete!\n");

    return 0;
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
