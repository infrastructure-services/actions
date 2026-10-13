# EF release plan evidence V1 (A1)

`EfReleasePlannerV1` composes a frozen, versioned EF evidence manifest and performs
pure PRE and POST comparisons. This increment is in Actions only. It adds no CLI,
workflow, credential provider, SQL executor, approval grant or Registry writer.
Existing NEW_EF, EXISTING_EF pilot and LEGACY consumers remain unchanged.

**Evidence consistency is not authenticity, execution authority or certification.**
Every result and manifest has `ExecutionAuthorized = false`. Nothing here enables
real APPLY. A consistent synthetic fixture does not certify a real database.

## Reuse and boundaries

The composer reuses `ReleaseScript`, `ReleasePayloadBuilder`, `SchemaCanonicalizer`,
`SqlScriptAnalyzer`, `RiskEngine`, `RecoveryImpact`, `RehearsalResult` and
`RecoveryCoverageEvidence`. The tests exercise the existing `RehearsalEngine`
through an in-memory fake; production composition only consumes its evidence.
The exact-byte executors and the closed EF parser contracts are unchanged.

The corporate `continuous-deployment-migrations.yml` capabilities were considered:

| Capacity | Integration boundary after A1 |
| --- | --- |
| Secret resolution / Key Vault | Reuse correct corporate actions through a governed target, environment, endpoint and credential-purpose mapping. An Owner name is not permission evidence. Missing/ambiguous/inaccessible required secrets must block. |
| Connection validation | Acquisition must independently prove the endpoint, observed identity, incarnation and effective permissions. Keep inspection and execution credentials separate. |
| Migration identification | Reuse repository discovery and governed adoption metadata, including the project path, managed range and exact exclusions. Historical adoption revision is distinct from the current source revision. |
| Risk | Reuse AST, forward/PRE and rollback/POST analysis, metrics and MAX aggregation; a regex classification alone is insufficient. |
| Approval | Integrate independent effective protected-environment approval bound to the frozen plan; a reference supplied by the caller is insufficient. |
| Execution | Retain the existing exact-payload integrity mechanism. A1 introduces no parallel executor and does not substitute an unbound EF database update. |

`DB_GOVERNANCE_READ_TOKEN` remains an independent dependency. This increment does
not correct the previously identified Workflow Key Vault action pin or resolve
runner, environment, credential or permission availability. Those are separate
integration work and must be proven before runtime can be enabled.

## Inputs and immutable identity

`Build(request, forward, rollback, now)` requires:

- Contract version 1, release ID, operation UUID and versioned validity policy.
  The caller supplies the policy maximum duration; no universal TTL is invented.
- Governed target/application/environment/endpoint/database/source repository,
  server binding (`ALLOW_LIST` or `MANAGED_ENDPOINT`), lifecycle, mode, onboarding
  state, classification reference and immutable governance provenance.
- Immutable source revision, project and startup-project paths, repository
  evidence and a sorted, unique managed migration sequence. Optional historical
  adoption provenance must identify the same repository; it need not equal HEAD.
- Complete PRE and qualification POST schema, data/security hashes, observed
  identity/incarnation, history, metrics, evidence references and capture times.
  EXISTING_EF requires managed onboarding and a certified schema baseline matching
  PRE. Applied history must be a strict prefix of the managed repository sequence;
  the suffix becomes pending, and qualification POST must contain the full sequence.
- Original nonempty forward and rollback bytes in their proper roles. Hashes use
  those bytes, including BOM/line endings. Analysis accepts a single initial UTF-8
  BOM without changing the payload; malformed UTF-8 or other BOMs block.
- Recovery strategy, strategy-approval reference, exact rehearsal-evidence hash,
  payload/target binding, and separately referenced recovered PRE and reapplied POST.

Evidence references are semantic IDs and SHA-256 hashes. Storage locators are not
accepted by this contract. Snapshots, raw SQL, data values and credentials are not
stored in the manifest. Inputs are cloned, the manifest is frozen as JSON, and its
SHA-256 binds the source, target, policy, PRE/POST, exact payload and recovery.
`Read()` returns a detached copy. The payload hash remains environment-independent;
the plan hash binds its particular governed target and environment.

An acquisition adapter must verify the referenced bytes and their authority,
resolve real Registry/Onboarding/classification, prove repository provenance and
connect the supplied observations to the actual capture/rehearsal. This pure API
cannot authenticate caller-supplied fields or certify that a supplied hash was
observed. Supplying fabricated but mutually consistent evidence is not authority.
Do not expose it as a caller-authenticated runtime API.

