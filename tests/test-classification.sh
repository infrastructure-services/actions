#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLASSIFIER="$SCRIPT_DIR/../scripts/classify-scenario.sh"
TEMP_DIR="$(mktemp -d)"
PASS_COUNT=0

cleanup() {
  rm -rf "$TEMP_DIR"
}

trap cleanup EXIT

write_list() {
  local FILE="$1"
  shift
  : > "$FILE"
  printf '%s\n' "$@" | sed '/^$/d' > "$FILE"
}

run_case() {
  local NAME="$1"
  local EXPECTED_STATUS="$2"
  local EXPECTED_REASON="$3"
  local EXPECTED_SCENARIO="$4"
  local EXPECTED_PENDING="$5"
  local LIFECYCLE="$6"
  local ACCESS="$7"
  local REPO_HAS="$8"
  local BUSINESS_COUNT="$9"
  local HISTORY_EXISTS="${10}"
  local REPO_FILE="${11}"
  local DB_FILE="${12}"
  local REPOSITORY_STATUS="${13:-READY}"
  local REPOSITORY_REASON="${14:-OK}"

  local RESULT
  RESULT="$(bash "$CLASSIFIER" "$LIFECYCLE" "$ACCESS" "$REPO_HAS" "$BUSINESS_COUNT" "$HISTORY_EXISTS" "$REPO_FILE" "$DB_FILE" "$REPOSITORY_STATUS" "$REPOSITORY_REASON")"

  value() {
    local KEY="$1"
    printf '%s\n' "$RESULT" | sed -n "s/^${KEY}=//p" | head -n 1
  }

  [[ "$(value consistency_status)" == "$EXPECTED_STATUS" ]]
  [[ "$(value consistency_reason)" == "$EXPECTED_REASON" ]]
  [[ "$(value scenario)" == "$EXPECTED_SCENARIO" ]]
  [[ "$(value has_pending_migrations)" == "$EXPECTED_PENDING" ]]

  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $NAME"
}

REPO_2="$TEMP_DIR/repo-2.txt"
REPO_3="$TEMP_DIR/repo-3.txt"
DB_0="$TEMP_DIR/db-0.txt"
DB_2="$TEMP_DIR/db-2.txt"
DB_3="$TEMP_DIR/db-3.txt"
DB_DIVERGED="$TEMP_DIR/db-diverged.txt"
DB_REORDERED="$TEMP_DIR/db-reordered.txt"
DB_ADVANCED="$TEMP_DIR/db-advanced.txt"

write_list "$REPO_2" M1 M2
write_list "$REPO_3" M1 M2 M3
write_list "$DB_0"
write_list "$DB_2" M1 M2
write_list "$DB_3" M1 M2 M3
write_list "$DB_DIVERGED" M1 M8
write_list "$DB_REORDERED" M2 M1
write_list "$DB_ADVANCED" M1 M2 M3

run_case "NEW_EF válido" CONSISTENT OK NEW_EF true NEW READY true 0 false "$REPO_2" "$DB_0"
run_case "EXISTING_EF con pendientes" CONSISTENT OK EXISTING_EF true EXISTING READY true 5 true "$REPO_3" "$DB_2"
run_case "EXISTING_EF sin pendientes" CONSISTENT OK EXISTING_EF false EXISTING READY true 5 true "$REPO_3" "$DB_3"
run_case "EXISTING_SQL válido" CONSISTENT OK EXISTING_SQL false EXISTING READY false 8 false "$DB_0" "$DB_0"
run_case "baseline requerido" BLOCKED BLOCKED_BASELINE_REQUIRED BLOCKED false EXISTING READY true 8 false "$REPO_2" "$DB_0"
run_case "history sin repo" BLOCKED BLOCKED_HISTORY_WITHOUT_REPO BLOCKED false EXISTING READY false 8 true "$DB_0" "$DB_2"
run_case "secuencia divergente" BLOCKED BLOCKED_EF_SEQUENCE_DIVERGED BLOCKED false EXISTING READY true 8 true "$REPO_3" "$DB_DIVERGED"
run_case "secuencia reordenada" BLOCKED BLOCKED_EF_SEQUENCE_DIVERGED BLOCKED false EXISTING READY true 8 true "$REPO_3" "$DB_REORDERED"
run_case "DB más avanzada que repo" BLOCKED BLOCKED_EF_SEQUENCE_DIVERGED BLOCKED false EXISTING READY true 8 true "$REPO_2" "$DB_ADVANCED"
run_case "NEW sin migrations" BLOCKED BLOCKED_NEW_WITHOUT_MIGRATIONS BLOCKED false NEW READY false 0 false "$DB_0" "$DB_0"
run_case "NEW no vacía" BLOCKED BLOCKED_NEW_NOT_EMPTY BLOCKED false NEW READY true 1 false "$REPO_2" "$DB_0"
run_case "NEW con user-defined type" BLOCKED BLOCKED_NEW_NOT_EMPTY BLOCKED false NEW READY true 1 false "$REPO_2" "$DB_0"
run_case "secret faltante" FAIL FAIL_SECRET_REQUIRED BLOCKED false EXISTING SECRET_MISSING true 8 true "$REPO_2" "$DB_2"
run_case "history existente pero vacía" BLOCKED BLOCKED_BASELINE_REQUIRED BLOCKED false EXISTING READY true 0 true "$REPO_2" "$DB_0"
run_case "conexión fallida" FAIL FAIL_DATABASE_UNREACHABLE BLOCKED false EXISTING CONNECTION_FAILED true 8 true "$REPO_2" "$DB_2"
run_case "metadata insuficiente" FAIL FAIL_METADATA_VISIBILITY BLOCKED false EXISTING METADATA_INSUFFICIENT true 8 true "$REPO_2" "$DB_2"
run_case "helper SQL no disponible" FAIL FAIL_SQL_DISCOVERY_HELPER BLOCKED false EXISTING HELPER_FAILED true 8 true "$REPO_2" "$DB_2"
run_case "repo EF inconsistente" BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT BLOCKED false EXISTING READY false 8 false "$DB_0" "$DB_0" BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT
run_case "múltiples migration projects" BLOCKED BLOCKED_AMBIGUOUS_MIGRATION_PROJECT BLOCKED false EXISTING READY false 8 false "$DB_0" "$DB_0" BLOCKED BLOCKED_AMBIGUOUS_MIGRATION_PROJECT

echo "OK: $PASS_COUNT casos de clasificación"
