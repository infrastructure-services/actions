# Legacy rehearsal V1 — engineering contract and TEST runbook

Date: 2026-09-30. These are instructions for a **future authorized run**.
No SQL, workflow, Registry/onboarding write or deployment was performed to build this change.
Synthetic evidence is not real TEST validation. This document creates no approval.

## Scope and entry points

Macro 1 remains analyze-only and keeps `executionAuthorized=false`.
Macro 2 reuses Macro 1 to reacquire and requalify the **same Git package identity** immediately before rehearsal.
The caller supplies selectors, the expected package identity and explicit consent; never observed truth or validation results.

| Operation | Workflow in infrastructure-services/workflow | CLI |
|---|---|---|
| Original Macro 1 analysis | `.github/workflows/legacy-package-qualification-v1-test.yml` | `qualify-legacy-package` |
| Macro 2 read-only preparation, including selected DATA provider | `.github/workflows/legacy-rehearsal-preflight-v1-test.yml` | `prepare-legacy-rehearsal` |
| Explicitly authorized complete rehearsal | `.github/workflows/legacy-rehearsal-v1-test.yml` | `rehearse-legacy-package` |

The two new commands take `--request <request.json> --output <new-output-directory>`.
The production composition root requires the governed GitHub runner context; local JSON is not a substitute for runtime authority.
All external actions are SHA-pinned. The engine selector must be a published full SHA and still match actions/main.
The workflow checkout must match its executed workflow SHA and current workflow/main. Source changes block.
Read-only child producers do not inherit the mutation connection credential.

## State, execution and evidence

`QUALIFIED → PRE_CAPTURED → FORWARD1_APPLIED → POST1_CAPTURED → ROLLBACK_APPLIED → PRE2_CAPTURED → FORWARD2_APPLIED → POST2_CAPTURED → REHEARSAL_EVALUATED`.

Only the next transition is accepted. The harness is single-use. No phase skipping, duplicate phase, resume, mutation retry, package rollback on failure, cleanup SQL or continuation after failure exists in V1.
Terminal states are `BLOCKED`, `FAILED`, `CANCELLED`, `TIMED_OUT`. A partial rehearsal cannot produce a promotion freeze.
The exact forward SHA is used twice. Any package identity/hash difference requires requalification.

Each capture uses the existing schema reader twice, preserves target/endpoint/observed identity, canonicalizer provenance,
metadata/metrics coverage, capture time and full structural comparison inputs. PRE must match the certified baseline.
Each write rechecks immutable sources and authorization, fresh structure, relevant SECURITY and DATA, and phase safety.
The actual mutating connection must observe the same server/database as inspection. Metadata visibility has no dbo exemption.
Snapshots are owned by the harness and rehashed between phases. A late unsupported operation blocks.

The harness owns one SQL transaction per mutating phase, following Macro 1's `HARNESS_OWNS_PHASE_TRANSACTIONS_V1`.
Only the existing Static Safety allowlist is supported. Author transaction control and unsupported/nontransactional families block.
Scripts are validated as exact UTF-8 Git bytes; original AST text spans are sent as batches without SQL regeneration.
Standalone `GO` is a client separator; repetition counts and SQLCMD expansion are rejected.
Connection retries are disabled; command timeout is 60 seconds, rehearsal deadline 20 minutes, CLI deadline 25 minutes, workflow timeout 30 minutes.
Cancellation is propagated to SQL commands. An uncommitted transaction is disposed on failure; this does **not** execute the package rollback.
Lost connection/commit ambiguity requires human diagnosis, never a replay.

`phase-journal.jsonl` is append-only in the run directory and flushed before every mutation (`STARTED`) and after its result.
The final receipt preserves captured phases and failures. Missing receipt or `STARTED` without `APPLIED` means unknown/partial execution, never success.
Force-killing a runner may prevent a final receipt or upload; operators must preserve the runner journal and inspect the target read-only.

## Recovery requirements

| Dimension | Rollback proof | Reapply proof | Missing/mismatched evidence |
|---|---|---|---|
| STRUCTURE, always required | Canonical PRE vs PRE2 including schema differences | POST1 vs POST2 | Blocks |
| DATA, impact-driven | Selected `IDataRollbackValidationContract` | Companion `IDataReapplyValidationContract` | Blocks before forward if missing; mismatch blocks continuation/success |
| SECURITY, impact-driven | Recovery Coverage canonical PRE vs PRE2 | Canonical POST1 vs POST2 | Blocks, including lost GRANT; never silently downgraded to SCHEMA_ONLY |

