#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/tools/DatabaseReleaseQualification"
HARNESS="$ENGINE/LegacyRehearsal.cs"
SQL="$ENGINE/SqlLegacyRehearsalRuntime.cs"
CLI="$ENGINE/LegacyRehearsalCli.cs"
for required in 'LegacyRehearsalStateMachineV1' 'CheckFrozenEvidence()' 'DATA_VALIDATOR_REQUIRED' 'SECURITY_DRIFT_BEFORE_MUTATION' 'REHEARSAL_RESUME_NOT_SUPPORTED' 'RehearsalEngine().QualifyAsync' 'RecoveryCoverage?.Complete' 'REHEARSAL_BLOCKED'; do
  grep -Fq "$required" "$HARNESS"
done
for required in 'BeginTransactionAsync(token)' 'transaction.CommitAsync(token)' 'command.CommandTimeout = 60' 'ExecuteNonQueryAsync(token)' 'MUTATION_CONNECTION_TARGET_MISMATCH' 'ExactBatches(script)' 'VerifyPhaseAsync'; do
  grep -Fq "$required" "$SQL"
done
if grep -Eq 'RollbackAsync|\.Rollback\(|while\s*\(|Retry|Console\.' "$SQL"; then
  echo 'FAIL executor retries, cleanup or unfiltered output'; exit 1
fi
for required in 'request.ExecutionAuthorized' 'ReadGovernedDocumentAsync' 'DATA_PROVIDER_CONTRACT_INVALID' 'GITHUB_RUN_ATTEMPT' 'FileMode.CreateNew' 'journal.Flush(true)' 'SCOPED_ROWSET_SHA256_V1'; do
  grep -Fq "$required" "$CLI"
done
if grep -Eq 'ExecuteNonQuery|BeginTransaction' "$ENGINE/SqlLegacyDataEvidenceReader.cs"; then
  echo 'FAIL DATA evidence reader contains mutation API'; exit 1
fi
grep -Fq 'Produced.TryGetValue(receipt' "$ENGINE/LegacyRehearsalAuthorization.cs"
echo 'OK: Macro2 explicit authority, exact executor, DATA reader and receipt boundaries'
