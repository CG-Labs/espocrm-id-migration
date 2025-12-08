-- Stage 0: Drop and recreate espocrm_migration database
-- This resets the migration database to start fresh

DROP DATABASE IF EXISTS espocrm_migration;
CREATE DATABASE espocrm_migration;

-- Schema will be imported separately from 02_schema_migration.sql
