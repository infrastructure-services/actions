#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACTION_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ENGINE="$ACTION_ROOT/tools/DatabaseReleaseQualification"
SCHEMA_READER="$ENGINE/SqlServerSchemaReader.cs"
ACTION="$ACTION_ROOT/action.yml"
RUNNER="$ACTION_ROOT/scripts/run-database-release-qualification.sh"
MUTATING_PATTERN='(^|[^[:alnum:]_])(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE)([^[:alnum:]_]|$)'

if grep -Eiq "$MUTATING_PATTERN" "$SCHEMA_READER"; then
  echo "FAIL: SqlServerSchemaReader contiene SQL no-SELECT."
  grep -Ein "$MUTATING_PATTERN" "$SCHEMA_READER" || true
  exit 1
fi

if grep -Eiq 'ExecuteNonQuery|MultipleActiveResultSets' "$SCHEMA_READER"; then
  echo "FAIL: el capturador read-only contiene una API mutante o dependencia de MARS."
  exit 1
fi

for required_catalog in \
  'HAS_PERMS_BY_NAME' \
  'sys.schemas' \
  'sys.tables' \
  'sys.columns' \
  'sys.computed_columns' \
  'sys.default_constraints' \
  'sys.key_constraints' \
  'sys.check_constraints' \
  'sys.foreign_keys' \
  'sys.indexes' \
  'sys.data_spaces' \
  'sys.triggers' \
  'sys.views' \
  'sys.sql_expression_dependencies' \
  'sys.sequences' \
  'sys.synonyms' \
  'sys.types' \
  'sys.partition_functions' \
  'sys.partition_schemes' \
  'sys.fulltext_indexes' \
  'sys.stats' \
  'temporal_type_desc' \
  'sys.dm_db_partition_stats'
do
  if ! grep -Fq "$required_catalog" "$SCHEMA_READER"; then
    echo "FAIL: falta cobertura de schema/impacto requerida: $required_catalog"
    exit 1
  fi
done

if grep -Eiq 'connection-string|DB_CONNECTION|password|secret-value|Key Vault|sqlcmd|Invoke-Sqlcmd' "$ACTION" "$RUNNER"; then
  echo "FAIL: la action analyze-only no debe recibir credenciales ni conectarse a SQL."
  exit 1
fi

if ! grep -Fq 'Microsoft.SqlServer.TransactSql.ScriptDom' "$ENGINE/DatabaseReleaseQualification.csproj" \
  || ! grep -Fq 'TSql180Parser' "$ENGINE/SqlScriptAnalyzer.cs"; then
  echo "FAIL: ScriptDom AST debe ser la autoridad del análisis T-SQL."
  exit 1
fi

if grep -Eq 'System\.Text\.RegularExpressions|Regex\.' "$ENGINE/SqlScriptAnalyzer.cs"; then
  echo "FAIL: SqlScriptAnalyzer no debe depender de regex como autoridad de parsing."
  exit 1
fi

if grep -Eiq 'apply-db-migrations|dotnet[[:space:]]+ef[[:space:]]+database[[:space:]]+update|helm|argo|deploy\.sql' "$ACTION" "$RUNNER"; then
  echo "FAIL: la action V1 referencia deployment o migración fuera de alcance."
  exit 1
fi

if grep -RIl --include='*.cs' 'IRehearsalDatabase' "$ENGINE" \
  | grep -vF 'QualificationEngine.cs' >/dev/null; then
  echo "FAIL: existe un adaptador de rehearsal real fuera del contrato explícito."
  exit 1
fi

if grep -RIl --include='*.cs' 'ExecuteSqlAsync(' "$ENGINE" \
  | grep -vF 'QualificationEngine.cs' >/dev/null; then
  echo "FAIL: SQL mutante aparece fuera del módulo explícito de rehearsal."
  exit 1
fi

for required in \
  'BLOCKED_DISCOVERY' \
  'BLOCKED_PROD_REHEARSAL' \
  'ROLLBACK_ANALYSIS_BASIS:POST1' \
  'BLOCKED_POST1_ROLLBACK_ANALYSIS_CONFIDENCE' \
  'post1RollbackAnalysis' \
  'post1Snapshot' \
  'BLOCKED_SCHEMA_ROLLBACK_MISMATCH' \
  'BLOCKED_DATA_ROLLBACK_UNVERIFIED' \
  'BLOCKED_REAPPLY_MISMATCH' \
  'ROLLBACK_SHA256' \
  'REEXECUTED_FORWARD'
do
  if ! grep -Fq "$required" "$ENGINE/QualificationEngine.cs"; then
    echo "FAIL: falta guard o evidencia de rehearsal: $required"
    exit 1
  fi
done

for required_post1_contract in \
  'RollbackAgainstPost1' \
  'RollbackDependencyRisk' \
  'RollbackOperationalRisk' \
  'post1-rollback-analysis.json' \
  'preliminary-dependency-analysis.json'
do
  if ! grep -R -Fq "$required_post1_contract" "$ENGINE"; then
    echo "FAIL: falta evidencia/contrato autoritativo POST1: $required_post1_contract"
    exit 1
  fi
done

for required_model in \
  'SchemaRollbackValidity' \
  'DataRollbackValidity' \
  'RollbackCapability' \
  'ReleasePayloadMetadata' \
  'QualificationAttestation' \
  'TargetRiskEngine'
do
  if ! grep -R -Fq "$required_model" "$ENGINE"; then
    echo "FAIL: falta contrato de hardening: $required_model"
    exit 1
  fi
done

if grep -RiqE 'generate.*rollback|rollback.*generate|auto.*rollback|DROP INDEX.*CREATE INDEX' "$ENGINE"; then
  echo "FAIL: se detectó una posible generación/corrección automática de rollback."
  exit 1
fi

echo "OK: qualification V1 mantiene captura SELECT-only y rehearsal aislado sin adaptador SQL real"
