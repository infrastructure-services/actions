# A2.2B — corporate PLAN adapter, first Actions gate

Verdict: **A2_2B_INTEGRATION_GATED**. This increment supplies a concrete consumer
of the existing operational CLI and corrects its corporate Git-source acquisition.
It is not the complete caller → authenticated PRE → NO_CHANGES vertical slice.
Production still returns BLOCKED when external evidence authority is unaccredited.
No provider, verifier, OIDC federation, baseline, credential or approval is invented.

## Verified baseline and bounded scope

| Repository | Verified main | Preflight |
| --- | --- | --- |
| Actions | `4cc79a8b1ead3b1abebb42b1c35d61cf00c6d44e` | Clean and equal remote; implementation branch starts here. |
| Workflow | `e19c4bd39eb23bade188c261c37380fa06f79632` | Clean; fast-forward from `4b3a11c1a00216482ab4440f13f7fea280741454`, one CI workflow addition. |
| Luana | `2bd491451664904994e52133bfc954134443146b` | Clean and equal remote. |
| Pilot | `0a01d61f81af8e142c49308c6a6abd67ec6d4936` | Clean and equal remote. |

Only Actions changes in this increment. No Workflow/application pin changes,
dispatch, SQL connection, secret lookup, Registry mutation or APPLY changes.
The external verifier and trust policy are **NO ACREDITADOS**, not asserted absent.
Stop at **hard gate A** for human review/merge of this Actions dependency.

## Reuse and actual contract gaps

| Capability | Corporate component | Increment / remaining gap |
| --- | --- | --- |
| Governance and adoption | Workflow `database-registry/resolve-governance-runtime.mjs`, `validate-ef-source.mjs`, `prepare-ef-source-runtime.mjs` | Fetch/hash at current immutable governance SHA and run credential-free. Reuse source selection rather than equating adoption SHA with current caller. |
| Integrated engine source | `EfGitHubSourceHostV1` GitHub REST adapter | Accept pinned ancestor of frozen Actions main, requiring base/merge-base identity and nondivergent comparison. Keep governance current and check all refs again. This is Git provenance, not DLL attestation. |
| Caller source | Same corporate source resolver, `GITHUB_SHA`/`GITHUB_REF` | Same-repository push/dispatch branch SHA must equal GitHub ref; PR/merge refs explicitly block. Runner variables are correlation only. |
| Projects/build/SQL generation | Workflow `resolve-ef-projects.mjs`, existing engine Release build and source-generation portion of `ef-test-plan.sh` | Reuse after gate A; do not execute application code with acquisition credentials. Existing review plan is not A2.1 authenticated evidence. |
| Discovery/PRE/history | `database-discovery-v2-test.yml`, Actions `sql-discovery-v2`, `repository-discovery-v2`, Schema Capture | Existing producers unchanged. Authenticated normalization, complete data/security projection and freshness still required. Discovery JSON plus hashes do not establish authority. |
| PLAN/NO_CHANGES/risk/recovery | `prepare-ef-release`, A1/A2.1 | New local composite and strict process/result adapter consume this exact CLI. No duplicated EF engine, parser or classifier. |
| Credential delivery | Corporate inspection secret and Key Vault capability | Inspect-only mapping/effective grants require accreditation. Never copy APPLY's Owner path. |

The reported Key Vault issue is confirmed at the inspected immutable pin:
`ef-test-migration-controlled.yml` references root Action
`aac0078bb5c9a641991b0c18dc3d372079cca69f`, whose `action.yml` is **Database Release
Qualification V1 (Analyze Only)**, not secret retrieval. This increment does not
touch APPLY or substitute an unverified pin. A separate owner-reviewed correction
must identify the actual corporate secret action/revision before changing that path.

## Local composite contract

Use `./<immutable-actions-checkout>/ef-release-plan` after the existing trusted
engine Release build. This action requires a Git checkout and built DLL in that
checkout; GitHub's downloaded `uses: owner/repo/path@sha` action directory is not
that contract. No caller-selectable executable/provider/test mode is accepted.

Inputs: `request-path` (A2.2A selectors-only JSON, operation PLAN, environment TEST),
`governance-read-token` (existing corporate read-only Git token), optional
`timeout-seconds` (1–900, default 120). The request Actions SHA must match checkout
HEAD. That correlation does not establish DLL/toolchain authenticity.

