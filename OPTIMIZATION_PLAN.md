# EspoCRM Database Optimization Plan

## Executive Summary

This document explores optimization strategies for the EspoCRM database after migrating from VARCHAR(17) to BIGINT IDs. We analyze table partitioning, index optimization, and architectural changes to improve query performance.

## Current State Analysis

### Database Statistics
- **Total Tables:** 818
- **Total Rows:** 73M+
- **Email Table:** ~2M rows (primary performance concern)
- **ID Type:** BIGINT (after migration from VARCHAR(17))

### Known Performance Issues
- **14 slow queries identified** (>10s execution time)
- **FULLTEXT index creation:** 50+ minutes on email table
- **Primary bottleneck:** Email table queries with FULLTEXT searches

### Current Email Table Structure
```sql
CREATE TABLE `email` (
  `id` bigint unsigned NOT NULL,
  `name` varchar(255),
  `body` mediumtext,           -- Large text column
  `body_plain` mediumtext,     -- Large text column
  `from_string` varchar(255),
  -- ... other columns
  PRIMARY KEY (`id`),
  FULLTEXT INDEX `IDX_SYSTEM_FULL_TEXT_SEARCH` (`name`, `body_plain`, `from_string`)
) ENGINE=InnoDB;
```

## Optimization Strategy 1: Separate Body Content Table

### Concept
Move large text columns (`body`, `body_plain`) to a separate table to:
1. Reduce main email table size
2. Improve query performance for non-body searches
3. Isolate FULLTEXT index to smaller, dedicated table

### Proposed Schema

```sql
-- Main email table (hot data, frequently queried)
CREATE TABLE `email` (
  `id` bigint unsigned NOT NULL,
  `name` varchar(255),
  `from_string` varchar(255),
  `date_sent` datetime,
  `status` varchar(50),
  `assigned_user_id` bigint unsigned,
  -- ... other metadata columns (NO body columns)
  PRIMARY KEY (`id`),
  KEY `IDX_DATE_SENT` (`date_sent`),
  KEY `IDX_STATUS` (`status`),
  KEY `IDX_ASSIGNED_USER_ID` (`assigned_user_id`)
) ENGINE=InnoDB;

-- Email body table (cold data, queried only when needed)
CREATE TABLE `email_body` (
  `email_id` bigint unsigned NOT NULL,
  `body` mediumtext,
  `body_plain` mediumtext,
  PRIMARY KEY (`email_id`),
  FULLTEXT INDEX `IDX_BODY_FULLTEXT` (`body_plain`),
  CONSTRAINT `FK_EMAIL_BODY` FOREIGN KEY (`email_id`) REFERENCES `email` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB;

-- Email metadata search table (for FULLTEXT on name/from)
CREATE TABLE `email_search` (
  `email_id` bigint unsigned NOT NULL,
  `name` varchar(255),
  `from_string` varchar(255),
  PRIMARY KEY (`email_id`),
  FULLTEXT INDEX `IDX_METADATA_FULLTEXT` (`name`, `from_string`),
  CONSTRAINT `FK_EMAIL_SEARCH` FOREIGN KEY (`email_id`) REFERENCES `email` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB;
```

### Performance Impact Analysis

**Benefits:**
- **Smaller email table** - Queries not needing body content scan fewer bytes
- **Faster table scans** - Main table row size reduced by ~80%
- **Targeted FULLTEXT** - Separate indexes for body vs metadata searches
- **Better caching** - Hot metadata stays in buffer pool longer
- **Faster imports** - Can import metadata first, bodies later in parallel

**Drawbacks:**
- **Application changes required** - EspoCRM code must be modified
- **JOIN overhead** - Queries needing body require JOIN
- **Migration complexity** - Need to split existing data

**Estimated Impact:**
- Non-body queries: **40-60% faster** (smaller table scans)
- Body-only FULLTEXT: **20-30% faster** (smaller FULLTEXT index)
- Queries needing both: **10-20% slower** (JOIN overhead)

### Implementation Difficulty
**HIGH** - Requires EspoCRM application code changes. Not recommended unless EspoCRM supports custom schema.

---

## Optimization Strategy 2: Table Partitioning

### Why BIGINT Enables Partitioning

