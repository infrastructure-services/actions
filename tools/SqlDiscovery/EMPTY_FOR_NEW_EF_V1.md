# EMPTY_FOR_NEW_EF taxonomy V1

Approved scope: NEW_EF is the first declared EF management entry on a physically
preprovisioned, existing database. NEW + NOT_FOUND remains
BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING. This contract authorizes only a later
read-only classification stage; it does not provision, apply or certify.

## Exact standard schema list

Version 1 permits empty containers named exactly:

`dbo`, `guest`, `sys`, `INFORMATION_SCHEMA`, `db_accessadmin`,
`db_backupoperator`, `db_datareader`, `db_datawriter`, `db_ddladmin`,
`db_denydatareader`, `db_denydatawriter`, `db_owner`, `db_securityadmin`.

The four built-in schemas and nine shipped fixed-role schemas are documented by
Microsoft: [principals](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/principals-database-engine)
and [ownership and user-schema separation](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/ownership-and-user-schema-separation).
Matching is binary and exact, independent of database collation. Owners and
prefixes grant no exemption. User objects under any permitted schema still block.
Custom schemas, including empty `cicd`, block.

## Reproducible, disjoint counts

`businessObjectCount` retains its historical predicate: all `sys.objects` rows
with `is_ms_shipped=0`, excluding the object named `dbo.__EFMigrationsHistory`
and objects whose parent is that history table. This includes tables (even empty),
views, procedures, functions, sequences, synonyms, DML triggers, constraints and
other user-defined schema-scoped objects. Names never exempt tooling.

The parent exclusion is made NULL-safe: when the history table does not exist,
all non-system objects remain countable. The former `NOT(parent_id = OBJECT_ID)`
predicate evaluated UNKNOWN for every row when OBJECT_ID was NULL. Correcting
that defect preserves the intended count domain and prevents false emptiness.

`technicalObjectCount` is the sum of these eight complementary category counts:

| Category | Catalog / inclusion |
|---|---|
| customSchemas | sys.schemas outside the exact versioned list |
| userDefinedTypes | sys.types with is_user_defined=1; exclude table types whose type_table_object_id is already in the business set |
| databaseTriggers | sys.triggers with parent_class=0 and is_ms_shipped=0, excluding IDs already in the business set |
| partitionFunctions | all sys.partition_functions rows |
| partitionSchemes | all sys.partition_schemes rows |
| userAssemblies | sys.assemblies with is_user_defined=1 |
| xmlSchemaCollections | sys.xml_schema_collections with xml_collection_id>0 |
| fullTextCatalogs | all sys.fulltext_catalogs rows |

Assemblies, XML collections and full-text catalogs are already inspected by the
repository's V1 SQL discovery; no infrastructure or security inventory is added.
Indexes and constraints need no independent complementary count: their owning
user table/view already prevents empty eligibility. System flags identify ignored
system objects; no blanket `cicd`, owner or tooling exclusion is used.

## Evidence and coverage

Physical raw evidence adds an optional `taxonomy` property:

```json
{
  "status": "OBSERVED",
  "businessObjectCount": 0,
  "technicalObjectCount": 0,
  "taxonomy": {
    "version": 1,
    "coverage": "COMPLETE",
    "counts": {
      "customSchemas": 0, "userDefinedTypes": 0, "databaseTriggers": 0,
      "partitionFunctions": 0, "partitionSchemes": 0, "userAssemblies": 0,
      "xmlSchemaCollections": 0, "fullTextCatalogs": 0
    }
  }
}
```

All counts are non-negative safe integers. Exact keys and the aggregate sum are
validated. COMPLETE requires every category and both aggregate counts. PARTIAL
contains only version/coverage, never an accredited technical count. Missing
taxonomy is historical evidence, not V1 support; unknown versions are rejected.
The real transport emits COMPLETE only after one SELECT batch returns exactly one
non-null, correctly shaped row, with VIEW DEFINITION revalidated on that same
connection. Permission failure, incomplete rows and overflow produce Partial with
no counts and blocked source projection. Read errors/timeouts/cancellation retain
their existing error states. No permission grants or retries are added.

The producer deep-copies taxonomy; adapter version 2 validates it and only
normalizes both zero counts to EMPTY when COMPLETE V1 is present. Historical
positive counts still normalize to POPULATED/TECHNICAL_ONLY; historical zeros
normalize to UNKNOWN. Pure V2 stays unchanged: its normalized EMPTY input is a
fact supplied by the adapter, not a raw count interpreted by the classifier.

## History, identity and composition

NEW_EF additionally requires observed history ABSENT after a valid target
connection and metadata inspection. EMPTY, PRESENT, UNKNOWN, ERROR and
NOT_ATTEMPTED cannot substitute. Any object impersonating the excluded history
table name is detected by history lookup, even when it is not a user table, and
fails closed. Positive physical emptiness alone never grants NEW_EF.

Governed target identity, repository and onboarding contracts are unchanged.
Schema Capture is complementary and can detect contradictions; its existing
`cicd`/owner filters cannot establish this taxonomy's completeness.

The legacy composition observations retain their existing count-derived vocabulary;
the adapter remains the authoritative normalization boundary. No historical EMPTY
claim can override the adapter's UNKNOWN result when taxonomy is missing.

`NEW_EF_BOOTSTRAP_REGISTRY_CERTIFICATION_GAP` remains a separate increment:
governed composition currently associates TARGET_REGISTERED with
BASELINE_REQUIRED/CERTIFIED. Controlled CERTIFIED fixtures prove taxonomy
composition only, not an uncertified NEW bootstrap. Registration and emptiness
never imply certification.

Lookup's offline-database visibility limitation remains recorded and unchanged;
NOT_FOUND is blocking. All evidence here is local/static/synthetic, not real SQL
or Runtime Gate B validation.
