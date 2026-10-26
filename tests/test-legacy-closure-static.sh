#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/tools/DatabaseReleaseQualification"
for source in LegacyAdoption.cs LegacyCertificationContinuation.cs LegacyExecutionFence.cs; do
  if grep -Eq 'SqlConnection|ExecuteNonQuery|Process[.]Start|HttpClient' "$ENGINE/$source"; then
    echo "FAIL: lifecycle contract contains physical I/O: $source"; exit 1
  fi
done
grep -Fq 'LEGACY_CERTIFICATION_AUTHORITY_AND_STORE_UNBOUND' "$ENGINE/LegacyCertificationContinuation.cs"
grep -Fq 'private readonly InMemoryCertificationCommitStore store' "$ENGINE/LegacyCertificationContinuation.cs"
grep -Fq 'CorporateCertified = false' "$ENGINE/LegacyCertificationContinuation.cs"
grep -Fq 'LegacyExecutionFence.AcquireProtected' "$ENGINE/LegacyPhasedCli.cs"
grep -Fq 'executionLease!.Confirm(checkpoint.CheckpointHash)' "$ENGINE/LegacyPhasedCli.cs"
grep -Fq 'executionLease?.PreserveFailure()' "$ENGINE/LegacyPhasedCli.cs"
grep -Fq 'DURABLE_EXECUTION_FENCE_REQUIRED' "$ENGINE/SqlLegacyRehearsalRuntime.cs"
grep -Fq 'executionLease.Begin(handoff, context, phase)' "$ENGINE/SqlLegacyRehearsalRuntime.cs"
grep -Fq 'LEGACY_PHASED_EXECUTION_REQUIRED' "$ENGINE/LegacyRehearsalCli.cs"
grep -Fq 'runtime is SqlLegacyRehearsalRuntimeV1' "$ENGINE/LegacyPhasedRehearsal.cs"
grep -Fq 'public SqlScriptAnalyzer() : this(LegacySqlProfile.Default)' "$ENGINE/SqlScriptAnalyzer.cs"
grep -Fq 'public SqlServerSchemaReader() : this(LegacySqlProfile.Default)' "$ENGINE/SqlServerSchemaReader.cs"
grep -Fq 'SchemaCaptureSourceResult> CaptureWithMetadataAsync' "$ENGINE/LegacySchemaCapture.cs"
if grep -Eq 'ExecuteNonQuery|BeginTransaction|ApplyExactAsync' "$ENGINE/LegacySchemaCapture.cs"; then
  echo 'FAIL: LEGACY view enrichment contains a mutation API'; exit 1
fi
echo 'OK: LEGACY lifecycle, fencing, profile isolation and synthetic boundaries'
