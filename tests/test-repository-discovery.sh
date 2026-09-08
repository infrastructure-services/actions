#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DISCOVERY="$SCRIPT_DIR/../scripts/discover-repository.sh"
TEMP_DIR="$(mktemp -d)"
PASS_COUNT=0

cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

value() {
  local FILE="$1"
  local KEY="$2"
  sed -n "s/^${KEY}=//p" "$FILE" | head -n 1
}

create_project() {
  local ROOT="$1"
  local NAME="$2"
  mkdir -p "$ROOT"
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$ROOT/$NAME.csproj"
}

create_valid_migration() {
  local ROOT="$1"
  local ID="$2"
  local WITH_ATTRIBUTE="${3:-true}"
  mkdir -p "$ROOT/Migrations"
  printf 'public partial class MigrationBody {}\n' > "$ROOT/Migrations/$ID.cs"
  if [[ "$WITH_ATTRIBUTE" == true ]]; then
    printf '[Migration("%s")]\npartial class MigrationMetadata {}\n' "$ID" > "$ROOT/Migrations/$ID.Designer.cs"
  else
    printf 'partial class MigrationMetadata {}\n' > "$ROOT/Migrations/$ID.Designer.cs"
  fi
}

run_case() {
  local NAME="$1"
  local REPO="$2"
  local EXPECTED_STATUS="$3"
  local EXPECTED_REASON="$4"
  local EXPECTED_HAS="$5"
  local EXPECTED_COUNT="$6"
  local OUTPUT_FILE="$TEMP_DIR/${NAME// /_}.out"

  : > "$OUTPUT_FILE"
  GITHUB_WORKSPACE="$REPO" \
  GITHUB_OUTPUT="$OUTPUT_FILE" \
  RUNNER_TEMP="$TEMP_DIR" \
  ENVIRONMENT_NAME=TEST \
  OUTPUT_DIRECTORY=artifacts/db-discovery \
    bash "$DISCOVERY"

  [[ "$(value "$OUTPUT_FILE" repository_status)" == "$EXPECTED_STATUS" ]]
  [[ "$(value "$OUTPUT_FILE" repository_reason)" == "$EXPECTED_REASON" ]]
  [[ "$(value "$OUTPUT_FILE" repo_has_migrations)" == "$EXPECTED_HAS" ]]
  [[ "$(value "$OUTPUT_FILE" repo_migration_count)" == "$EXPECTED_COUNT" ]]

  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $NAME"
}

EMPTY_REPO="$TEMP_DIR/empty-repo"
create_project "$EMPTY_REPO" App
run_case "repo sin EF" "$EMPTY_REPO" READY OK false 0

VALID_REPO="$TEMP_DIR/valid-repo"
create_project "$VALID_REPO" App
create_valid_migration "$VALID_REPO" 20240101010101_Initial
create_valid_migration "$VALID_REPO" 20240202020202_AddOrders
run_case "migrations estáticas válidas" "$VALID_REPO" READY OK true 2
mapfile -t ORDERED_IDS < "$VALID_REPO/artifacts/db-discovery/TEST/repo-migrations.txt"
[[ "${ORDERED_IDS[0]}" == "20240101010101_Initial" ]]
[[ "${ORDERED_IDS[1]}" == "20240202020202_AddOrders" ]]

FALLBACK_REPO="$TEMP_DIR/fallback-repo"
create_project "$FALLBACK_REPO" App
create_valid_migration "$FALLBACK_REPO" 20240303030303_Fallback false
run_case "fallback por par de archivos válido" "$FALLBACK_REPO" READY OK true 1

SNAPSHOT_ONLY_REPO="$TEMP_DIR/snapshot-only-repo"
create_project "$SNAPSHOT_ONLY_REPO" App
mkdir -p "$SNAPSHOT_ONLY_REPO/Migrations"
printf 'public class AppModelSnapshot {}\n' > "$SNAPSHOT_ONLY_REPO/Migrations/AppModelSnapshot.cs"
run_case "ModelSnapshot sin migration válida" "$SNAPSHOT_ONLY_REPO" BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT false 0

INVALID_EVIDENCE_REPO="$TEMP_DIR/invalid-evidence-repo"
create_project "$INVALID_EVIDENCE_REPO" App
mkdir -p "$INVALID_EVIDENCE_REPO/Migrations"
printf 'public partial class Broken {}\n' > "$INVALID_EVIDENCE_REPO/Migrations/Broken.cs"
printf '[Migration("not-a-valid-id")]\npartial class Broken {}\n' > "$INVALID_EVIDENCE_REPO/Migrations/Broken.Designer.cs"
run_case "evidencia sin lista canónica" "$INVALID_EVIDENCE_REPO" BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT false 0

MULTI_PROJECT_REPO="$TEMP_DIR/multi-project-repo"
create_project "$MULTI_PROJECT_REPO/A" A
create_project "$MULTI_PROJECT_REPO/B" B
create_valid_migration "$MULTI_PROJECT_REPO/A" 20240404040404_ProjectA
create_valid_migration "$MULTI_PROJECT_REPO/B" 20240505050505_ProjectB
run_case "múltiples migration projects" "$MULTI_PROJECT_REPO" BLOCKED BLOCKED_AMBIGUOUS_MIGRATION_PROJECT false 0

echo "OK: $PASS_COUNT casos de repository discovery"
