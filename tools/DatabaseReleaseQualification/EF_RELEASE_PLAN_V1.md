# EF release plan evidence V1 (A1 / A2.1)

`EfReleasePlannerV1` composes a frozen, versioned EF evidence manifest and performs
pure PRE and POST comparisons. This increment is in Actions only. It adds no CLI,
workflow, credential provider, SQL executor, approval grant or Registry writer.
Existing NEW_EF, EXISTING_EF pilot and LEGACY consumers remain unchanged.
The A2.1 service boundary and its remaining production dependencies are described
below; the original public `Build` API retains its A1 behavior.

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
supplies synthetic observations and verifies blocking in the original A1 API: acceptance by the closed
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

## A2.1 preparation service and trust boundary

The library entrypoint is
`new EfEvidencePreparationV1(hostProvider).PrepareAsync(selection, now, token)`.
It authenticates referenced content through `IEfAuthorizedEvidenceProviderV1`,
verifies actual SHA-256 bytes, checks strict JSON (including duplicate/unknown
fields), composes A1 inputs and checks current evidence again before returning.
`new EfEvidencePreparationV1()` always blocks with `AUTHORIZED_PROVIDER_REQUIRED`.
There is no production provider or workflow integration in A2.1.

The host, not request JSON, selects the provider. `ReadAuthorizedAsync` must
independently authenticate producer identity, immutable code revision, permitted
purpose, target, repository revision and acquisition session. The provider must
verify the producer's derivation of normalized documents from raw evidence, not
merely permit downloading bytes at a caller-supplied hash. Producer receipts are
retained in the frozen manifest. A valid hash or a caller's `COMPLETE` value does
not establish authenticity. Test providers authorize in-memory fixture bytes;
they are deliberately not presented as authentication infrastructure.

`VerifyCurrentAsync` must reject changed source revisions, governance/baseline
pointers, revoked authorization and rebound evidence for that scope. It is a
required host check, not a local timestamp substitute. A subsequent APPLY still
needs atomic freshness/authorization enforcement; no evidence check here grants
a capability to execute. The trusted plan hash must come from protected evidence.

| Authorized document / purpose | Content used by preparation |
| --- | --- |
| `GOVERNANCE` | Target, lifecycle, onboarding, classification, certified baseline or bootstrap, policy and their independent content references. |
| `SOURCE` | Actual source revision, managed history, project paths, historical adoption provenance and original UP/DOWN bytes; historical adoption need not equal the current revision. |
| `TARGET_PRE`, `FRESH_PRE`, `ACTUAL_POST` | Distinct observations of the real endpoint, identity/incarnation, existing Discovery V2 states/history, canonical schema, data/security hashes, coverage and metrics. |
| `SQL_NAME_RESOLUTION` | Governed effective schema resolution tied to inspection/generation context. The producer must exclude ambiguous default-schema/dbo fallback; the analyzer never guesses dbo. |
| `ISOLATED_QUALIFICATION` | Separate lab target, lab PRE/POST/PRE2/POST2, payload/source binding and existing recovery evidence. |
| `RECOVERY_EXERCISE`, `RECOVERY_APPROVAL` | Actual exercise bytes and independent approval bound to target, payload, strategy and validity. |
| `CONTENT_HASH_CONTRACT` | Versioned full content scope and pinned data/security projection producer revisions. |

Nested references are read under their own purposes (governance/onboarding bytes,
policy, existing V2 classification/repository discovery, baseline, bootstrap and
adoption). Missing, denied, stale, malformed or mismatched evidence blocks with
sanitized reason codes. Discovery V2 and classification implementations are reused,
not reimplemented or invoked through another SQL path.

## A2.1 EF semantics and isolated qualification

`EfSqlSemanticAnalysisV1` reuses `ExactBatches`, ScriptDom,
`EfIdentityInsertContract` and `SqlScriptAnalyzer.AnalyzeParsed`. It preserves the
original bytes/hash, accepts one leading BOM, resolves names only with explicit
producer evidence, and records paired identity ON/OFF operations. Both wrappers
must have identical predicates; only inserts into that table may intervene.

