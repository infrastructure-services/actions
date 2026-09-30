# Legacy phased rehearsal V1 — TEST runtime boundary

The published reusable workflow `legacy-rehearsal-v1-test.yml` contains four
ordered jobs: read-only `legacy_pre`, then `legacy_forward1`,
`legacy_rollback`, and `legacy_forward2`. The last three each reference
the **same** `legacy-rehearsal-test` GitHub Environment. GitHub creates a
separate deployment for each job, so each job waits for a new review before
its own mutation. The workflow holds a target-wide concurrency lock across
the entire run. Reruns are refused.

Before any real run, repository administrators must configure
`legacy-rehearsal-test` in the **application repository** with Required
reviewers and Prevent self-review, and verify that the repository's GitHub
plan supports required reviewers. Do not use an environment with no
protection rules or administrator bypass. No repository setting is changed
by this code. The mutation credential must only be available to the three
protected jobs and must be scoped to the governed TEST target.

The PRE job requalifies Macro 1, verifies the exact Git package and target
binding, captures PRE structure and required DATA/SECURITY evidence, then
uploads a versioned checkpoint. Each mutating job downloads only its
predecessor artifact from the same run and receives the predecessor hash
through `needs`. The phase CLI verifies both, the run and job identities,
workflow/actions revisions, target and package identities, baseline,
precondition, evidence set, phase order, timestamps, and complete prior
evidence. A hash alone is never authority. The job reacquires current
governance and source provenance, checks freshness, validates current SQL
structure, DATA and SECURITY against the checkpoint, then executes only its
named script through a single-use runtime. Its actual SQL mutation connection
checks server and database before opening the phase transaction.

`FORWARD1` captures POST1. `ROLLBACK` captures PRE2 and requires PRE/PRE2
equality. `FORWARD2` captures POST2, requires POST1/POST2 equality, computes
Recovery Coverage, and emits the final receipt and eligible freeze. No job
can execute another phase. There is no `--resume`, arbitrary phase override,
or caller-supplied previous state.

Any failure, timeout, cancellation, missing artifact, stale checkpoint,
identity mismatch, incomplete evidence or uncertain commit stops the chain.
Neither rollback nor the next phase starts on its own. The partial journal and
sanitized block code are uploaded when possible. Human operators inspect
the target read-only and obtain a new approval before a later, separately
designed recovery mutation. The old monolithic CLI is retained only for
synthetic regression and is not called by the production workflow.
