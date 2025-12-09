# EspoCRM Benchmark Query Analysis

## Overview

Analysis of 14 slow queries identified from production slow query log. Each query is analyzed with EXPLAIN output, performance metrics, and optimization recommendations.

---

## Query 01 - FULLTEXT Search on Email

**Original Time (VARCHAR):** 15.36s
**BIGINT Time (cold cache):** >30s (timed out)
**BIGINT Time (warm cache):** 15.75s

### Query Pattern
```sql
SELECT ... FROM email
LEFT JOIN email_user ON ... AND user_id = 173730071406712634
WHERE email.id IN (
  SELECT email.id FROM email
  LEFT JOIN entity_team ON ...
  WHERE (team_id IN (...) OR user_id = ...)
)
AND (from_email_address_id IN (...) OR (status = 'Sent' AND created_by_id = ...))
AND MATCH (name, body_plain, from_string) AGAINST ('"companies house"' IN BOOLEAN MODE)
ORDER BY date_sent DESC, id DESC
LIMIT 41
```

### EXPLAIN Output

| Table | Type | Key | Rows | Filtered | Extra |
|-------|------|-----|------|----------|-------|
| email | index_merge | IDX_FROM_EMAIL_ADDRESS_ID, IDX_CREATED_BY_ID | 6,638 | 4.50% | Using union; **Using filesort** |
| emailUserInbox | eq_ref | UNIQ_EMAIL_ID_USER_ID | 1 | 50.00% | Using where |
| fromEmailAddress | eq_ref | PRIMARY | 1 | 100.00% | Using where |
| email | eq_ref | PRIMARY | 1 | 10.00% | Using where |
| entityTeam | ref | IDX_ENTITY_ID | 1 | 100.00% | Using where |
| emailUserInbox | eq_ref | UNIQ_EMAIL_ID_USER_ID | 1 | 100.00% | FirstMatch |

### Performance Issues

**1. Using filesort ❌**
- Expensive sorting on `ORDER BY date_sent DESC, id DESC`
- No index covers the sort order
- Sorting 6,638 rows with 4.50% filter = ~298 rows sorted
- **Impact:** Major performance bottleneck

**2. Index merge (union) ⚠️**
- Using two separate indexes: `IDX_FROM_EMAIL_ADDRESS_ID` + `IDX_CREATED_BY_ID`
- Index union is slower than a single composite index
- Indicates no single index satisfies the WHERE clause efficiently
- **Impact:** Suboptimal index selection

**3. FULLTEXT index NOT driving query ❌**
- MATCH clause exists but not used as primary access path
- MySQL chose index_merge over FULLTEXT
- FULLTEXT likely evaluated after initial row filtering
- **Impact:** FULLTEXT index underutilized

**4. Low selectivity ⚠️**
- Only 4.50% of 6,638 rows match all filters
- Examining ~6,600 rows to return 41 results
- **Impact:** Processing many unnecessary rows

### Optimization Recommendations

**Recommendation 1: Composite Index for User + Date Sorting**
```sql
CREATE INDEX IDX_ASSIGNED_DATE_STATUS ON email (
  assigned_user_id,
  status,
  date_sent DESC,
  id DESC
) WHERE deleted = 0;
```
**Expected Impact:**
- Eliminates filesort (direct index scan for sorting)
- Supports user_id + status filters
- Covers ORDER BY clause completely
- **Estimated improvement:** 40-60% faster

**Recommendation 2: Covering Index for Date-based Access**
```sql
CREATE INDEX IDX_DATE_COVERING ON email (
  date_sent DESC,
  id DESC,
  status,
  deleted,
  assigned_user_id,
  from_email_address_id,
  created_by_id
);
```
**Expected Impact:**
- Covers sort order
- Includes frequently filtered columns
- Reduces table lookups
- **Estimated improvement:** 30-50% faster

**Recommendation 3: Rewrite Query to Use FULLTEXT First**
If FULLTEXT search is highly selective, rewrite query to:
```sql
SELECT ... FROM (
  SELECT id FROM email
  WHERE MATCH (name, body_plain, from_string) AGAINST ('"companies house"' IN BOOLEAN MODE)
  AND deleted = 0
) AS ft_results
JOIN email ON email.id = ft_results.id
WHERE ...other conditions...
ORDER BY date_sent DESC, id DESC
LIMIT 41
```
**Expected Impact:**
- FULLTEXT becomes primary filter
- Smaller result set for subsequent joins
- **Estimated improvement:** Variable (depends on FULLTEXT selectivity)

### Testing Results

**Before OPTIMIZE TABLE:**
- Cold cache: >30s (timeout)
- Warm cache: 15.75s

**After OPTIMIZE TABLE:**
- ⏳ Pending completion

### Next Steps

1. ⏳ Complete OPTIMIZE TABLE
2. ⏳ Re-run query and measure performance impact
3. ⏳ Test recommended index additions
4. ⏳ Compare performance with/without new indexes

---

## Query 02 - [Pending Analysis]

**Original Time (VARCHAR):** 15.42s

Status: ⏳ Awaiting analysis

---

## Query 03 - [Pending Analysis]

**Original Time (VARCHAR):** 10.84s
**BIGINT Time:** 0.00s
**Improvement:** 100%

**Note:** This query showed 100% improvement - worth analyzing to understand what made it so much faster.

Status: ⏳ Awaiting analysis

---

## Query 04 - [Pending Analysis]

**Original Time (VARCHAR):** 10.99s
**BIGINT Time:** 0.16s
**Improvement:** 98.5%

**Note:** Excellent improvement - analyze to identify optimization pattern.

Status: ⏳ Awaiting analysis

---

## Queries 05-14

Status: ⏳ Pending EXPLAIN analysis

---

## Summary Statistics

**Total Queries Analyzed:** 1/14
**Queries with >90% improvement:** 2 (Query 03, Query 04)
**Queries with regression:** 2 (Query 12: -643%, Query 13: -7%)
**Queries failed/timeout:** 7

---

*Document will be updated as each benchmark query is analyzed.*