Supported direct effects include CREATE/DROP TABLE, column changes, primary keys,
indexes and literal INSERT/UPDATE/DELETE. DML targets must exist at that point in
the script or in PRE. The subset blocks unknown syntax, indirect functions,
subqueries, sequence effects, FK definitions and DML with unproven catalog effects
(triggers, FK relationships, dependencies, existing default/check constraints or
computed columns). This conservatism can block valid SQL until its indirect impact
has a dedicated model. It does not widen the closed executor's grammar.

`SEMANTIC_ANALYSIS_COMPLETE` describes structural effects under the supplied
snapshot/resolution contract; it does not prove runtime success or safe recovery.
The unchanged EF 10.0.9 fixture now has complete UP and DOWN semantics in A2.1.
Its identity pair, new table, seeded row and migration-history writes are modeled;
DOWN still carries potential data loss. Without an authenticated qualification,
preparation returns `BLOCKED` / `QUALIFIED_POST_REQUIRED`, never invented recovery.

The acquired manifest keeps real PRE and lab PRE/qualified POST distinct. Lab
target, physical database and incarnation must differ from the real target, while
PRE content must match. Recovered PRE and reapplied POST retain lab identity;
existing rehearsal/security evidence must identify that lab. Qualified POST is
an independently produced state, never a projection from migration names.

Clone comparison requires a pinned content-hash contract. Its producer must
preserve complete data scope and security semantics (principals, SIDs, grants,
ownership and affected objects), while separately binding physical identity.
A2.1 does not silently strip identity from existing security evidence or invent
a production projection algorithm. Missing projection evidence blocks. The host
must verify the same contract for real PRE, lab states, fresh PRE and actual POST.

`RevalidateAsync` authenticates a new real PRE plus the current source and compares
all plan dimensions. `VerifyPostAsync` authenticates an execution receipt and an
independent actual POST, retaining the real incarnation while comparing content
against qualified lab POST. Uncertain/failed/partial receipts cannot match.

FullReversible evidence can produce `PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED`.
Down availability, potential data loss and verified exercise remain separate.
SchemaOnly, RestoreRequired and ForwardFixOnly still require dedicated verified
exercise adapters; references alone cannot enable them.

## Continuous operation and remaining integration

Equal managed/applied history requires authenticated fresh PRE, matching certified
baseline and empty SQL; it returns `NO_CHANGES` with an evidence-set hash and no
plan/write capability. First NEW_EF preparation still requires independently
evidenced bootstrap and the A1 exact empty-PRE recovery contract. The stabilized
NEW_EF retained-history behavior is unchanged and needs an explicit adapter.

`ProposeBaselineTransitionAsync` authenticates actual POST, receipt, current
predecessor and separate transition authorization bound to plan/operation/target,
validity and risk/recovery dimensions. It reuses `CertificationDecisionEngine`
and returns `BASELINE_TRANSITION_CANDIDATE_NOT_COMMITTED`, with `Committed=false`.
It never writes or certifies Registry. A future authorized store commit must check
the predecessor atomically; a successive release consumes that independently
committed baseline without repeating adoption.

A2.2 must supply authenticated host/provider adapters over existing governance,
Discovery V2 and repository evidence; pinned portable content projections; an
isolated qualification producer supporting the EF semantic subset; and effective
approval/permission evidence. Restore/forward-fix adapters, retained-history NEW
recovery and atomic baseline persistence remain explicit dependencies if those
paths are integrated. Runtime authorization remains separately required.

Offline tests (`--ef-preparation`, also in the full suite) cover the real fixture,
Inventory/Payments/Fleet across different repositories, applications, endpoints,
schemas and pending histories; independent fake lab qualification; NO_CHANGES;
successive baseline transitions; and adversarial provenance/drift/uncertain states.
No fake result establishes production readiness. All A1/A2.1 results preserve
`ExecutionAuthorized=false`. Stop after the Actions PR for human review/merge;
do not automatically begin A2.2 or repin consumers.
