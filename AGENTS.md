# Repository agent rules — actions

Purpose: shared discovery, Schema Capture and Database Release Qualification implementation for CI/CD DB SQL.

Canonical governance is in `C:\Repos\workflow\docs\project`. Read `01_MASTER_CONTEXT.md`, `02_CURRENT_STATE.md`, `03_ROADMAP.md`, then `05_DECISIONS.md`. The sole complete invariant list is in `01_MASTER_CONTEXT.md`.

- Confirm branch, HEAD and working tree; preserve user changes and stay within requested scope.
- Maintain fail-closed behavior, read-only guarantees, append-only records, evidence/storage separation and Registry/store separation by reference to the canonical invariants.
- Do not add real SQL execution, choose persistence, or implement roadmap work unless explicitly requested.
- Do not fetch/pull, switch branches, commit or push without explicit instruction.
- Safe baseline commands: `dotnet restore tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj --configfile tools/DatabaseReleaseQualification/NuGet.Config`; `dotnet build tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-restore`; `dotnet run --project tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj`; relevant `bash tests/test-*.sh` guards. Obtain authorization for networked restore.

Required closeout:

```text
TASK-CLOSEOUT
Task:
Status:

Implemented:
Tests:
Decisions:
Files changed:
Risks:
Remaining:
Next recommended task:
```
