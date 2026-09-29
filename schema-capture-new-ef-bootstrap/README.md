# NEW EF read-only bootstrap source

This dedicated TEST producer performs the existing two SELECT-only captures and identity/determinism comparison. It selects the bootstrap stage before any Registry V1 lookup. It accepts no Registry V1 inputs, rejects inherited Registry context, and never invokes `evaluate-database-state` in this stage.

On successful capture it emits the existing `registry-status` and `drift-status` outputs as `NOT_EVALUATED`, with `gate-status: BLOCKED` and reason `NEW_EF_CERTIFICATION_NOT_EVALUATED`. Failures do not emit these intentional states. No observed certification result is converted or suppressed.

The governed consumer must use `compositionContractVersion: 2`, NEW/EF declarations, independently registered governance and explicit onboarding, plus SCHEMA_CAPTURE provenance identifying `infrastructure-services/actions` / `schema-capture-new-ef-bootstrap/action.yml` at the pinned revision and exact-file hash. Existing provenance fields identify this source; no new fields or enums are introduced. Composition V1 and historical evidence do not acquire the exception.

The ordinary `schema-capture` producer retains mandatory Registry V1 evaluation and its original outputs. BASELINE_REQUIRED remains blocking. CERTIFIED fixtures remain compatible through composition V1. EXISTING cannot use composition V2.

Physical emptiness must independently satisfy EMPTY_FOR_NEW_EF V1. Successful bootstrap classification grants only ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE. It does not provision a database, approve rehearsal, apply migrations, certify a schema, authorize release, or establish rollback readiness. Rehearsal retains its MANAGED onboarding/lineage requirement; certification follows a valid qualified transition under its separate contract.