`FULL_REVERSIBLE` requires successful complete rehearsal and sufficient matching evidence for **every required dimension**.
The existing `SCHEMA_ONLY`, `FORWARD_FIX_ONLY`, `RESTORE_REQUIRED`, `UNKNOWN` taxonomy is retained.
Failure never becomes FULL_REVERSIBLE merely because rollback executed. `INVALID` remains validity, not a recovery class.

## Governed DATA definitions

No real DATA package or contract has been approved. That is a runtime prerequisite, not evidence of missing generic plumbing.
A future definition lives in workflow at `.github/workflows/legacy-data-contracts/<selector>.json` and follows
[`schemas/legacy-data-contract-v1.schema.json`](schemas/legacy-data-contract-v1.schema.json).
Select only a reviewed identifier; the runtime reads exact Git bytes from the governed checkout and records their source hash/revision.
The definition must match the actual impact scope, target and version. Unknown provider kinds and any mismatch block.
No arbitrary queries, assembly loading or caller-supplied evidence are accepted.

The supplied `SCOPED_ROWSET_SHA256_V1` provider implements `EXACT_CONTENT_HASH_V1`: a deterministic multiset of typed row hashes,
preserving duplicates, column names/types and NULL. It reads only impacted tables/columns, twice per observation, without publishing values.
It requires ordinary visible tables, supported scalar types, no computed/hidden/encrypted columns, stable captures and bounded results.
Limits: at most 32 tables, 100,000 rows per table, 16 MiB serialized values per capture. The definition chooses a smaller row limit if needed.
Unsupported types, missing selected columns/tables, overflow, visibility gaps, limits or unstable data fail closed.
This V1 provider therefore supports bounded ordinary rowsets; packages whose post-state drops a selected table/column need a separately reviewed provider/contract and remain blocked with this provider.
Do not label schema equality as DATA recovery or choose a scope that omits impacted data.

## Authorization and promotion boundary

A future grant lives in workflow at `.github/workflows/legacy-rehearsal-authorizations/<selector>.json` and follows
[`schemas/legacy-rehearsal-authorization-v1.schema.json`](schemas/legacy-rehearsal-authorization-v1.schema.json).
Existing workflow-repository Git/PR governance is the authority; no new durable service is introduced.
Configure the `legacy-rehearsal-test` GitHub environment with required reviewers and protected source branches before use.
Reviewers must approve all three writes together: FORWARD1, ROLLBACK, FORWARD2. Technical blockers cannot be overridden.
The grant binds actor, TEST target, exact package, engine commit, precondition identity and selected DATA contract identity (when required),
with a validity window no longer than one hour. The workflow also requires `executionAuthorized: true`; its default is false.
GitHub job reruns are refused (`run_attempt != 1`). A grant authorizes an explicit invocation within its window, not an automatic retry.
Any additional invocation still requires explicit consent and fresh qualification; V1 does not claim a durable one-time grant ledger.

`preconditionIdentity` is a length-prefixed SHA256 over target, certified schema hash, Registry bytes hash, governance bytes hash and onboarding bytes hash.
DATA approval identity binds selector, version, target, scope hash and definition bytes hash.
These content identities deliberately exclude the commit containing the grant to avoid self-reference. Actual commit provenance remains in the receipt.
Use the preflight outputs directly rather than calculating or guessing identities by hand.

[`LegacyRehearsalReceiptV1`](LegacyRehearsal.cs) and its [JSON envelope schema](schemas/legacy-rehearsal-receipt-v1.schema.json)
bind package/manifest/script hashes, fresh Macro 1 qualification and evidenceSetHash, baseline, PRE/POST observations,
phase statuses, DATA definition/evidence, SECURITY coverage, final class, reasons, authorization provenance and runtime/timestamps.
Nested engine evidence keeps its existing typed V1 contracts; hashes and cross-field comparisons are enforced by code, not JSON Schema alone.
`canProceedToPromotion` remains false: this artifact is not execution authorization or durable certification.

A successful **real** in-process harness may mint [`LegacyPromotionFreezeV1`](LegacyRehearsalAuthorization.cs), binding the exact artifacts,
qualification, receipt, coverage, recovery class and PRE/baseline identity. Synthetic, imported, altered and rehashed receipts cannot mint a freeze.
This closes the artifact contract only. Future promotion consumers must verify trusted producer provenance; no QA/PROD executor or State Store is implemented here.

