# SQL Server Schema Capture identity contract

`schema-capture` is an inspection-only component. Its SQL connection must belong to a dedicated **INSPECTION IDENTITY** and must never be resolved automatically from an Owner or deployment credential.

## Identity separation

| Identity purpose | Allowed use |
|---|---|
| INSPECTION IDENTITY | Schema capture, drift inspection, dependency inspection and read-only target preflight. |
| DEPLOYMENT IDENTITY | Future mutating releases. It is outside this action and must not be supplied to schema capture. |

The inspection identity must not be a member of `db_owner`, `sysadmin`, `db_ddladmin`, `securityadmin`, or another broad role capable of mutation.

## Minimum permission contract

Schema metadata requires database connectivity plus metadata visibility:

- `CONNECT`
- `VIEW DEFINITION`

Optional impact metrics may additionally require:

| SQL Server version | Optional metrics permissions |
|---|---|
| SQL Server 2019 or earlier | `VIEW DATABASE STATE` plus `VIEW DEFINITION` |
| SQL Server 2022 or later | `VIEW DATABASE PERFORMANCE STATE` plus `VIEW SECURITY DEFINITION` |

If optional metrics are not visible, structural capture may continue with `metricsAvailability=PARTIAL` or `UNAVAILABLE`. Missing structural metadata remains a failure.

This contract does not provision identities or permissions. Identity creation, Key Vault configuration and permission assignment are external onboarding operations.

## Primary routing and read-only protection

The reader sets `ApplicationIntent=ReadWrite` only to request the authoritative primary state. `ApplicationIntent=ReadWrite` does not grant write permissions.

Read-only protection is provided by all three layers:

1. a least-privilege SQL inspection identity;
2. a reader that issues only `SELECT` queries;
3. runtime and static guards that reject mutating SQL and non-query execution APIs.

The connection string enters the capture process only through `DB_CONNECTION`. It is never accepted as a CLI argument and must not be written to logs, summaries or artifacts.

## Database Registry evaluation

After two deterministic captures, the action may read a central `targets.json` and evaluate schema drift without reconnecting to SQL Server. The registry is input-only: its bytes are checked before and after evaluation, and runtime evidence is written exclusively below the schema-capture artifact directory.

Registry format V1 requires `registryFormatVersion=1`; missing or unknown versions fail closed. The workflow supplies repository, requested ref, actual checkout commit, logical file path, and the SHA256 of the exact registry file. The evaluator independently verifies the file hash and persists that shared `registryProvenance` in evaluation, baseline, and drift evidence.

`observedSchemaHash` is evidence from the current capture. `certifiedSchemaHash` is an independently approved value from the versioned registry. The action never copies, promotes or writes the observed value into the certified field. A `BASELINE_REQUIRED` target produces `baseline-candidate.json` with `candidateStatus=NOT_CERTIFIED` and a blocked eligibility gate.

Credential resolution remains intentionally outside Database Registry V1. A future `credentialRef` may replace the temporary workflow variable used by the pilot, but the registry evaluator neither reads nor stores connection data.