The adapter invokes the fixed built `DatabaseReleaseQualification.dll` and fixed
`prepare-ef-release` command. Its child environment is allowlisted: no SQL/Owner,
NuGet/build credentials, provider switches, NODE_OPTIONS or .NET startup hooks.
It creates a unique output directory below RUNNER_TEMP. The CLI validates the full
strict request; caller JSON cannot become authenticated observation evidence.

Outputs appear only after complete validation: `status`, `reasons-json`,
`exit-code`, `execution-authorized=false`, `certified=false`, `artifact-directory`
and `artifacts-ready=true`. It initially writes `artifacts-ready=false`.
Codes 65/66/70/124/130 remain failures; blocked reason codes are preserved exactly
when the CLI has a valid completion marker. Exit 0 is evidence consistency only.
Invalid process/output contracts fail without publishing any artifact directory.

Only `ef-result.json` and optional `ef-plan-review.json` pass the allowlist.
Result/stdout must agree; unknown/duplicate fields, inconsistent status/exit,
missing plan review, mismatched plan hashes, unexpected files, links and oversized
files block publication. NO_CHANGES requires an evidence-set hash and no plan.
Raw child output, errors, request contents and SQL are never echoed on failure;
adapter failures emit only an allowlisted stable reason code. A successful plan
review must include risk and verified recovery; inconsistent summaries block.
Do not upload directory globs or raw request/stdout on an adapter failure.

The adapter assumes a trusted isolated runner, PATH/.NET/Node and installed engine.
It is not a sandbox against an attacker who controls the runner or can replace the
adapter/binary or mutate its private files concurrently. That deployment trust is
part of the still-unaccredited executed-host control.

## Workflow gate and compatibility

The existing DAG remains `discovery → analyze_ef_test → apply_ef_test` with its
historical `apply=true` default. Luana currently passes `apply=false` explicitly.
This PR neither activates nor demonstrates isolation of a new PLAN job in that DAG.
It must not be described as operationally complete just because the production
adapter safely returns BLOCKED. Existing NEW preprovisioning and LEGACY paths are
unchanged; no automatic database creation is pending or required.

After Actions human merge, a separate Workflow change must use the actual merged
SHA and make route selection mutually exclusive: new PLAN route excludes both old
analysis and APPLY for every value of `apply`, including true. PLAN success and
NO_CHANGES must have no dependency edge to APPLY. Prove the full old/new job matrix,
including failed parsing and BLOCKED. Avoid APPLY environments/secrets entirely.
Upload only the two explicit allowed paths, conditioned on validated
`artifacts-ready`, while retaining the nonzero job outcome. Do not use
`continue-on-error` to turn a blocked result into a successful job.

## Required external decisions and evidence

| Owner | Concrete request | Evidence to unblock / impact / next action |
| --- | --- | --- |
| Human Actions reviewer | Review/merge this bounded increment | Actual integrated SHA unlocks the dependent Workflow PR; no automatic repin/merge. |
| Infra/Security | Identify existing workload/evidence verifier and approve TEST trust policy | Contract must verify signature/issuer/audience/time, caller repository/SHA, reusable path/SHA, run/attempt, target, purpose, session and replay rules. OIDC alone cannot attest DLL bytes. Supply approved verifier/deployment contract before production registration. No federation or permission change included. |
| Platform runner/build owners | Attest/isolate pinned Actions host, .NET/Node and build from application code | Reproducible code/build identity bound to acquisition session; no application process sees acquisition/inspection credentials. Demonstrate tamper rejection. |
| Governance/DBA | Govern target and provide independently certified PRE/baseline where applicable | Exact current governance, onboarding, source lineage and certified-state evidence; Registry observations must not auto-certify. Baseline and concurrent changes must invalidate acquisition. |
| Infra/Key Vault | Accredit existing TEST inspection identity and governed target mapping | Minimal read-only access and denial outside scope, sanitized receipt; explicit separation from Owner and deployment approvals. No secret read is authorized in this task. |
| Producer owners | Authenticate existing Discovery V2, capture, history and source normalization into A2.1 | Bind producer code revision, target/session/purpose/content hash, transformation and freshness; complete data/security projection. Tampered, replayed, stale or mismatched evidence must be rejected. |
| Qualification/DBA | Independent isolated POST/recovery for pending migrations | Payload-bound verified recovery and independent lab identity. Not required for NO_CHANGES, but authentic PRE/source/projection/baseline still are. |

