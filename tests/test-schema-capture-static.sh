#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACTION_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ROOT_ACTION="$ACTION_ROOT/action.yml"
CAPTURE_ACTION="$ACTION_ROOT/schema-capture/action.yml"
CAPTURE_README="$ACTION_ROOT/schema-capture/README.md"
CAPTURE_RUNNER="$ACTION_ROOT/schema-capture/run-schema-capture.sh"
ENGINE="$ACTION_ROOT/tools/DatabaseReleaseQualification"
READER="$ENGINE/SqlServerSchemaReader.cs"
PROGRAM="$ENGINE/Program.cs"
CAPTURE_MODEL="$ENGINE/SchemaCapture.cs"
STATE_EVALUATOR="$ENGINE/DatabaseStateEvaluator.cs"
MUTATING_PATTERN='(^|[^[:alnum:]_])(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE)([^[:alnum:]_]|$)'

if grep -Eiq 'connection-string|DB_CONNECTION|secret-value|password' "$ROOT_ACTION"; then
  echo 'FAIL: la root action analyze-only expone una conexión SQL.'
  exit 1
fi

for required in \
  'connection-string:' \
  'DB_CONNECTION: ${{ inputs.connection-string }}' \
  'run-schema-capture.sh' \
  'capture-1-hash:' \
  'capture-2-hash:' \
  'metrics-availability:' \
  'application-id:' \
  'registry-file:' \
  'registry-repository:' \
  'registry-ref:' \
  'registry-commit-sha:' \
  'registry-file-path:' \
  'registry-file-sha256:' \
  'observed-schema-hash:' \
  'certified-schema-hash:' \
  'drift-status:' \
  'gate-status:' \
  'baseline-candidate:'
do
  if ! grep -Fq "$required" "$CAPTURE_ACTION"; then
    echo "FAIL: falta contrato de schema-capture: $required"
    exit 1
  fi
done

for identity_contract in \
  'INSPECTION IDENTITY' \
  'DEPLOYMENT IDENTITY' \
  'db_owner' \
  'sysadmin' \
  'db_ddladmin' \
  'securityadmin' \
  'CONNECT' \
  'VIEW DEFINITION' \
  'VIEW DATABASE STATE' \
  'VIEW DATABASE PERFORMANCE STATE' \
  'VIEW SECURITY DEFINITION' \
  'ApplicationIntent=ReadWrite'
do
  if ! grep -Fq "$identity_contract" "$CAPTURE_README"; then
    echo "FAIL: falta requisito del contrato least-privilege: $identity_contract"
    exit 1
  fi
done

if ! grep -Fq 'nunca Owner/deployment' "$CAPTURE_ACTION" \
  || ! grep -Fq 'IdentityPurpose { get; init; } = "INSPECTION"' "$CAPTURE_MODEL"; then
  echo 'FAIL: la identidad de inspección no está modelada en action/metadata.'
  exit 1
fi

if grep -Fq -- '--connection-string' "$PROGRAM" "$CAPTURE_RUNNER" \
  || grep -Fq 'Required(options, "connection-string")' "$PROGRAM"; then
  echo 'FAIL: la connection string puede ingresar por argumentos del proceso.'
  exit 1
fi

if ! grep -Fq 'Environment.GetEnvironmentVariable("DB_CONNECTION")' "$PROGRAM"; then
  echo 'FAIL: capture-schema no lee la conexión exclusivamente desde DB_CONNECTION.'
  exit 1
fi

if [[ "$(grep -Fc 'dotnet build ' "$CAPTURE_RUNNER")" -ne 1 ]] \
  || ! grep -Fq 'run_capture capture-1' "$CAPTURE_RUNNER" \
  || ! grep -Fq 'run_capture capture-2' "$CAPTURE_RUNNER"; then
  echo 'FAIL: el runner debe compilar una vez y lanzar dos captures independientes.'
  exit 1
fi

if [[ "$(grep -Ec '^[[:space:]]*run_capture capture-[12] ' "$CAPTURE_RUNNER")" -ne 2 ]] \
  || [[ "$(grep -Fc 'evaluate-database-state' "$CAPTURE_RUNNER")" -ne 1 ]]; then
  echo 'FAIL: registry evaluation debe reutilizar dos captures y no ejecutar una tercera.'
  exit 1