## Runtime validation matrix

| Scenario | Validation path | Mutation required in this gate? | Prerequisites / expected evidence |
|---|---|---|---|
| EXISTING_EF (A) | Existing `database-discovery-v2-test.yml` | No | Governed EXISTING/EF target, coherent repo/history, certified baseline and MANAGED onboarding; HG5/HG6 artifacts, exact provenance, expected classification/block |
| NEW_EF (B) | Same discovery workflow, existing dedicated NEW bootstrap producer | No | Explicit NEW/EF, preprovisioned DB, EMPTY_FOR_NEW_EF V1, ABSENT EF history, PRESENT_VALID repository; bootstrap capture, intentional NOT_EVALUATED only where allowed |
| EXISTING_LEGACY (C) | Macro 1 → Macro 2 preflight → authorized rehearsal | Yes, only final rehearsal | Governed EXISTING/LEGACY target, certified baseline, MANAGED onboarding, exact package, DATA contract if required, SECURITY visibility, grant and protected TEST environment; complete real receipt and freeze |

A/B audit preserves their closed discovery/classification/bootstrap engineering gates. Their read-only eligibility does not authorize EF migrations.
The preexisting EF deployment workflow is a separate path and was not changed or newly certified by this work.
Durable certification and later promotion are outside this bounded engineering closure.

## Operational runbook — common preparation

1. Review this complete local changeset; publish only under separate commit/push approval. Record the resulting actions and workflow SHAs. Local changes cannot be executed by GitHub.
2. Select a real TEST target and application revision. Confirm target binding, inspection identity, TLS/CA trust, network reachability, SQL metadata permissions and private repository read access.
3. Inspect the governed Registry/onboarding files. Registration is not certification. Never edit those records as an automatic recovery step.
4. Arrange an exclusive maintenance window for Legacy: quiesce application/DBA writers and competing pipelines across repositories. GitHub concurrency is repository-scoped and does not lock external SQL sessions.
5. Any target/configuration, onboarding, Registry or baseline changes require their own approval and verification. A human approval cannot waive drift or incomplete evidence.

Observed local facts at this closure: CICDV3 (`applicationId=3602`) is declared EXISTING/EF_MIGRATIONS,
onboarding is `BLOCKED`, Registry is `BASELINE_REQUIRED` and its certified hash is null.
No NEW or Legacy target/real DATA contract is invented here. SQL availability and TLS trust were not tested in this conversation.

## EXISTING_EF — stages

**PRECHECK:** review the selected Registry/governance/onboarding records and caller/application commit without SQL changes.
After authorization for read-only TEST inspection, call `database-discovery-v2-test.yml` with `applicationId`, `databaseName`,
`inspectionConnectionString` and `governanceToken` through an approved application caller pinned to a workflow commit.

**EXPECTED / REVIEW:** source provenance, SQL target identity, coherent EF history/repository, schema relation and actual `EXISTING_EF` classification.
Eligibility is only for the next read-only stage. For CICDV3 as currently recorded, expect a blocked result until governance/onboarding/baseline work is separately completed.

**STOP / ABORT:** mismatch, drift, unresolved history, insufficient visibility, missing authority, TLS or connection failure.
Retain sanitized artifacts; correct the external prerequisite under separate approval and repeat read-only inspection.
No EF update, Registry write, rollback or cleanup is authorized by this runbook step.

## NEW_EF — stages

**PRECHECK:** select and approve a real NEW/EF target; the DB must already be provisioned. Verify the pinned bootstrap producer and required caller inputs/secrets.
After read-only TEST authorization, call the same discovery workflow. It selects the dedicated NEW bootstrap branch from governed declarations.

**EXPECTED / REVIEW:** `NEW_EF`, PRESENT_VALID repository, EF history ABSENT, complete empty-object taxonomy, deterministic bootstrap schema capture,
and only contractually intentional NOT_EVALUATED. Registration must not become certification.

**STOP / ABORT:** NOT_FOUND does not provision; populated/unknown DB, non-ABSENT history, invalid repository, incomplete taxonomy, stale producer or contradictory Registry evidence blocks.
Retain artifacts. Provisioning, target/configuration changes, initial migrations and future certification require separate explicit authorization.

## EXISTING_LEGACY — stages

