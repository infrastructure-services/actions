#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONTRACT="$ROOT/tools/DatabaseReleaseQualification/EfReleasePlan.cs"

# A pure evidence API must not grow an I/O path or inherit pilot authority.
if grep -En 'SqlConnection|SqlCommand|ExecuteNonQuery|ExecuteSqlAsync|QualifyAsync|HttpClient|Process[.]|Environment[.]|File[.]|Directory[.]|System[.]IO|Microsoft[.]Data' "$CONTRACT"; then
  echo 'FAIL: EF plan composer contains an execution/acquisition API'; exit 1
fi
if grep -Ein 'CICDV3|3602|sqlv1testdcsrv1|SEBLOB|Luana|20260701161500|Owner' "$CONTRACT"; then
  echo 'FAIL: EF plan composer contains pilot-specific policy'; exit 1
fi
if grep -En 'ExecutionAuthorized[[:space:]]*=>[[:space:]]*true' "$CONTRACT"; then
  echo 'FAIL: evidence consistency grants execution'; exit 1
fi
for required in 'PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED' 'PRE_MATCH_NOT_AUTHORIZED' 'POST_MATCH_NOT_CERTIFIED' 'RECOVERY_STRATEGY_EVIDENCE_ADAPTER_REQUIRED'; do
  grep -Fq "$required" "$CONTRACT"
done
echo 'OK: EF plan is generic, pure and grants no execution authority'
