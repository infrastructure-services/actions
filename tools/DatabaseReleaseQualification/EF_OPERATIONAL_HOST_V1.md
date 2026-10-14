# EF operational host V1 — A2.2A

The existing `DatabaseReleaseQualification` executable now exposes
`prepare-ef-release`. It composes the integrated A1/A2.1 services; it does not add
an EF engine or a SQL path. Its production composition is **BLOCKED** pending the
authenticated operational producers listed below. Successful fake-provider tests
are not evidence of external-service authentication or runtime readiness.

This increment is safe to review and integrate as **A2_2A_PR_READY_GATED**:
publication of the CLI/contracts does not enable their operational providers.
The production command remains BLOCKED after integration. There is no activation
flag, secret-installation shortcut, or approval that bypasses the missing producers.

## Invocation and request

After the existing Release build of the pinned Actions engine:

```sh
dotnet "$engine/DatabaseReleaseQualification.dll" prepare-ef-release \
  --request "$RUNNER_TEMP/ef-request.json" \
  --output "$RUNNER_TEMP/ef-result-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT" \
  --timeout-seconds 120
```

Arguments are positional in that order. Timeout is optional, default 120 seconds,
range 1–900. Input is UTF-8 JSON, 1–65,536 bytes, maximum depth 32. Unknown/duplicate
properties, storage locators, floating revisions and extra arguments are rejected.
The output directory must be absent or empty and cannot traverse directory links.
Artifacts use create-new semantics; previous evidence is never overwritten.

The request schema is `schemas/ef-operational-request-v1.schema.json`.
It contains selectors only:

| Field | Contract |
| --- | --- |
| `contractVersion` | `1` |
| `operation` | `PLAN`, `REVALIDATE`, `VERIFY_POST` |
| `applicationId`, `environment` | Explicit application; environment must be `TEST`. |
| `governanceRevision`, `actionsRevision` | Integrated immutable 40-character lowercase SHA pins. |
| `selection` | Existing `EfPreparationSelectionV1`: target/source/revision/session scope, release/operation identity and governance/source/PRE/optional qualification evidence references. |
| `continuation` | Omitted/null for PLAN; independently authorized frozen-plan reference plus fresh PRE/current source for REVALIDATE, or execution receipt/actual POST for VERIFY_POST. |

Evidence IDs and hashes select content; they do not authenticate it. There is no
`trusted` field, raw observation/SQL input, configurable provider assembly, artifact
directory provider, test mode, SQL connection or mutation credential argument.
The engine must be built from an immutable trusted Actions checkout as in the
existing corporate build. The caller's Actions SHA is not an execution attestation.

## Results and artifacts

Stdout is one sanitized JSON `EfOperationalResultV1`, also written as
`ef-result.json` when a valid fresh output location is available. It includes
contract version, operation, status, stable reason codes, optional plan/evidence-set
hashes, verified Git-source receipts, `executionAuthorized:false` and
`certified:false`. Invalid arguments/output locations may only produce stdout.

| Exit | Status |
| --- | --- |
| `0` | `PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED`, `NO_CHANGES`, `PRE_MATCH_NOT_AUTHORIZED` or `POST_MATCH_NOT_CERTIFIED` |
| `65` | `INVALID`: request/schema/size/environment/output precondition rejected |
| `66` | `BLOCKED`: missing, denied, stale, divergent or unverifiable evidence |
| `70` | `ERROR`: technical I/O or artifact publication failure |
| `124` | `TIMEOUT`: the bounded host invocation expired |
| `130` | `CANCELLED`: cancellation token, console interrupt, or Unix SIGTERM |

When the engine returns a plan, `ef-plan-review.json` contains only its hashes,
applied/pending counts, scenario, risk and recovery summary. It may accompany a
blocked plan; it never authorizes execution. There is no review plan for NO_CHANGES.
Only these two files are allowed review artifacts: no SQL, raw capture, rows,
server/database/endpoint names, project paths, credentials or exception text.
The result file is written last as the completion marker. A missing result or
nonzero process exit must block, even if a review file is present.

The typed host outcome retains the frozen A1 plan in a JSON-ignored property for
a future protected handoff adapter. Public review JSON cannot reconstruct or
authenticate that plan. Continuation loads canonical plan bytes from a separately
authorized `EF_PREPARED_PLAN` producer, verifies the content hash and reuses A2.1
Revalidate/VerifyPost. No self-supplied hash or downloaded review artifact suffices.

