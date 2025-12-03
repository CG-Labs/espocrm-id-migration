# EspoCRM Migration Strategy - Final Approach

## Overview
Migrate 818 tables from varchar(17) IDs to bigint using separate database approach.

## Stage 1: Generate ID Mapping

**Input:** espocrm database (325 tables with varchar(17) IDs)

**Process:**
- Query information_schema to identify tables with varchar(17) ID columns
- Generate SQL file that creates espocrm_migration.id_mapping table
- Uses dynamic SQL: information_schema builds INSERT statements
- Each INSERT: `INSERT INTO espocrm_migration.id_mapping SELECT id, UUID_SHORT() FROM espocrm.table_name`
- Executes on MySQL server (no data transfer, all server-side)

**Output:**
- `migration-output/01_create_id_mapping.sql`
- Execution: `mysql -h HOST -u espocrm_migration -p < file.sql`
- Monitor: `SHOW PROCESSLIST` until idle
- Result: espocrm_migration.id_mapping table with ~53M old_id→new_id mappings

---

## Stage 2: Schema Migration

**Input:** espocrm database (all 818 tables)

**Process:**
- Execute mysqldump to export complete schema (no data): `mysqldump --no-data espocrm`
- .NET loads entire schema file into memory
- Apply regex transformation: `` `(id|[a-z_]+_id)` varchar(17)` `` → `` `$1` bigint unsigned ``
- Write transformed schema to output file

**Output:**
- `migration-output/02_schema_migration.sql`
- Import: `mysql -h HOST -u espocrm_migration -p espocrm_migration < 02_schema_migration.sql`
- Result: espocrm_migration database with all 818 tables (empty, bigint schema)

---

## Stage 3: Data Dumps

**Input:** espocrm database (all 818 tables)

**Process:**
- Execute mysqldump for data (--no-create-info --complete-insert --skip-extended-insert)
- **7 individual dumps** for largest tables:
  - app_log_record (30.5M rows)
  - action_history_record (7.6M rows)
  - attachment (3.7M rows)
  - note (3.5M rows)
  - email_email_account (2.3M rows)
  - entity_user (1.9M rows)
  - email (1.9M rows)
- **1 batch dump** for remaining 811 tables

**Output:**
- `migration-output/03_app_log_record.sql`
- `migration-output/03_action_history_record.sql`
- `migration-output/03_attachment.sql`
- `migration-output/03_note.sql`
- `migration-output/03_email_email_account.sql`
- `migration-output/03_entity_user.sql`
- `migration-output/03_email.sql`
- `migration-output/03_batch_tables.sql`
- Total: 8 dump files

---

## Stage 4: Transform Dumps

**Input:**
- 8 dump files from Stage 3
- id_mapping dictionary (loaded into memory from espocrm_migration.id_mapping table)

**Process:**
For each of 8 dump files:
1. Query espocrm_migration.id_mapping → load into Dictionary<string, long> (one-time)
2. Count total lines in dump file
3. Stream read dump file line by line
4. For each line:
   - Regex find all varchar(17) IDs: `'([0-9a-f]{17})'`
   - Replace with bigint from dictionary: `mapping[old_id]`
   - Write transformed line to output
   - Update progress: `[current_line / total_lines]`
5. Close streams

**Output:**
- `migration-output/04_app_log_record.transformed.sql`
- `migration-output/04_action_history_record.transformed.sql`
- `migration-output/04_attachment.transformed.sql`
- `migration-output/04_note.transformed.sql`
- `migration-output/04_email_email_account.transformed.sql`
- `migration-output/04_entity_user.transformed.sql`
- `migration-output/04_email.transformed.sql`
- `migration-output/04_batch_tables.transformed.sql`
- Total: 8 transformed dump files ready for import

---

## Stage 5: Manual Import & Validation

**You handle:**
- Import transformed dumps: `mysql -h HOST -u espocrm_migration -p espocrm_migration < file.transformed.sql`
- Verify row counts match
- Test application against espocrm_migration
- Cutover when ready

---

## Database Configuration

- **Source:** espocrm (read-only during migration)
- **Target:** espocrm_migration (full access for espocrm_migration user)
- **User:** espocrm_migration / 26bdb99a4e9eed595920
- **Permissions:** SELECT on espocrm, ALL on espocrm_migration

---

## Questions for Resolution

1. Stage 3: Should dumps run in parallel or sequential?
2. Stage 4: Load entire mapping dictionary once (53M entries), or reload per file?
3. Any stages need different approach?
