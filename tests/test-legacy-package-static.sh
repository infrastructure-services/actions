#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/tools/DatabaseReleaseQualification"
SECURITY="$ENGINE/SqlRecoverySecurityCatalogReader.cs"
SAFETY="$ENGINE/SqlLegacyScopeSafetySource.cs"
GIT="$ENGINE/LegacyArtifactDiscovery.cs"
RUNTIME="$ENGINE/LegacyProductionCompositionRoot.cs"

if grep -Eiq 'ExecuteNonQuery|\.Prepare\(|SqlParameter|BeginTransaction|ExecuteSqlAsync\(' "$SECURITY" "$SAFETY"; then
  echo "FAIL: legacy catalog source includes a mutating command API"
  exit 1
fi

for marker in 'SqlConnectionEncryptOption.Strict' 'TrustServerCertificate = false' \
  'ConnectRetryCount = 0' 'Pooling = false' 'MultipleActiveResultSets = false' \
  'ApplicationIntent = ApplicationIntent.ReadWrite' 'SecuritySqlGuard.Validate(sql)' \
  'ReadAsync(RecoverySecurityScope scope'; do
  if ! grep -Fq "$marker" "$SECURITY"; then
    echo "FAIL: security adapter missing required boundary: $marker"
    exit 1
  fi
done

for marker in 'GIT_NO_REPLACE_OBJECTS' 'GIT_NO_LAZY_FETCH' 'UseShellExecute = false' \
  'cat-file' 'ls-tree' 'LegacyPortablePath.Validate'; do
  if ! grep -Fq "$marker" "$GIT"; then
    echo "FAIL: Git provenance missing required boundary: $marker"
    exit 1
  fi
done

if grep -RIl --include='Legacy*.cs' 'ExecuteSqlAsync(' "$ENGINE" | grep -q .; then
  echo "FAIL: LEGACY qualification calls a SQL executor"
  exit 1
fi

for marker in 'LEGACY_JOB_WORKFLOW_SHA' 'RunDiscoveryProducer("repository-discovery-v2"' \
  'RunDiscoveryProducer("sql-discovery-v2"' 'ReadDiscoveryOutput(repositoryOutput' \
  'ReadDiscoveryOutput(sqlOutput' 'RemoteMain(governanceGit' \
  'RemoteMain(actionsGit' 'CaptureWithMetadataAsync('; do
  if ! grep -Fq "$marker" "$RUNTIME"; then
    echo "FAIL: runtime authority linkage missing: $marker"
    exit 1
  fi
done
if grep -Eq 'LEGACY_(REPOSITORY|SQL)_DISCOVERY_JSON' "$RUNTIME"; then
  echo 'FAIL: discovery JSON imported from caller environment'
  exit 1
fi

echo "OK: LEGACY package static read-only and Git boundaries present"
