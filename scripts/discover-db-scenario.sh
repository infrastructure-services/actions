#!/usr/bin/env bash
set -euo pipefail

ACTION_PATH="${GITHUB_ACTION_PATH:?GITHUB_ACTION_PATH no informado}"
WORKSPACE="${GITHUB_WORKSPACE:-$PWD}"
LIFECYCLE="$(printf '%s' "${DATABASE_LIFECYCLE:-}" | tr '[:lower:]' '[:upper:]' | tr -d '[:space:]')"
OUTPUT_ROOT="${OUTPUT_DIRECTORY:-artifacts/db-discovery}"
SAFE_ENVIRONMENT="$(printf '%s' "${ENVIRONMENT_NAME:-TEST}" | sed 's#[^A-Za-z0-9._-]#_#g')"

if [[ -z "$OUTPUT_ROOT" || "$OUTPUT_ROOT" == /* || "$OUTPUT_ROOT" =~ (^|/)\.\.(/|$) ]]; then
  echo "output-directory debe ser un path relativo dentro del workspace."
  exit 1
fi

REPORT_RELATIVE_DIRECTORY="${OUTPUT_ROOT%/}/$SAFE_ENVIRONMENT"
REPORT_DIRECTORY="$WORKSPACE/$REPORT_RELATIVE_DIRECTORY"
REPORT_FILE="$REPORT_DIRECTORY/discovery.json"
SUMMARY_FILE="$REPORT_DIRECTORY/summary.md"
REPO_MIGRATIONS_FILE="$REPORT_DIRECTORY/repo-migrations.txt"
DB_MIGRATIONS_FILE="$REPORT_DIRECTORY/db-migrations.txt"
TEMP_ROOT="${RUNNER_TEMP:-/tmp}/db-sql-discovery-${GITHUB_RUN_ID:-local}-${GITHUB_JOB:-job}-${RANDOM}"
SQL_DISCOVERY_FILE="$TEMP_ROOT/sql-discovery.json"
SQL_STDERR="$TEMP_ROOT/sql-discovery.stderr"
SQL_BUILD_ROOT="$TEMP_ROOT/build"

mkdir -p "$REPORT_DIRECTORY" "$TEMP_ROOT"
touch "$REPO_MIGRATIONS_FILE"
: > "$DB_MIGRATIONS_FILE"

cleanup() {
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT

DATABASE_NAME=""
BUSINESS_OBJECT_COUNT=0
BUSINESS_TABLE_COUNT=0
EMPTY_FOR_NEW=false
EF_HISTORY_EXISTS=false
METADATA_VISIBILITY_VERIFIED=false
STRUCTURAL_COUNTS_JSON='{}'
CLASSIFICATION_DATA=""

sha256_file() {
  local FILE="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$FILE" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$FILE" | awk '{print $1}'
  else
    echo "No existe una herramienta SHA256 en el runner."
    exit 1
  fi
}

classification_value() {
  local KEY="$1"
  printf '%s\n' "$CLASSIFICATION_DATA" | sed -n "s/^${KEY}=//p" | head -n 1
}

run_classifier() {
  local ACCESS_STATUS="$1"
  CLASSIFICATION_DATA="$(bash "$ACTION_PATH/scripts/classify-scenario.sh" \
    "$LIFECYCLE" \
    "$ACCESS_STATUS" \
    "${REPO_HAS_MIGRATIONS:-false}" \
    "$BUSINESS_OBJECT_COUNT" \
    "$EF_HISTORY_EXISTS" \
    "$REPO_MIGRATIONS_FILE" \
    "$DB_MIGRATIONS_FILE" \
    "${REPOSITORY_STATUS:-READY}" \
    "${REPOSITORY_REASON:-OK}")"
}

write_report_and_outputs() {
  local CONSISTENCY_STATUS CONSISTENCY_REASON SCENARIO SOURCE_KIND
  local LAST_APPLIED LATEST_REPO HAS_PENDING REPO_COUNT HISTORY_COUNT
  local REPO_SHA HISTORY_SHA

  CONSISTENCY_STATUS="$(classification_value consistency_status)"
  CONSISTENCY_REASON="$(classification_value consistency_reason)"
  SCENARIO="$(classification_value scenario)"
  SOURCE_KIND="$(classification_value source_kind)"
  LAST_APPLIED="$(classification_value last_applied_migration)"
  LATEST_REPO="$(classification_value latest_repo_migration)"
  HAS_PENDING="$(classification_value has_pending_migrations)"
  REPO_COUNT="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
  HISTORY_COUNT="$(sed '/^[[:space:]]*$/d' "$DB_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
  REPO_SHA="$(sha256_file "$REPO_MIGRATIONS_FILE")"
  HISTORY_SHA="$(sha256_file "$DB_MIGRATIONS_FILE")"

  jq -n \
    --arg lifecycle "$LIFECYCLE" \
    --arg environment "$SAFE_ENVIRONMENT" \
    --arg databaseName "$DATABASE_NAME" \
    --arg scenario "$SCENARIO" \
    --arg sourceKind "$SOURCE_KIND" \
    --arg consistencyStatus "$CONSISTENCY_STATUS" \
    --arg consistencyReason "$CONSISTENCY_REASON" \
    --arg lastAppliedMigration "$LAST_APPLIED" \
    --arg latestRepoMigration "$LATEST_REPO" \
    --arg selectedMigrationProject "${SELECTED_MIGRATION_PROJECT:-}" \
    --arg repoMigrationsSha256 "$REPO_SHA" \
    --arg efHistorySha256 "$HISTORY_SHA" \
    --argjson businessObjectCount "$BUSINESS_OBJECT_COUNT" \
    --argjson businessTableCount "$BUSINESS_TABLE_COUNT" \
    --argjson emptyForNew "$EMPTY_FOR_NEW" \
    --argjson efHistoryExists "$EF_HISTORY_EXISTS" \
    --argjson repoHasMigrations "${REPO_HAS_MIGRATIONS:-false}" \
    --argjson modelSnapshotExists "${MODEL_SNAPSHOT_EXISTS:-false}" \
    --argjson hasPendingMigrations "$HAS_PENDING" \
    --argjson metadataVisibilityVerified "$METADATA_VISIBILITY_VERIFIED" \
    --argjson structuralCounts "$STRUCTURAL_COUNTS_JSON" \
    --rawfile repoMigrations "$REPO_MIGRATIONS_FILE" \
    --rawfile dbMigrations "$DB_MIGRATIONS_FILE" \
    '{
      databaseLifecycle: $lifecycle,
      environmentName: $environment,
      databaseName: $databaseName,
      scenario: $scenario,
      sourceKind: $sourceKind,
      businessObjectCount: $businessObjectCount,
      businessTableCount: $businessTableCount,
      emptyForNew: $emptyForNew,
      efHistoryExists: $efHistoryExists,
      efHistoryCount: (($dbMigrations | split("\n") | map(select(length > 0))) | length),
      efHistorySha256: $efHistorySha256,
      efHistoryList: ($dbMigrations | split("\n") | map(select(length > 0))),
      repoHasMigrations: $repoHasMigrations,
      repoMigrationCount: (($repoMigrations | split("\n") | map(select(length > 0))) | length),
      repoMigrationsSha256: $repoMigrationsSha256,
      repoMigrationList: ($repoMigrations | split("\n") | map(select(length > 0))),
      selectedMigrationProject: $selectedMigrationProject,
      modelSnapshotExists: $modelSnapshotExists,
      lastAppliedMigration: $lastAppliedMigration,
      latestRepoMigration: $latestRepoMigration,
      hasPendingMigrations: $hasPendingMigrations,
      consistencyStatus: $consistencyStatus,
      consistencyReason: $consistencyReason,
      metadataVisibilityVerified: $metadataVisibilityVerified,
      structuralCounts: $structuralCounts,
      technicalSchemaExclusions: ["sys", "INFORMATION_SCHEMA", "cicd"],
      technicalObjectExclusions: ["dbo.__EFMigrationsHistory"],
      sqlAccessMode: "SELECT_ONLY_PRIMARY"
    }' > "$REPORT_FILE"

  {
    echo "### DB Scenario Discovery — $SAFE_ENVIRONMENT"
    echo ""
    echo "| Campo | Valor |"
    echo "|---|---|"
    printf '| Lifecycle declarado | `%s` |\n' "$LIFECYCLE"
    printf '| Escenario detectado | `%s` |\n' "$SCENARIO"
    printf '| Source kind | `%s` |\n' "$SOURCE_KIND"
    printf '| Base inspeccionada | `%s` |\n' "${DATABASE_NAME:-No disponible}"
    printf '| Objetos/estructuras de negocio | `%s` |\n' "$BUSINESS_OBJECT_COUNT"
    printf '| Tablas de negocio | `%s` |\n' "$BUSINESS_TABLE_COUNT"
    printf '| Vacía para NEW | `%s` |\n' "$EMPTY_FOR_NEW"
    printf '| Visibilidad metadata verificada | `%s` |\n' "$METADATA_VISIBILITY_VERIFIED"
    printf '| EF history detectada | `%s` |\n' "$EF_HISTORY_EXISTS"
    printf '| Migraciones repo | `%s` |\n' "$REPO_COUNT"
    printf '| Migraciones aplicadas | `%s` |\n' "$HISTORY_COUNT"
    printf '| SHA256 repo | `%s` |\n' "$REPO_SHA"
    printf '| SHA256 history | `%s` |\n' "$HISTORY_SHA"
    printf '| Proyecto de migraciones | `%s` |\n' "${SELECTED_MIGRATION_PROJECT:-none}"
    printf '| Última aplicada | `%s` |\n' "${LAST_APPLIED:-none}"
    printf '| Última disponible | `%s` |\n' "${LATEST_REPO:-none}"
    printf '| Consistency status | `%s` |\n' "$CONSISTENCY_STATUS"
    printf '| Consistency reason | `%s` |\n' "$CONSISTENCY_REASON"
    echo ""
    echo "El helper SQL ejecuta únicamente SELECT y solicita conexión al primary."
  } > "$SUMMARY_FILE"

  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    cat "$SUMMARY_FILE" >> "$GITHUB_STEP_SUMMARY"
  fi

  {
    echo "scenario=$SCENARIO"
    echo "source_kind=$SOURCE_KIND"
    echo "consistency_status=$CONSISTENCY_STATUS"
    echo "consistency_reason=$CONSISTENCY_REASON"
    echo "database_name=$DATABASE_NAME"
    echo "business_object_count=$BUSINESS_OBJECT_COUNT"
    echo "business_table_count=$BUSINESS_TABLE_COUNT"
    echo "empty_for_new=$EMPTY_FOR_NEW"
    echo "ef_history_exists=$EF_HISTORY_EXISTS"
    echo "ef_history_count=$HISTORY_COUNT"
    echo "ef_history_sha256=$HISTORY_SHA"
    echo "last_applied_migration=$LAST_APPLIED"
    echo "repo_has_migrations=${REPO_HAS_MIGRATIONS:-false}"
    echo "repo_migration_count=$REPO_COUNT"
    echo "repo_migrations_sha256=$REPO_SHA"
    echo "latest_repo_migration=$LATEST_REPO"
    echo "has_pending_migrations=$HAS_PENDING"
    echo "selected_migration_project=${SELECTED_MIGRATION_PROJECT:-}"
  } >> "$GITHUB_OUTPUT"
}

if ! command -v jq >/dev/null 2>&1; then
  echo "jq es requerido por discover-db-scenario."
  exit 1
fi

if [[ "$LIFECYCLE" != "NEW" && "$LIFECYCLE" != "EXISTING" ]]; then
  run_classifier READY
  write_report_and_outputs
  exit 0
fi

if [[ "${SECRET_FOUND,,}" != "true" || -z "${DB_CONNECTION:-}" ]]; then
  run_classifier SECRET_MISSING
  write_report_and_outputs
  exit 0
fi

echo "::add-mask::$DB_CONNECTION"

if [[ "${REPOSITORY_STATUS:-READY}" != "READY" ]]; then
  run_classifier READY
  write_report_and_outputs
  exit 0
fi

if [[ "${DOTNET_SETUP_OUTCOME:-success}" != "success" ]]; then
  run_classifier HELPER_FAILED
  write_report_and_outputs
  exit 0
fi

set +e
dotnet restore "$ACTION_PATH/tools/SqlDiscovery/SqlDiscovery.csproj" \
  --configfile "$ACTION_PATH/tools/SqlDiscovery/NuGet.Config" \
  --property:BaseIntermediateOutputPath="$SQL_BUILD_ROOT/obj/" \
  >/dev/null 2> "$SQL_STDERR"
SQL_RESTORE_EXIT=$?

if [[ "$SQL_RESTORE_EXIT" -eq 0 ]]; then
  dotnet build "$ACTION_PATH/tools/SqlDiscovery/SqlDiscovery.csproj" \
    --no-restore \
    --configuration Release \
    --property:BaseOutputPath="$SQL_BUILD_ROOT/bin/" \
    --property:BaseIntermediateOutputPath="$SQL_BUILD_ROOT/obj/" \
    >/dev/null 2>> "$SQL_STDERR"
  SQL_BUILD_EXIT=$?
else
  SQL_BUILD_EXIT=1
fi

if [[ "$SQL_RESTORE_EXIT" -eq 0 && "$SQL_BUILD_EXIT" -eq 0 ]]; then
  DB_CONNECTION="$DB_CONNECTION" dotnet run \
    --project "$ACTION_PATH/tools/SqlDiscovery/SqlDiscovery.csproj" \
    --configuration Release \
    --no-build \
    --no-restore \
    --property:BaseOutputPath="$SQL_BUILD_ROOT/bin/" \
    --property:BaseIntermediateOutputPath="$SQL_BUILD_ROOT/obj/" \
    > "$SQL_DISCOVERY_FILE" 2>> "$SQL_STDERR"
  SQL_EXIT=$?
else
  SQL_EXIT=1
fi
set -e
unset DB_CONNECTION

if [[ "$SQL_RESTORE_EXIT" -ne 0 || "$SQL_BUILD_EXIT" -ne 0 ]]; then
  run_classifier HELPER_FAILED
  write_report_and_outputs
  exit 0
fi

if [[ "$SQL_EXIT" -eq 5 ]]; then
  run_classifier METADATA_INSUFFICIENT
  write_report_and_outputs
  exit 0
fi

if [[ "$SQL_EXIT" -eq 4 || "$SQL_EXIT" -eq 2 ]]; then
  run_classifier HELPER_FAILED
  write_report_and_outputs
  exit 0
fi

if [[ "$SQL_EXIT" -ne 0 ]]; then
  run_classifier CONNECTION_FAILED
  write_report_and_outputs
  exit 0
fi

if ! jq empty "$SQL_DISCOVERY_FILE" >/dev/null 2>&1; then
  run_classifier HELPER_FAILED
  write_report_and_outputs
  exit 0
fi

DATABASE_NAME="$(jq -r '.databaseName // ""' "$SQL_DISCOVERY_FILE")"
BUSINESS_OBJECT_COUNT="$(jq -r '.businessObjectCount // 0' "$SQL_DISCOVERY_FILE")"
BUSINESS_TABLE_COUNT="$(jq -r '.businessTableCount // 0' "$SQL_DISCOVERY_FILE")"
EMPTY_FOR_NEW="$(jq -r '.emptyForNew // false' "$SQL_DISCOVERY_FILE")"
EF_HISTORY_EXISTS="$(jq -r '.efHistoryExists // false' "$SQL_DISCOVERY_FILE")"
METADATA_VISIBILITY_VERIFIED="$(jq -r '.metadataVisibilityVerified // false' "$SQL_DISCOVERY_FILE")"
STRUCTURAL_COUNTS_JSON="$(jq -c '.structuralCounts // {}' "$SQL_DISCOVERY_FILE")"
jq -r '.efHistory[]?' "$SQL_DISCOVERY_FILE" > "$DB_MIGRATIONS_FILE"

run_classifier READY
write_report_and_outputs
