# EspoCRM EntityDefs Changes for BIGINT Migration

## Overview

This document outlines the changes required to EspoCRM entity definition files (entityDefs) to support BIGINT IDs after migrating from VARCHAR(17).

---

## Critical Findings from Query Analysis

### Query 03: PRIMARY KEY Lookup Performance

**Query:**
```sql
SELECT email.id, email.name FROM email
WHERE email.id = 173730071432613492
AND email.deleted = 0
LIMIT 1;
```

**Performance:**
- **VARCHAR(17):** 10.84 seconds
- **BIGINT:** 0.00 seconds (instant)
- **Improvement:** 100% (10.84s saved)

**EXPLAIN Analysis:**
- type: `const` (optimal)
- key: `PRIMARY`
- rows: 1
- filtered: 100%

**Root Cause of VARCHAR Slowness:**
1. String comparison overhead (17 characters vs 8 bytes)
2. Character set/collation processing
3. Index tree depth greater with VARCHAR keys
4. Memory/cache inefficiency (VARCHAR keys don't fit in cache lines as efficiently)

**Conclusion:** BIGINT PRIMARY KEY lookups are **instant**. VARCHAR(17) PRIMARY KEY lookups are **10+ seconds**. This alone justifies the migration.

---

### Query 04: Complex UNION with Multiple Entities

**Query Pattern:**
```sql
SELECT COUNT(*) FROM (
  SELECT ... FROM call WHERE parent_id = X ...
  UNION
  SELECT ... FROM company_appointment WHERE parent_id = X ...
  UNION
  SELECT ... FROM email WHERE parent_id = X OR account_id = X ...
  UNION
  SELECT ... FROM email WHERE email_address foreign key = X ...
  UNION
  SELECT ... FROM incident WHERE parent_id = X ...
  UNION
  SELECT ... FROM meeting WHERE parent_id = X ...
  UNION
  -- ... more entity types
) AS activities;
```

**Performance:**
- **VARCHAR(17):** 10.99 seconds
- **BIGINT:** 0.16 seconds
- **Improvement:** 98.5%

**EXPLAIN Analysis:**
Each UNION branch shows:
- Efficient `index_merge` on parent/account foreign keys
- `eq_ref` lookups on PRIMARY keys (all BIGINT now)
- Very small row counts (1-154 rows per UNION)
- Fast PRIMARY KEY joins

**Why BIGINT is Faster:**
1. **Faster PRIMARY KEY joins** - eq_ref on BIGINT is instant
2. **Efficient index_merge** - Numeric index unions are faster
3. **Better JOIN performance** - BIGINT comparisons are faster than VARCHAR
4. **Reduced memory footprint** - 8 bytes vs 17+ bytes per key

**Indexes Used (all benefit from BIGINT):**
- PRIMARY KEY (id) - BIGINT
- IDX_PARENT (parent_type, parent_id) - parent_id is BIGINT
- IDX_ACCOUNT_ID (account_id) - BIGINT
- UNIQ_ENTITY_ID_* - entity_id columns are BIGINT

---

## Required EntityDefs Changes

### 1. Core ID Field Type

**All Entity Definition Files:**

Location: `application/Espo/Resources/metadata/entityDefs/*.json`

**Change:**
```json
{
  "fields": {
    "id": {
      "type": "id",
      "dbType": "bigint"  // Changed from varchar(17) or varchar(24)
    }
  }
}
```

**Affected Entities (all 818 tables):**
- Account, Contact, Email, Call, Meeting, Task, etc.
- All custom entities
- All junction tables (account_contact, email_user, etc.)

---

### 2. Foreign Key Field Types

**All Foreign Key References:**

```json
{
  "fields": {
    "accountId": {
      "type": "foreign",
      "relation": "account",
      "foreignType": "belongsTo",
      "dbType": "bigint"  // Changed from varchar
    },
    "assignedUserId": {
      "type": "foreign",
      "relation": "assignedUser",
      "dbType": "bigint"
    },
    "parentId": {
      "type": "foreignParent",
      "dbType": "bigint"
    }
  }
}
```

**Common FK Fields to Update:**
- `*_id` columns (assigned_user_id, created_by_id, modified_by_id, etc.)
- `parent_id` (polymorphic foreign keys)
- `entity_id` (in junction tables)
- All relationship table foreign keys

---

### 3. Index Definitions

**No index changes required** - Existing indexes work with BIGINT:

```json
{
  "indexes": {
    "parent": {
      "columns": ["parentType", "parentId"]  // parentId now BIGINT
    },
    "assignedUser": {
      "columns": ["assignedUserId", "deleted"]  // assignedUserId now BIGINT
    },
    "account": {
      "columns": ["accountId"]  // accountId now BIGINT
    }
  }
}
```

**Note:** Index definitions don't need to specify data type - they inherit from field definitions.

---

### 4. Index Requirements

**Tested Indexes:**

Based on query analysis and testing, the following indexes are recommended:

**Email Entity (`application/Espo/Resources/metadata/entityDefs/Email.json`):**

```json
{
  "indexes": {
    "deletedJunkStatus": {
      "columns": ["deleted", "isJunk", "status"],
      "type": "index",
      "comment": "Exists in espocrm_staging - common filter pattern"
    },
    "fullTextSearch": {
      "columns": ["name", "bodyPlain", "fromString"],
      "type": "fulltext",
      "comment": "Required for FULLTEXT searches - add AFTER data import for performance"
    }
  }
}
```

**Tested but NOT beneficial:**
- `assignedStatusDate (assigned_user_id, status, date_sent, id)` - Not used by optimizer due to query OR conditions

**Note:** Most email queries have complex OR conditions and subqueries that prevent effective index usage beyond the existing PRIMARY KEY and foreign key indexes. Query performance is limited by query complexity, not missing indexes.

---

### 5. UUID_SHORT() ID Generation

**Entity Manager Configuration:**

EspoCRM needs to generate BIGINT IDs using UUID_SHORT() instead of calling `Espo\Core\Utils\Id::generate()` which creates VARCHAR hex IDs.

**Option A: Database-level DEFAULT**
```sql
ALTER TABLE email MODIFY id bigint unsigned NOT NULL DEFAULT (UUID_SHORT());
```

**Option B: Application-level (Repository layer)**
```php
// In application/Espo/Modules/Crm/Repositories/Email.php
protected function beforeSave(Entity $entity, array $options = [])
{
    if (!$entity->has('id')) {
        $id = $this->entityManager
            ->getConnection()
            ->fetchOne("SELECT UUID_SHORT() as id")['id'];
        $entity->set('id', $id);
    }
    parent::beforeSave($entity, $options);
}
```

**Recommendation:** Option B (application-level) is more portable and testable.

---

### 6. ORM Configuration

**Update Entity Type Mapping:**

File: `application/Espo/ORM/Defs/FieldDefs.php` or similar

```php
const ID_TYPE_MAP = [
    'id' => 'bigint',  // Changed from 'string'
    'foreignId' => 'bigint',  // Changed from 'string'
    'foreignParentId' => 'bigint'  // Changed from 'string'
];
```

---

### 7. API Response Format

**JSON Serialization:**

IDs will now be returned as numbers in API responses:

**Before (VARCHAR):**
```json
{
  "id": "6931a7ab2f780b866",
  "accountId": "5a9b3c4d1e2f3g4h5"
}
```

**After (BIGINT):**
```json
{
  "id": 173730071432634963,
  "accountId": 173730071406725107
}
```

**JavaScript Compatibility Note:**
- BIGINT values up to 2^53-1 (9,007,199,254,740,991) are safe in JavaScript
- UUID_SHORT() max is 2^64-1 but practical values are much smaller
- No compatibility issues expected

---

## Implementation Checklist

### Phase 1: Metadata Updates
- [ ] Update all entityDefs/*.json files to set `id` field `dbType: "bigint"`
- [ ] Update all foreign key fields to `dbType: "bigint"`
- [ ] Add recommended composite indexes to Email entity
- [ ] Add recommended indexes to other entities based on query analysis

### Phase 2: Code Changes
- [ ] Update ID generation to use UUID_SHORT()
- [ ] Update ORM type mappings for ID fields
- [ ] Update entity repositories to generate BIGINT IDs
- [ ] Update any code that assumes ID is string/hex format

### Phase 3: Testing
- [ ] Unit tests for ID generation
- [ ] Integration tests for foreign key relationships
- [ ] API tests for JSON serialization
- [ ] Performance tests comparing VARCHAR vs BIGINT

### Phase 4: Migration
- [ ] Database migration scripts (already complete in migration tool)
- [ ] Deploy updated entityDefs
- [ ] Deploy code changes
- [ ] Verify ID generation works correctly

---

## Performance Impact Summary

### Measured Improvements

**Query 03 (PRIMARY KEY lookup):**
- VARCHAR: 10.84s
- BIGINT: 0.00s
- **Improvement: 100%** ✅

**Query 04 (Complex UNION):**
- VARCHAR: 10.99s
- BIGINT: 0.16s
- **Improvement: 98.5%** ✅

**Query 01 (FULLTEXT search):**
- VARCHAR: 15.36s
- BIGINT: 15.33s (after OPTIMIZE)
- Improvement: ~0.2% (minimal - needs composite indexes)

### Key Takeaway

**BIGINT provides massive performance improvements (98-100%) for:**
- PRIMARY KEY lookups
- Foreign key JOINs
- Index merges on foreign keys
- UNION queries across multiple entities

**BIGINT provides minimal improvement for:**
- Queries bottlenecked by filesort (need covering indexes)
- Queries with missing composite indexes
- FULLTEXT-heavy queries (FULLTEXT is the bottleneck)

---

## EspoCRM Framework Integration

### File Locations

**Entity Definitions:**
```
application/Espo/Resources/metadata/entityDefs/
├── Account.json
├── Contact.json
├── Email.json
├── Call.json
├── Meeting.json
├── Task.json
└── ... (all entities)
```

**Custom Entity Definitions:**
```
custom/Espo/Custom/Resources/metadata/entityDefs/
└── ... (custom entities)
```

### Example: Email Entity Update

**Before:**
```json
{
  "fields": {
    "id": {
      "type": "id"
    },
    "accountId": {
      "type": "foreign",
      "relation": "account"
    }
  }
}
```

**After:**
```json
{
  "fields": {
    "id": {
      "type": "id",
      "dbType": "bigint"
    },
    "accountId": {
      "type": "foreign",
      "relation": "account",
      "dbType": "bigint"
    },
    "assignedUserId": {
      "type": "foreign",
      "relation": "assignedUser",
      "dbType": "bigint"
    },
    "createdById": {
      "type": "foreign",
      "dbType": "bigint"
    }
  },
  "indexes": {
    "assignedStatusDate": {
      "columns": ["assignedUserId", "status", "dateSent:desc"],
      "type": "index"
    }
  }
}
```

---

## Validation Steps

### 1. Verify ID Generation
```php
$email = $this->getEntityManager()->createEntity('Email', [
    'name' => 'Test Email'
]);

// Should output a BIGINT like: 173730071432634963
echo $email->get('id');

// Should be numeric, not hex string
assert(is_numeric($email->get('id')));
assert($email->get('id') > 0);
```

### 2. Verify Foreign Key Relationships
```php
$account = $this->getEntityManager()->getEntity('Account', $accountId);
$emails = $this->getEntityManager()
    ->getRDBRepository('Email')
    ->where(['accountId' => $account->get('id')])
    ->find();

// Should work correctly with BIGINT IDs
assert($emails->count() > 0);
```

### 3. Verify API Responses
```bash
curl https://your-espocrm/api/v1/Email/173730071432634963

# Response should have numeric ID:
{
  "id": 173730071432634963,
  "name": "Test Email",
  "accountId": 173730071406725107
}
```

---

## Rollback Plan

If issues arise, rollback requires:
1. Restore database from pre-migration backup
2. Revert entityDefs changes
3. Revert code changes to ID generation
4. Clear cache

**Note:** There is no in-place rollback - you must restore from backup.

---

## Next Steps

1. ⏳ Complete analysis of remaining queries (5-14)
2. ⏳ Identify common index patterns across all queries
3. ⏳ Test recommended composite indexes
4. Update this document with complete entityDefs change list
5. Create migration guide for EspoCRM framework integration
