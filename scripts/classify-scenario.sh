#!/usr/bin/env bash
set -euo pipefail

LIFECYCLE="${1:-}"
ACCESS_STATUS="${2:-READY}"
REPO_HAS_MIGRATIONS="${3:-false}"
BUSINESS_OBJECT_COUNT="${4:-0}"
EF_HISTORY_EXISTS="${5:-false}"
REPO_MIGRATIONS_FILE="${6:-}"
DB_MIGRATIONS_FILE="${7:-}"
REPOSITORY_STATUS="${8:-READY}"
REPOSITORY_REASON="${9:-OK}"

bool_value() {
  [[ "${1,,}" == "true" ]] && echo true || echo false
}

REPO_HAS_MIGRATIONS="$(bool_value "$REPO_HAS_MIGRATIONS")"
EF_HISTORY_EXISTS="$(bool_value "$EF_HISTORY_EXISTS")"
BUSINESS_OBJECT_COUNT="${BUSINESS_OBJECT_COUNT:-0}"

if ! [[ "$BUSINESS_OBJECT_COUNT" =~ ^[0-9]+$ ]]; then
  BUSINESS_OBJECT_COUNT=0
fi

REPO_COUNT=0
DB_COUNT=0
LAST_APPLIED=""
LATEST_REPO=""

if [[ -n "$REPO_MIGRATIONS_FILE" && -f "$REPO_MIGRATIONS_FILE" ]]; then
  REPO_COUNT="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
  LATEST_REPO="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | tail -n 1)"
fi

if [[ -n "$DB_MIGRATIONS_FILE" && -f "$DB_MIGRATIONS_FILE" ]]; then
  DB_COUNT="$(sed '/^[[:space:]]*$/d' "$DB_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
  LAST_APPLIED="$(sed '/^[[:space:]]*$/d' "$DB_MIGRATIONS_FILE" | tail -n 1)"
fi

STATUS="CONSISTENT"
REASON="OK"
SCENARIO=""
SOURCE_KIND="UNKNOWN"
HAS_PENDING="false"

emit_result() {
  printf 'consistency_status=%s\n' "$STATUS"
  printf 'consistency_reason=%s\n' "$REASON"
  printf 'scenario=%s\n' "$SCENARIO"
  printf 'source_kind=%s\n' "$SOURCE_KIND"
  printf 'last_applied_migration=%s\n' "$LAST_APPLIED"
  printf 'latest_repo_migration=%s\n' "$LATEST_REPO"
  printf 'has_pending_migrations=%s\n' "$HAS_PENDING"
  printf 'repo_migration_count=%s\n' "$REPO_COUNT"
  printf 'ef_history_count=%s\n' "$DB_COUNT"
}

case "$LIFECYCLE" in
  NEW|EXISTING)
    ;;
  *)
    STATUS="FAIL"
    REASON="FAIL_INVALID_DATABASE_LIFECYCLE"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
esac

case "$ACCESS_STATUS" in
  READY)
    ;;
  SECRET_MISSING)
    STATUS="FAIL"
    REASON="FAIL_SECRET_REQUIRED"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
  CONNECTION_FAILED)
    STATUS="FAIL"
    REASON="FAIL_DATABASE_UNREACHABLE"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
  METADATA_INSUFFICIENT)
    STATUS="FAIL"
    REASON="FAIL_METADATA_VISIBILITY"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
  HELPER_FAILED)
    STATUS="FAIL"
    REASON="FAIL_SQL_DISCOVERY_HELPER"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
  *)
    STATUS="FAIL"
    REASON="FAIL_INVALID_ACCESS_STATUS"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
esac

case "$REPOSITORY_STATUS" in
  READY)
    ;;
  BLOCKED)
    STATUS="BLOCKED"
    REASON="${REPOSITORY_REASON:-BLOCKED_EF_REPOSITORY_INCONSISTENT}"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
  *)
    STATUS="FAIL"
    REASON="FAIL_INVALID_REPOSITORY_STATUS"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
    ;;
esac

if [[ "$REPO_HAS_MIGRATIONS" == "false" && "$EF_HISTORY_EXISTS" == "true" ]]; then
  STATUS="BLOCKED"
  REASON="BLOCKED_HISTORY_WITHOUT_REPO"
  SCENARIO="BLOCKED"
  emit_result
  exit 0
fi

if [[ "$LIFECYCLE" == "NEW" ]]; then
  if [[ "$REPO_HAS_MIGRATIONS" == "false" || "$REPO_COUNT" -eq 0 ]]; then
    STATUS="BLOCKED"
    REASON="BLOCKED_NEW_WITHOUT_MIGRATIONS"
    SCENARIO="BLOCKED"
  elif [[ "$BUSINESS_OBJECT_COUNT" -gt 0 ]]; then
    STATUS="BLOCKED"
    REASON="BLOCKED_NEW_NOT_EMPTY"
    SCENARIO="BLOCKED"
    SOURCE_KIND="EF"
  elif [[ "$EF_HISTORY_EXISTS" == "true" ]]; then
    STATUS="BLOCKED"
    REASON="BLOCKED_NEW_HAS_EF_HISTORY"
    SCENARIO="BLOCKED"
    SOURCE_KIND="EF"
  else
    SCENARIO="NEW_EF"
    SOURCE_KIND="EF"
    HAS_PENDING="true"
  fi

  emit_result
  exit 0
fi

if [[ "$REPO_HAS_MIGRATIONS" == "true" ]]; then
  SOURCE_KIND="EF"

  if [[ "$EF_HISTORY_EXISTS" == "false" || "$DB_COUNT" -eq 0 ]]; then
    STATUS="BLOCKED"
    REASON="BLOCKED_BASELINE_REQUIRED"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
  fi

  SEQUENCE_VALID="true"

  if [[ "$DB_COUNT" -gt "$REPO_COUNT" ]]; then
    SEQUENCE_VALID="false"
  else
    INDEX=1
    while IFS= read -r DB_MIGRATION; do
      [[ -n "$DB_MIGRATION" ]] || continue
      REPO_MIGRATION="$(sed -n "${INDEX}p" "$REPO_MIGRATIONS_FILE")"

      if [[ "$DB_MIGRATION" != "$REPO_MIGRATION" ]]; then
        SEQUENCE_VALID="false"
        break
      fi

      INDEX=$((INDEX + 1))
    done < "$DB_MIGRATIONS_FILE"
  fi

  if [[ "$SEQUENCE_VALID" != "true" ]]; then
    STATUS="BLOCKED"
    REASON="BLOCKED_EF_SEQUENCE_DIVERGED"
    SCENARIO="BLOCKED"
    emit_result
    exit 0
  fi

  SCENARIO="EXISTING_EF"

  if [[ "$REPO_COUNT" -gt "$DB_COUNT" ]]; then
    HAS_PENDING="true"
  fi

  emit_result
  exit 0
fi

SCENARIO="EXISTING_SQL"
SOURCE_KIND="SQL"
emit_result