fi

for required_artifact in \
  'canonical-schema.json' \
  'schema.sha256' \
  'metadata.json' \
  'impact-metrics.json' \
  'determinism.json' \
  'schema-diff.json' \
  'target.json' \
  'registry-evaluation.json' \
  'baseline-candidate.json' \
  'drift-analysis.json' \
  'summary.md'
do
  if ! grep -R -Fq "$required_artifact" "$ENGINE/SchemaCapture.cs" "$STATE_EVALUATOR" "$CAPTURE_RUNNER"; then
    echo "FAIL: falta artifact requerido: $required_artifact"
    exit 1
  fi
done

if grep -Eiq "$MUTATING_PATTERN" "$READER"; then
  echo 'FAIL: SqlServerSchemaReader contiene SQL mutante.'
  grep -Ein "$MUTATING_PATTERN" "$READER" || true
  exit 1
fi

if grep -Eiq 'DB_CONNECTION|SqlConnection|SqlCommand|SqlDataReader|ExecuteReader|ExecuteScalar|ExecuteNonQuery' "$STATE_EVALUATOR"; then
  echo 'FAIL: DatabaseStateEvaluator no puede abrir conexión ni ejecutar SQL.'
  exit 1
fi

for immutable_contract in \
  'REGISTRY_FILE_SHA256_AT_START' \
  'REGISTRY_FILE_SHA256_AFTER' \
  'REGISTRY_FILE_PATH_NORMALIZED' \
  'REGISTRY_IMMUTABILITY_VIOLATION' \
  'RegistryProvenance' \
  'RegistryFormatVersion' \
  'Registry format:' \
  'Registry commit:' \
  'REGISTRY_COMMIT_SHA:0:12' \
  'REGISTRY_FILE_SHA256_MISMATCH' \
  'NOT_CERTIFIED' \
  'HASH_MISMATCH' \
  'StructuralDiffAvailable = false'
do
  if ! grep -R -Fq "$immutable_contract" "$STATE_EVALUATOR" "$CAPTURE_RUNNER"; then
    echo "FAIL: falta protección de registry/drift: $immutable_contract"
    exit 1
  fi
done

if grep -Eiq 'ExecuteNonQuery|MultipleActiveResultSets' "$READER" "$CAPTURE_RUNNER" "$CAPTURE_ACTION"; then
  echo 'FAIL: schema capture contiene API mutante o dependencia de MARS.'
  exit 1
fi

if grep -Fq 'referenced_minor_name' "$READER" \
  || ! grep -Fq 'd.referenced_minor_id' "$READER" \
  || ! grep -Fq 'd.referenced_database_name' "$READER"; then
  echo 'FAIL: la captura de dependencias usa una columna inexistente o pierde el database referenciado.'
  exit 1
fi

for keyword in INSERT UPDATE DELETE MERGE CREATE ALTER DROP TRUNCATE EXEC EXECUTE; do
  if ! grep -Fq "$keyword" "$CAPTURE_MODEL"; then
    echo "FAIL: el runtime guard no bloquea $keyword."
    exit 1
  fi
done

if grep -Eiq 'dotnet[[:space:]]+ef|migrations|forward|rollback|apply-db-migrations|helm|argo' \
  "$CAPTURE_ACTION" "$CAPTURE_RUNNER" "$READER"; then
  echo 'FAIL: schema capture referencia ejecución fuera de su alcance read-only.'
  exit 1
fi

for required_status in \
  FAIL_DATABASE_UNREACHABLE \
  FAIL_METADATA_VISIBILITY \
  FAIL_SCHEMA_CAPTURE \
  FAIL_SCHEMA_CAPTURE_NONDETERMINISTIC \
  SUCCESS
do
  if ! grep -R -Fq "$required_status" "$CAPTURE_MODEL" "$CAPTURE_RUNNER"; then
    echo "FAIL: falta estado sanitizado: $required_status"
    exit 1
  fi
done

for version_guard in '>= 11' '>= 12' '>= 13' '>= 14' '>= 16'; do
  if ! grep -Fq "$version_guard" "$READER"; then
    echo "FAIL: falta degradación/versionado SQL Server: $version_guard"
    exit 1
  fi
done

echo 'OK: sub-action schema-capture aislada, doble, versionada y SELECT-only'