## Analysis and recovery

The existing analyzer must resolve both scripts completely. Unknown syntax,
incomplete schema/impact, missing or invalid metrics and blocking dependencies
prevent a consistent result. With incomplete analysis or impact, `Risk` is absent
and `ImpactComplete` is false; consumers must never interpret absence as LOW.
Complete risk uses the unchanged engine, including rollback risk.

Recovery reports separate script availability, potential data loss, required data
validation, schema/data validity, exercise consistency and strategy-approval
reference. `ExerciseVerified` means the submitted evidence passed consistency
checks; it does not authenticate execution or the approving principal.

The supported consistency path is `FullReversible`: existing qualification must
be complete, its UP/DOWN hashes and impact/security scope must match, canonical
documents/JSON/hashes must agree, PRE2 must equal PRE, and POST2 must equal POST1.
History, schema, data, security and incarnation are compared; all four observations
need distinct evidence IDs and coherent timestamps. Missing or invalid recovery
evidence blocks. The expected POST is bound to the submitted qualified POST1/POST2,
not inferred from a migration name or the number of migrations.

`SchemaOnly`, `RestoreRequired` and `ForwardFixOnly` explicitly block with
`RECOVERY_STRATEGY_EVIDENCE_ADAPTER_REQUIRED`. A human reference cannot stand in for
a verified restore/forward-fix exercise. Unknown strategy also blocks. Execution
and incident recovery remain outside A1; there is no retry or cleanup mechanism.

NEW_EF composition requires a separately evidenced bootstrap and a complete empty
physical PRE consistent with its schema. A1 does not provision absent databases.
Its exact PRE/PRE2 equality is deliberately stricter than the stabilized NEW_EF
pilot's initial ABSENT to retained EMPTY history recovery policy. That policy and
executor remain unchanged; adapting their richer receipt is a later explicit
contract decision, not an implied permission of this composer.

## Pure checks and statuses

| API / status | Meaning |
| --- | --- |
| `Build`: `PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED` | The supplied evidence is mutually consistent within this contract. |
| `Revalidate`: `PRE_MATCH_NOT_AUTHORIZED` | Trusted plan hash, validity, exact SQL hashes and a fresh supplied PRE match. |
| `VerifyPost`: `POST_MATCH_NOT_CERTIFIED` | A supplied successful execution receipt binds operation/target/plan/payload/forward hash and time; an independently referenced POST matches all approved state dimensions. |
| Any API: `BLOCKED` | Missing, invalid, stale, inconsistent or unsupported evidence. Reasons are stable codes; exception text is not emitted. |

The trusted plan hash must come from independent protected evidence, never merely
from the same untrusted artifact. Actual approval, permissions, code/tool revisions,
credential purpose and effective freshness must be checked by the future runtime
adapter immediately before any write. `Revalidate` does not perform those checks.
An uncertain, failed or partial execution receipt cannot produce a POST match.
Post verification may finish after plan expiry only if execution completed within
validity; it grants no further execution time.

## Offline verification and real-fixture limit

The test harness registers `EfReleasePlanTests` in the full regression suite and
provides `--ef-release-plan` for focused checks. Tests cover Orders, Billing,
Warehouse and Shipping with different identities, endpoints, repositories,
history lengths and pending ranges, plus adversarial bindings, partial evidence,
recovery, payload replacement, drift, expiry and uncertain execution.

The real EF 10.0.9 UP/DOWN fixtures retain their recorded hashes and provenance in
`tests/DatabaseReleaseQualification.Tests/Fixtures/Ef1009/README.md`. The recorded
seven-of-eight managed prefix has pending `20260701161500_AddSeblobTable`. This is
an offline expectation, not a fresh observation of CICDV3. The test intentionally
supplies synthetic observations and verifies blocking: acceptance by the closed
exact-batch parser does not establish complete generic semantic analysis or a
verified recovery exercise for those bytes. The generic analyzer currently lacks
the required coverage for the fixture's implicit schema / identity-insert wrapper.
No synthetic evidence is presented as a successful real qualification.

Commands (after an authorized dependency restore):

```text
dotnet build tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-restore
dotnet run --project tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-build --no-restore -- --ef-release-plan
dotnet run --project tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-build --no-restore
bash tests/test-ef-release-plan-static.sh
git diff --check
```

After the independent Actions PR, stop for human review/merge. No Workflow/caller
repin, runtime adapter, dispatch or real E2E is part of A1. Further work requires an
explicit integration increment and independent evidence for the listed operational
dependencies; the final corporate circuit is not complete in this PR.