VARCHAR(17) IDs were **unsuitable** for effective partitioning:
- Hash partitioning on VARCHAR is inefficient
- Range partitioning on hex strings doesn't distribute evenly
- UUID_SHORT() values are random, making range partitioning difficult

BIGINT IDs enable **better partitioning strategies**:
- Hash partitioning on numeric values is efficient
- Can partition by ID ranges if IDs are sequential
- Can partition by computed columns (date extracted from UUID_SHORT timestamp)

### Partitioning Strategy Analysis

#### Option A: HASH Partitioning by ID
```sql
ALTER TABLE email
PARTITION BY HASH(id)
PARTITIONS 16;
```

**When effective:**
- Queries filtering by specific email IDs
- Even distribution across partitions
- Good for parallel processing

**When ineffective:**
- Range queries (date ranges, status filters)
- FULLTEXT searches (must search all partitions)
- Most EspoCRM queries don't filter by specific ID

**Verdict:** ❌ **Not recommended** - Most queries would still scan all partitions

#### Option B: RANGE Partitioning by Date
```sql
ALTER TABLE email
PARTITION BY RANGE (YEAR(date_sent) * 100 + MONTH(date_sent)) (
  PARTITION p202301 VALUES LESS THAN (202302),
  PARTITION p202302 VALUES LESS THAN (202303),
  -- ... monthly partitions
  PARTITION p202512 VALUES LESS THAN (202513),
  PARTITION p_future VALUES LESS THAN MAXVALUE
);
```

**When effective:**
- Queries with date ranges (most email queries have date filters)
- Partition pruning eliminates old partitions from searches
- Easy to archive old partitions

**When ineffective:**
- Queries without date filters
- FULLTEXT searches without date bounds

**Verdict:** ❌ **NOT COMPATIBLE** - MySQL partitioned tables do not support FULLTEXT indexes

**Critical Limitation Discovered:**
- ERROR 1214: "The used table type doesn't support FULLTEXT indexes"
- InnoDB partitioned tables CANNOT have FULLTEXT indexes
- Email table requires FULLTEXT index for search functionality
- **Therefore, partitioning the email table is NOT VIABLE**

**Tested:** Attempted to create email_partitioned with RANGE partitioning by year
**Result:** Failed - incompatible with existing FULLTEXT index on (name, body_plain, from_string)

#### Option C: LIST Partitioning by Status
```sql
ALTER TABLE email
PARTITION BY LIST COLUMNS(status) (
  PARTITION p_archived VALUES IN ('Archived'),
  PARTITION p_sent VALUES IN ('Sent'),
  PARTITION p_draft VALUES IN ('Draft'),
  PARTITION p_active VALUES IN ('Sending', 'Pending', 'Failed')
);
```

**When effective:**
- Queries filtering by status (common in EspoCRM)
- Archive partition can be moved to slower storage

**When ineffective:**
- Queries scanning multiple statuses
- Limited number of statuses (only 4-5 partitions)

**Verdict:** ⚠️ **Limited benefit** - Too few distinct values

### Partitioning Effectiveness Criteria

To determine if partitioning will help, we need to analyze slow queries for:
1. **Partition pruning potential** - Do queries filter on partition key?
2. **Partition access patterns** - How many partitions would typical queries access?
3. **Data distribution** - Is data evenly distributed across partitions?

**Analysis pending FULLTEXT index completion - will run EXPLAIN on slow queries**

---

## Optimization Strategy 3: Index Improvements

### Current Index Analysis (Pending)

Once FULLTEXT index completes, we will analyze:
1. Missing indexes on frequently filtered columns
2. Composite index opportunities
3. Index column order optimization
4. Covering index opportunities

### Preliminary Index Candidates

Based on query patterns (to be verified with EXPLAIN):

```sql
-- Composite index for common email filters
ALTER TABLE email
ADD INDEX IDX_STATUS_DATE_DELETED (status, date_sent DESC, deleted);

-- Index for assigned user + date queries
ALTER TABLE email
ADD INDEX IDX_ASSIGNED_DATE (assigned_user_id, date_sent DESC, deleted);

-- Index for folder + status queries
ALTER TABLE email
ADD INDEX IDX_FOLDER_STATUS (group_folder_id, status, deleted);

-- Index for parent entity lookups
ALTER TABLE email
ADD INDEX IDX_PARENT (parent_type, parent_id, deleted);
```