## Authentication implemented and dependencies

`EfGitHubSourceHostV1` is the fixed production session factory. It requires the
GitHub runner correlation variables and `EF_GOVERNANCE_READ_TOKEN`; absence or
denied access blocks. The token is consumed only in-process and never handed to
the governance subprocess. Its implementation uses GitHub's read-only
[Refs](https://docs.github.com/en/rest/git/refs#get-a-reference) and
[Contents](https://docs.github.com/en/rest/repos/contents#get-repository-content)
endpoints at `https://api.github.com`, with redirects disabled, bounded responses
and cancellation. It does not use download URLs or arbitrary hosts.

Corporate authority repositories are fixed in host code to the existing
`infrastructure-services/workflow` and `infrastructure-services/actions`.
The adapter requires current Workflow main for governance freshness. An immutable
Actions pin must equal the frozen Actions main tip or be its ancestor, verified by
GitHub's compare endpoint using both exact SHAs. Diverged/unmerged revisions block.
Source follows the existing corporate caller-source contract: same-repository
`push` or `workflow_dispatch`, matching immutable caller SHA and current branch
ref. PR/merge refs remain `SOURCE_REF_POLICY_REQUIRED` until their trust policy is
approved. Content is fetched at the exact Workflow SHA, verified against Git blob
identity and recorded with SHA-256; no floating code is executed. All observed refs
are checked again after acquisition; changed governance, engine tip or source blocks.

It authenticates the existing governance/onboarding/Registry source bytes and
reuses the exact `resolve-governance-runtime.mjs`, `validate-ef-source.mjs` and
`prepare-ef-source-runtime.mjs`
modules from that revision in an isolated, credential-free subprocess. Target,
application, TEST, EF mode and onboarding must match. Adoption repository is
checked when present; the historical adoption SHA is not required to equal current
source. The corporate source resolver authorizes the caller selection separately
from historical adoption metadata. A final current-ref check detects changes during acquisition, including a
new baseline commit. Registry bytes are not reclassified as a certified baseline.

This proves only Git-source provenance, conditional on the trusted runner/host
deployment. GitHub variables, a valid PAT and Git documents do not attest the
executing job, effective protected-environment approval or SQL producer output.
Therefore this production factory returns no `IEfAuthorizedEvidenceProviderV1`
and explicitly blocks with these dependencies:

- `EXECUTED_HOST_ATTESTATION_REQUIRED`: authenticate the executing reusable
  workflow/job, host revision and acquisition session using the corporate trust
  mechanism; environment variables alone are insufficient.
- `A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED`: verified mapping from existing V2
  discovery/classification, repository/adoption, generated exact SQL, certified
  baseline and captures to A2.1 documents, including current-target freshness and
  a protected immutable-plan handoff. Existing review artifacts do not satisfy it.
- `CONTENT_PROJECTION_PRODUCER_REQUIRED`: pinned complete data/security projection
  evidence for real and lab states, preserving principals, grants and scope.
- `ISOLATED_QUALIFICATION_PRODUCER_REQUIRED`: independent payload-bound lab
  PRE/POST/recovery/approval evidence. Strategies without verified adapters remain
  blocked. NO_CHANGES needs no lab, but still needs authentic normalized PRE/source.

These providers are absent from the integrated sources inspected for A2.2A.
They cannot be enabled by installing a secret or accepting a caller flag. No real
token, external producer authentication, SQL, workflow or environment was exercised
by the offline tests. The implementation contains no automatic database creation;
NEW requires an externally provisioned target and independently evidenced bootstrap.

## Corporate workflow integration boundary

A2.2B adds the local composite `ef-release-plan/action.yml` and its strict process/
artifact adapter. It consumes the existing Release build from the same immutable
Actions Git checkout; it performs no build, restore, Discovery or SQL itself. See
[`EF_CORPORATE_PLAN_A2_2B.md`](EF_CORPORATE_PLAN_A2_2B.md) for its review boundary,
offline evidence and pending Workflow/authentication work. This is a prerequisite
Actions increment; Workflow must wait for human merge before consuming its SHA.

After the missing adapters are implemented and Actions is human-merged, a bounded
Workflow PR can add a generic TEST PLAN job using the integrated Actions SHA,
governed target/application selection and current same-repository source. Reuse
Discovery V2, governed `projectPath`/adoption metadata and the existing build.
Invoke the command above, retain its exit code, parse `ef-result.json` for outputs,
and upload only `ef-result.json` and optional `ef-plan-review.json` on completion.
Map `EF_GOVERNANCE_READ_TOKEN` from the existing read-only governance credential;
do not pass mutation credentials or treat exit 0 as APPLY approval. Do not repin
consumers to an unmerged branch. Workflow and applications are unchanged here.

Offline validation: `--ef-operational-host` runs the targeted groups in the existing
test harness; the full suite includes them. Tests run both the actual production
command (fail-closed outside its host) and the real CLI with explicitly injected
fake providers in a test-only executable mode. The optional directed test
`--ef-host-governance-contract <workflow-checkout>` runs the unchanged resolver
with synthetic documents and a fake HTTP transport, without external access.

## Publication security boundary

The production command constructs `EfGitHubSourceHostV1` directly. Every return
from that factory contains a null authorized provider. Even correct runner
variables, valid Git bytes and a matching governed target cannot produce a PLAN.
PLAN, REVALIDATE and VERIFY_POST share this gate; APPLY is not an accepted operation.
The fixed result properties never grant execution or certification.

Typed C# dependency injection is a trusted embedding interface, not a caller
interface. No argument, JSON field, environment switch, fixture directory or
assembly selector can register a provider in the shipped process. Test factories
and child modes are compiled only into the separate test executable. The production
process tests explicitly reject caller authority fields and test-provider flags,
and matching spoofed runner variables still block without credentials (no HTTP).
The separate mocked-HTTP tests cover successful Git reads that still cannot
create an authorized provider.

This boundary assumes a trusted installed engine, .NET/Node runtime, runner and
process environment. It is not a sandbox against someone who can replace binaries,
inject runtime startup code or change the runner executable search path. Future
host attestation must bind that deployment and isolate application build/generation
from its credentials. OIDC claims alone do not prove which DLL bytes executed.
Existing SQL commands elsewhere in the engine are unchanged and are never called
by `prepare-ef-release`; they are not authorized by its result.

## Unblocking plan by responsibility (future work, not enabled here)

| Owner | Exists in inspected code | Missing dependency | Evidence required to resolve it |
| --- | --- | --- | --- |
| Actions | A1/A2.1, parser/risk/recovery contracts, executable CLI, fixed Git source adapter and strict result contract. | Production session factory that combines authenticated identity with authorized purpose-specific producers; protected frozen-plan handoff. | Integrated immutable commit; rejection of forged/replayed/wrong-target evidence; real session returns a consistent PLAN or NO_CHANGES without write authority; exact-byte handoff round trip. |
| Workflow | `ef-test-migration-controlled.yml`, reusable Discovery V2, immutable checkouts/build, `resolve-ef-projects.mjs`, exact SQL generation and artifact handling. Current orchestration still contains pilot constants and APPLY. | A generic PLAN-only route consuming this command and real producer session, preserving exit codes and isolating it from `apply_ef_test`. | Reviewed Workflow diff and integrated SHA; static job-graph guards; later authorized run from an existing caller showing the CLI receives authentic evidence, publishes only allowed artifacts, and never schedules APPLY. |
| GitHub/OIDC identity administrators + Actions verifier | GitHub run variables used only for correlation; immutable corporate Git authority. GitHub supports signed reusable-workflow identity claims. | Approved workload trust policy and verifier, audience/issuer/signature/time checks, caller/reusable-workflow revision and session binding, replay controls, trusted host deployment. No verifier or federation was configured in this task. | Valid signed session accepted; altered signature, wrong audience/repository/workflow/revision, expiry and replay rejected. Bind `job_workflow_ref`/`job_workflow_sha`, source SHA, run/attempt and trusted Actions code separately; do not infer protected approval from an environment string. Any required permissions/configuration need a separately authorized change. |
| Governance owners | Central Git/PR resolver, target/onboarding documents and EF adoption metadata, including `projectPath`, managed start and pre-adoption artifacts. | Purpose-specific producer authority and content/projection policy; authenticated certified-baseline reference or independently evidenced NEW bootstrap; current freshness binding. | Exact integrated document hashes and governed target resolution; authorized baseline/bootstrap evidence; changed revision/target/projection blocks. Historical adoption SHA remains provenance, distinct from the current caller SHA. Registry never auto-certifies. |
| Key Vault / platform identity owners | Current workflow declares inspection credentials and an existing owner-secret path only for its separate APPLY job. No Key Vault access or effective grants were verified here. | Approved read-only acquisition identity and credential mapping for the governed TEST target; scoped access and separation from owner/mutation credentials. A missing normalized producer is not fixed by supplying a secret. | Later authorized access/denial checks and sanitized audit receipts prove intended access and denial outside scope; no credential in logs/artifacts or the application build. Use the existing corporate credential delivery capability; do not copy the APPLY owner-secret route into PLAN. |
| Evidence producer owners + Actions adapters | Discovery V2, schema capture, repository/project/adoption resolution, generated SQL and existing qualification contracts. | Authenticated normalization to A2.1, complete data/security projection, independent payload-bound isolated PRE/POST/recovery evidence and protected continuation transport. | Source/content hashes, producer identity/revision/purpose/session, target/incarnation and freshness verified end to end; tampering, drift, divergence, missing recovery or projection block. NO_CHANGES needs authentic PRE/source but no rehearsal. Identity remains independent of storage; this PR selects no backend. |
| TEST infrastructure / DBA owners | Existing TEST runner/discovery wiring and NEW preprovisioned-database contract. Current environment access was not exercised. | Validated target binding, preprovisioned NEW target where applicable, read-only observation capability and independent isolated qualification environment/approved recovery adapter. | Separately authorized observations prove identity and coverage; qualification evidence proves the exact payload and recovery in the isolated environment. No target database creation, real APPLY or lab writes are authorized by this integration PR. |

GitHub's [reusable-workflow OIDC guidance](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-with-reusable-workflows)
and [claim reference](https://docs.github.com/en/actions/reference/security/oidc)
describe the identity inputs for the proposed verifier. Federated trust conditions
must match the capabilities of the actual corporate identity provider; this is not
a claim that Azure automatically validates every GitHub custom claim.
Key Vault access must follow the existing corporate access model; the
[Azure RBAC guidance](https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide)
distinguishes data-plane secret access from resource administration. No role change
or identity migration is included here.

## Next increment: one vertical corporate PLAN integration

The next increment must deliver coordinated Actions and Workflow changes with an
end-to-end acceptance boundary, not another standalone library:

1. Implement the trusted acquisition session and adapters in Actions against the
   existing producers and the identity/governance contracts above. Keep each absent
   producer blocking; do not infer raw evidence from review JSON. Bind the executed
   Actions revision, normalized evidence, scope and protected plan in one session.
2. In `.github/workflows/ef-test-migration-controlled.yml`, add a bounded generic
   `prepare_ef_release` PLAN-only route using the existing discovery reusable workflow,
   immutable checkouts, setup/build and project resolver. Resolve target/application
   and database binding from governed metadata, not pilot constants. Reuse
   `projectPath`, managed migration start and exact adoption exclusions. Continue
   acquiring migrations from the current caller checkout.
3. Reuse the source-generation parts of `scripts/ef-test-plan.sh` through the
   existing resolver and producer adapters; do not rebuild a Luana-specific flow
   or treat its pilot review plan as an authenticated A2.1 document. Isolate
   application-controlled build/generation from the trusted session credentials.
4. Make the new route explicitly PLAN-only and mutually exclusive with the
   existing APPLY path. The current `apply` input defaults to true; merely adding
   a job or setting a caller flag is insufficient. Guards must prove that selecting
   this route cannot schedule `apply_ef_test`, regardless of caller inputs, or
   obtain its owner credentials. Preserve the historical executor/LEGACY behavior.
5. Feed authenticated selectors to `prepare-ef-release`, require a complete result,
   preserve 65/66/70/124/130 as failures and permit only the two sanitized artifacts.
   Exit 0 continues to mean consistent evidence without execution authorization.
6. Integrate Actions first, pin its merged SHA in the Workflow PR, then integrate
   Workflow before updating any caller pin. A later separately authorized TEST run
   must exercise the existing corporate workflow from an existing caller, with
   real identity and producers. Require positive PLAN/NO_CHANGES as applicable and
   negative missing-evidence/tampering cases; an always-blocked run is only wiring
   evidence. Changes needing isolated qualification remain blocked until that
   independent producer is accredited. NEW uses a database provisioned beforehand.

This is a design and dependency plan only. This PR makes no Workflow/application,
permission, environment, Key Vault, Registry, SQL or executor change. Stop after
publishing A2.2A for human review; do not start this next increment automatically.