## Minimal later TEST procedure (requires separate authorization)

1. Merge reviewed Actions and later Workflow dependencies; record their exact SHAs.
2. Obtain approved host/verifier trust and purpose-specific producer registration.
   Do not register a fake/test provider or accept caller documents as authority.
3. Select a governed EXISTING_EF TEST target with consistent full managed history,
   independently valid baseline/PRE and no pending migrations. Use inspection-only
   credentials via the existing corporate delivery mechanism.
4. Review the Workflow DAG proof, immutable caller source, target/session bindings,
   freshness, projection coverage and authorized read-only acquisition permissions.
5. Request explicit authorization for one TEST run. Expected: NO_CHANGES, exit 0,
   an evidence-set hash, no plan/SQL, `ExecutionAuthorized=false`, `Certified=false`.
   Missing authority must instead fail with the exact blocking reasons.
6. Retain sanitized result and external verifier/acquisition audit receipts without
   credentials. Prove no APPLY job scheduled and no SQL write issued. Synthetic
   tests cannot provide this evidence.

Configuration rollback: restore the previous reviewed caller/workflow pin through
the ordinary reviewed Git change; revoke only newly authorized dedicated grants if
needed. No database or Registry rollback is involved in this PLAN-only procedure.

## Offline validation and limits

Run the existing .NET Release build with `--no-restore`; then the full qualification
test executable (A1/A2.1/A2.2A, EF parser/payload, NEW/EXISTING/LEGACY/risk/recovery).
Directed checks: `--ef-operational-host`, `--ef-host-governance-contract <workflow>`
and `node --test tests/test-ef-release-plan-adapter.mjs`. Run SQL Discovery V2's
offline harness and relevant static guards plus `git diff --check`.

Validation on 2026-10-14: Release builds passed without restore (zero warnings/
errors); qualification suite **524/524**, SQL Discovery V2 **53/53**, directed host
**11/11**, PLAN adapter **13/13**, real governance/source resolver contract PASS.
Producer **74**, classification adapter **90**, repository discovery **35**,
governed composition **23**, public Discovery **52**, SQL terminal hardening **17**,
NEW bootstrap **40** and isolated bootstrap producer **15** cases passed. Existing
Workflow EF/LEGACY static guards, NEW workflow **33**, NEW integration **34**, project
resolution **10**, adopted-source orchestration and real committed Luana source
checks passed. Luana's supplied seven-entry history is a fixture, not observed SQL.
Classification V2 **62/62** and V1 **19/19** passed. Static operational/A1/
SQL-read-only/Schema Capture/release guards and `git diff --check` passed.

On Windows, use Git Bash on PATH (not the WindowsApps alias), set
`ACTIONS_REPOSITORY_ROOT` to the Actions checkout for Discovery's harness, and set
`ACTIONS_BASH_DIRECTORY` for the Node/Bash producer tests. Sandbox restrictions on
MSYS startup and hard links required rerunning those local checks outside the
sandbox; no network restore or real SQL was performed. The orchestration test
requires `<actions-checkout>` as its positional argument. For Workflow NEW tests,
also set `APPLICATION_REPOSITORY_ROOT` to the pilot checkout.

Positive PLAN/NO_CHANGES tests use the separate test executable's authorized fake
provider and validate actual CLI artifacts. Production-process tests prove it
cannot install that provider. Git ref/integration/race tests use fake HTTP; the
directed contract test executes unmodified real Workflow resolver modules with
synthetic source documents. External signatures/replay, effective grants, complete
production normalization/projection and the real TEST E2E remain unaccredited.

GitHub API contracts used: [Refs](https://docs.github.com/en/rest/git/refs#get-a-reference),
[Compare](https://docs.github.com/en/rest/commits/commits#compare-two-commits),
[Contents](https://docs.github.com/en/rest/repos/contents#get-repository-content).
Only existing Contents read access is used; this increment adds no OIDC request,
Actions-run permission or administrative API.