**Analysis Status:** ⏳ Pending EXPLAIN execution on slow queries

---

## Slow Query Analysis

### Queries Identified from Slow Query Log

**14 slow queries** identified with execution times ranging from 10.84s to 37.17s on VARCHAR implementation.

### Benchmark Query Analysis

**14 slow queries** identified with execution times ranging from 10.84s to 37.17s on VARCHAR implementation.

**Analysis approach:**
1. Run EXPLAIN on each query to understand execution plan
2. Identify common patterns across queries
3. Determine if partitioning would benefit query patterns
4. Recommend indexes based on actual query behavior

**Detailed analysis:** See `BENCHMARK_ANALYSIS.md` for individual query breakdowns

**Status:** ⏳ 1/14 queries analyzed (Query 01 complete)

---

## Performance Testing Methodology

### Benchmark Process

1. ✅ Extract slow queries from production log
2. ✅ Transform VARCHAR IDs to BIGINT IDs
3. ⏳ Create FULLTEXT indexes (in progress)
4. ⏳ Run EXPLAIN on each query
5. ⏳ Execute queries and measure timing
6. ⏳ Compare VARCHAR vs BIGINT performance

### Metrics to Collect

For each optimization strategy:
- **Query execution time** - Before/after comparison
- **Rows examined** - From EXPLAIN output
- **Index usage** - Which indexes are utilized
- **Partition pruning** - Partitions accessed
- **Memory usage** - Buffer pool impact
- **Disk I/O** - Read operations required

---

## Recommendations (Preliminary)

### High Priority
1. ✅ **Move FULLTEXT index creation to post-import** - Implemented
2. ✅ **Analyze slow query EXPLAIN plans** - Queries 01, 03, 04 complete
3. ❌ **Table partitioning ruled out** - Incompatible with FULLTEXT indexes (MySQL limitation)

### Medium Priority
4. **Add composite indexes** - Based on common query patterns
5. **Review application query patterns** - Identify optimization opportunities at application level

### Low Priority
6. **Separate body table** - Only if application supports custom schema
7. **Archive old emails** - Move old data to separate archive tables/database

---

## Discovery Tasks

### Task 1: Identify Empty Tables

Query all 818 tables to find which have zero rows. Empty tables:
- May indicate unused features
- Can be excluded from migration/backups
- Help understand actual database usage patterns

**Query to execute:**
```sql
SELECT
    TABLE_NAME,
    TABLE_ROWS,
    ROUND((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024, 2) AS size_mb
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = 'espocrm_migration'
AND TABLE_ROWS = 0
ORDER BY size_mb DESC;
```

**Expected output:** List of empty tables with their allocated disk space

**Status:** ⏳ Pending execution

### Task 2: Table Size Distribution

Analyze table sizes to identify optimization targets:
```sql
SELECT
    TABLE_NAME,
    TABLE_ROWS,
    ROUND((DATA_LENGTH) / 1024 / 1024, 2) AS data_mb,
    ROUND((INDEX_LENGTH) / 1024 / 1024, 2) AS index_mb,
    ROUND((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024, 2) AS total_mb
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = 'espocrm_migration'
ORDER BY (DATA_LENGTH + INDEX_LENGTH) DESC
LIMIT 50;
```

**Status:** ⏳ Pending execution

---

## Next Steps

1. ⏳ Wait for FULLTEXT index creation to complete (~50 minutes so far)
2. ⏳ Execute discovery tasks (empty tables, size distribution)
3. ⏳ Run EXPLAIN on all 14 slow queries
4. ⏳ Analyze partition key effectiveness for each query
5. ⏳ Generate specific index recommendations with measured impact
6. ⏳ Create implementation plan for production deployment

---

## Current Status

- **ALTER TABLE Progress:** 3,036 seconds (50.6 minutes) - Still running
- **Replication:** Paused to save disk space
- **Disk Space:** 51GB free (91% used)
- **Code Changes:** ✅ Committed - FULLTEXT optimization for future runs

---

*Document will be updated with EXPLAIN analysis and specific recommendations once FULLTEXT index creation completes.*
