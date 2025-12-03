# EspoCRM ID Migration Tool

Migrates EspoCRM database from VARCHAR(17) IDs to BIGINT UNSIGNED using UUID_SHORT().

## Prerequisites

- .NET 9.0 SDK
- MySQL client tools (mysql, mysqldump)
- Database access:
  - Read: espocrm database
  - Full: espocrm_migration database

## Setup

1. Clone repository
2. Copy `MigrationTool/appsettings.template.json` to `MigrationTool/appsettings.json`
3. Update connection string and output path in `appsettings.json`
4. Build: `dotnet build`

## Usage

```bash
cd MigrationTool
dotnet run
```

Select stage to run (1-5).

## Migration Stages

### Stage 1: ID Mapping
Generates SQL to populate id_mapping table with ~53M mappings.

**Output:** `01_create_id_mapping.sql`

**Execute:** `mysql -u espocrm_migration -p < 01_create_id_mapping.sql`

**Monitor:** `mysql -u espocrm_migration -p -e 'SHOW PROCESSLIST'`

### Stage 2: Schema Migration
Dumps and transforms schema (varchar(17) → bigint).

**Output:** `02_schema_migration.sql`

**Execute:** `mysql -u espocrm_migration -p espocrm_migration < 02_schema_migration.sql`

### Stage 3: Data Dumps
Dumps all 818 tables (7 large individually, 811 in batch).

**Output:** 8 SQL dump files (`03_*.sql`)

### Stage 4: Transform Dumps
Transforms dumps using id_mapping (replaces varchar IDs with bigint).

**Output:** 8 transformed SQL files (`04_*.transformed.sql`)

### Stage 5: Run All
Executes all stages sequentially.

## Server Deployment

On Linux server:
```bash
git clone <repo>
cd espocrm-id-migration
dotnet restore
dotnet build
cd MigrationTool
cp appsettings.template.json appsettings.json
# Edit appsettings.json with credentials
dotnet run
```

## Configuration

`appsettings.json`:
```json
{
  "ConnectionStrings": {
    "StagingDatabase": "Server=localhost;Port=3306;Database=espocrm;Uid=USER;Pwd=PASSWORD;"
  },
  "OutputPath": "/mnt/HC_Volume_101891352/migration"
}
```