1. **Choose package:** approve a real TEST package revision with Manifest V1, exact forward and rollback files. Run the original Macro 1 workflow read-only after inspection authorization.
   Review manifest/script hashes, package identity, target, baseline, managed onboarding, safety, impact and readiness. DATA_REQUIRED without a selected provider remains blocked in original Macro 1; use its artifact identity for the next step.
2. **Define DATA if required:** identify impacted tables/columns, approve the equivalence criterion, bounds and provider, and publish its governed definition under a selected identifier. No real definition exists yet.
3. **Read-only Macro 2 preparation:** call `legacy-rehearsal-preflight-v1-test.yml` with `targetId`, `manifestPath`, `expectedPackageIdentity`, `actionsRevision`, proposed `authorizationSelector`, and `dataContractSelector` when required.
   It receives only inspection/repository-read secrets. Review `legacy-rehearsal-preflight.json`: expect `READY_FOR_AUTHORIZATION`, `executionAuthorized=false`, exact package, precondition identity and DATA approval identity. It resolves the provider and verifies PRE DATA can be captured. Any block stops the process.
4. **Human authorization:** approve the complete TEST sequence, maintenance window, reviewed scripts, recovery plan, inspection/mutation identities and risk. Publish the grant JSON from the preflight identities, the intended actor, a <=1 hour window and an approval reference. Configure/verify required reviewers on `legacy-rehearsal-test`.
   Re-resolve the published workflow SHA after the grant commit. Engine SHA must remain the reviewed actions/main commit. Content-bound preconditions allow the new grant commit without pretending the previous runtime evidence is fresh.
5. **ACTION, only after that approval:** invoke `legacy-rehearsal-v1-test.yml` from a reviewed caller with the same inputs plus `executionAuthorized: true` and the dedicated `mutationConnectionString` secret. The workflow requalifies and reacquires evidence; it does not trust old observations or caller JSON.
6. **EXPECTED:** PRE → FORWARD1 → POST1 → ROLLBACK → PRE2 → FORWARD2 → POST2; the target remains at POST2, not PRE. No final cleanup rollback is performed.
7. **REVIEW:** `phase-journal.jsonl`, `legacy-rehearsal-receipt.json`, its hash/provenance, all seven phase outcomes, all four captures, required DATA/SECURITY coverage, rollback/reapply equality, `REHEARSAL_COMPLETE`, and the real promotion-freeze artifact. A synthetic receipt is never runtime PASS.
8. **STOP / RECOVERY:** any failed/partial capture or write, timeout, cancellation, expired grant, changed source/package, drift, coverage or equality failure stops. Preserve the journal and connection/runner diagnostics under their controlled access policy. Inspect the target read-only to establish actual state; obtain a new human decision for recovery. Do not rerun jobs, skip phases, execute rollback manually under the old grant, change Registry or label a partial run successful.

Reusable workflows have `workflow_call`, not `workflow_dispatch`. They must be invoked through an authorized application caller.
Example call shape below is a **template**, not an installed workflow; replace every bracketed selector with approved real values after publication:

```yaml
jobs:
  legacy_test:
    uses: infrastructure-services/workflow/.github/workflows/legacy-rehearsal-v1-test.yml@<published-workflow-sha>
    with:
      targetId: <approved-target-id>
      manifestPath: <approved-manifest-path>
      expectedPackageIdentity: <approved-lpqv1-identity>
      actionsRevision: <published-actions-main-sha>
      authorizationSelector: <approved-grant-selector>
      dataContractSelector: <approved-data-selector-or-empty>
      executionAuthorized: true
    secrets:
      inspectionConnectionString: ${{ secrets.<approved-inspection-secret> }}
      mutationConnectionString: ${{ secrets.<approved-test-mutation-secret> }}
      governanceToken: ${{ secrets.<approved-repository-read-secret> }}
```

For the preparation call, select `legacy-rehearsal-preflight-v1-test.yml` and omit `executionAuthorized` and the mutation secret.
For A/B, select `database-discovery-v2-test.yml`, pass only `applicationId` and `databaseName` and the two read-only secrets.

## Human checkpoints

Separate approvals remain necessary for publishing the changeset; selecting/provisioning/configuring real targets; onboarding/Registry/baseline changes;
approving a real DATA definition; TEST read-only access; and the complete Legacy forward/rollback/reapply sequence.
After any uncertain or failed mutation, recovery needs a new decision. Nothing in this document authorizes those operations now.
