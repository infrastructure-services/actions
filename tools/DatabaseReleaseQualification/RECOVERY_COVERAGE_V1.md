# Qualification recovery coverage V1

Status: local contract and synthetic/fake implementation. No SQL runtime adapter,
real rehearsal, Registry changes, global Schema Capture changes or execution authorization.
Legacy Artifact & Safety V1 remains a separate, approved, unimplemented increment.

## Rule and evidence

Recovery class and recovery coverage are independent. `RehearsalResult.CanProceed`
requires QUALIFIED, valid structure/data status, FullReversible, rollback/reapply
certification, and complete versioned coverage. Missing coverage fails closed.
SECURITY mismatch has its own blocker and leaves capability UNKNOWN; it is never
converted to SCHEMA_ONLY or RESTORE_REQUIRED. Existing DATA class mappings remain.

| Dimension | Requirement | Evidence | Equivalence |
|---|---|---|---|
| STRUCTURE | Always for rehearsal | Existing four canonical snapshots; all must have complete structural coverage | PRE/PRE2 and POST1/POST2 |
| DATA | Existing forward data mutation/potential data loss or rollback data mutation rule | Existing IDataRollbackValidationContract; complementary IDataReapplyValidationContract for POST1/POST2 | Valid rollback and valid reapply evidence under those domain contracts |
| SECURITY | Any supported structural mutation or supported permission statement | Scoped explicit security snapshots at all four phases | Canonical equality PRE/PRE2 and POST1/POST2 |

An absent required provider, partial/insufficient/error evidence, unknown impact,
unresolved target, identity mismatch, or changed impact scope stops before the next
mutating step. A mismatch is evidence of non-equivalence, not an approval request.
NOT_REQUIRED is produced only from analysis, never supplied by a release manifest.
`IDataRollbackValidationContract` retains PRE capture and rollback validity semantics.
Its Valid result alone does not claim POST1/POST2 data equivalence: the companion is
required before reapply can occur after a successful DATA rollback validation.
No automatic full-data snapshots or new recovery class are introduced.

The coverage report binds exact forward/rollback hashes, scope hash, security phase
hashes and canonical security documents. Phase labels are outside state hashes so
equivalent states across phases compare equal. The package writer rejects coverage
bound to different script bytes; attestations retain the full report. Consumers must
check coverage/CanProceed, not just the nominal recovery class. This contract does not
change the independent certification or deployment-authorization interfaces.

## Security source boundary

`IRecoverySecurityCatalogReader.ReadAsync` is a read-only, fakeable source boundary.
`ReadOnlyRecoverySecurityProvider` validates its output. There is no SQL executor,
connection input, retry, or runtime catalog adapter in this increment. Runtime Gate C
must supply a reviewed read-only adapter bound to the same rehearsal database.
An adapter must not mark visibility COMPLETE merely because a query succeeded or
returned no rows. Missing catalog visibility, unresolved names, unsupported catalog
classes or ambiguous resolution must return incomplete/error evidence.

The scope is the union of affected securables in both scripts, fixed before PRE.
Every requested securable must have one state record; a missing object is represented
explicitly with Exists=false, no explicit owner and no permission rows. Its containing
schema and owner must still be resolved. Omitted records are invalid evidence.
Capture all explicit permissions of each affected securable, not only those named by
the script: DROP/recreate can remove permissions absent from the script. Do not scan
all database object permissions when the change has a bounded scope.

Required catalog projection for a future source:

| Catalog | Selection and projected state |
|---|---|
| sys.database_permissions | Class 0 for DATABASE; class 3 for named SCHEMA; class 1 for named OBJECT, including every column exception. Keep permission_name, state G/W/D/R, grantee and grantor identities. Never discard R or grantor. |
| sys.objects / sys.schemas | Resolve qualified names to current IDs only for joins. Preserve existence, explicit object owner vs inherited schema owner, and schema owner. No object_id/schema_id in canonical identity. |
| sys.columns | Resolve permission minor_id to column name. Unresolvable nonzero IDs are incomplete, never silently treated as object-level. |
| sys.database_principals | Resolve requested principals plus all permission parties and ownership closure. Keep name, type, SID, authentication type, default schema, owner name, fixed-role flag. Exclude numeric IDs and timestamps. |

Joins must use the permission class as well as major_id; numeric IDs from distinct
classes can collide. Resolve scope parameters against the database collation, reject
ambiguous/colliding scope entries, then return the requested logical scope consistently.
Principal SID is retained to detect same-name recreation; hex casing is normalized.
Lists are sorted ordinally; names and owner identities are otherwise not rewritten.
Database identity must be stable across all phases. Runtime source binding to the
governed target remains mandatory outside this synthetic contract.

This models explicit security, not effective permissions, server logins, role membership
closure, external identity providers, or all SQL Server state. The analyzer does not
enable changes to those excluded domains. Database-level scope means the database's
explicit permission rows, not every object's permissions. Object changes include the
relevant schema-owner identity; they do not imply mutation of all schema permissions.

## Supported impact and conservative boundaries

The existing analyzer remains authoritative. Structural operations require SECURITY
on their resolved objects. Trigger changes with a second unproven securable identity
are blocked for impact completeness. Partial or unsupported analysis cannot qualify.

GRANT/DENY/REVOKE support is bounded to named database principals and statically
resolved OBJECT::schema.name or SCHEMA::schema targets, or an allowlisted unqualified
database permission (CONNECT, CREATE TABLE/VIEW, VIEW DEFINITION, CONTROL).
Object/schema permissions are SELECT/INSERT/UPDATE/DELETE/REFERENCES/EXECUTE,
VIEW DEFINITION, ALTER, CONTROL and TAKE OWNERSHIP. Column permissions are object-only.
AS and WITH GRANT OPTION remain represented through requested principals and catalog
grantor/state evidence. CASCADE is unsupported because it can widen affected scope.
Server permissions, login/credential/role mutations, cross-database targets, dynamic
SQL and unresolved forms remain unsupported. This is impact/qualification analysis;
the complete artifact safety policy is still the next increment.

## Sources and limits

SQL Server catalog semantics were checked against Microsoft documentation:

- [sys.database_permissions](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-permissions-transact-sql?view=sql-server-ver17)
- [sys.database_principals](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-principals-transact-sql?view=sql-server-ver17)
- [sys.objects](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-objects-transact-sql?view=sql-server-ver17)

The local tests use explicit fake evidence; they do not establish runtime visibility,
transport security, catalog adapter correctness, or real rollback success. Baseline is
governed accepted state; PRE is an observation. Neither this report nor a manifest
certifies a baseline. TEST remains the future empirical qualification laboratory;
same-payload promotion and human execution authorization retain their separate gates.
