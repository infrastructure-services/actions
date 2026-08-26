#!/usr/bin/env bash
set -uo pipefail

ENGINE_PROJECT="$GITHUB_ACTION_PATH/../tools/DatabaseReleaseQualification/DatabaseReleaseQualification.csproj"
NUGET_CONFIG="$GITHUB_ACTION_PATH/../tools/DatabaseReleaseQualification/NuGet.Config"
BUILD_DIRECTORY="$RUNNER_TEMP/database-schema-capture-engine-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}"
RESULT_DIRECTORY="$RUNNER_TEMP/database-schema-capture-results-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}"
DB_CONNECTION_VALUE="${DB_CONNECTION-}"
unset DB_CONNECTION

if [[ "${ENVIRONMENT_NAME^^}" != "TEST" ]]; then
  echo "Schema capture rechazado: ambiente no permitido." >&2
  exit 3
fi

if [[ "$OUTPUT_DIRECTORY" = /* ]]; then
  ARTIFACT_DIRECTORY="$OUTPUT_DIRECTORY"
else
  ARTIFACT_DIRECTORY="$GITHUB_WORKSPACE/$OUTPUT_DIRECTORY"
fi

CAPTURE_1_DIRECTORY="$ARTIFACT_DIRECTORY/capture-1"
CAPTURE_2_DIRECTORY="$ARTIFACT_DIRECTORY/capture-2"
COMPARISON_DIRECTORY="$ARTIFACT_DIRECTORY/comparison"
SUMMARY_FILE="$ARTIFACT_DIRECTORY/summary.md"
mkdir -p "$ARTIFACT_DIRECTORY" "$RESULT_DIRECTORY" "$BUILD_DIRECTORY"

safe_value() {
  local value="${1-}"
  value="${value//$'\r'/_}"
  value="${value//$'\n'/_}"
  value="${value//\`/_}"
  value="${value//|/_}"
  printf '%s' "$value"
}

safe_status() {
  local value="${1-FAIL_SCHEMA_CAPTURE}"
  if [[ "$value" =~ ^(SUCCESS|FAIL_DATABASE_UNREACHABLE|FAIL_METADATA_VISIBILITY|FAIL_SCHEMA_CAPTURE|FAIL_SCHEMA_CAPTURE_NONDETERMINISTIC)$ ]]; then
    printf '%s' "$value"
  else
    printf '%s' 'FAIL_SCHEMA_CAPTURE'
  fi
}

safe_diagnostic() {
  local value="${1-UNKNOWN_SAFE_DIAGNOSTIC}"
  if [[ "$value" =~ ^[A-Za-z0-9_:-]{1,128}$ ]]; then
    printf '%s' "$value"
  else
    printf '%s' 'UNKNOWN_SAFE_DIAGNOSTIC'
  fi
}

write_outputs() {
  local status="$1" deterministic="$2" hash1="${3-}" hash2="${4-}" coverage="${5-}" metrics="${6-}"
  {
    printf 'status=%s\n' "$status"
    printf 'deterministic=%s\n' "$deterministic"
    printf 'capture_1_hash=%s\n' "$hash1"
    printf 'capture_2_hash=%s\n' "$hash2"
    printf 'schema_coverage=%s\n' "$coverage"
    printf 'metrics_availability=%s\n' "$metrics"
    printf 'artifact_directory=%s\n' "$ARTIFACT_DIRECTORY"
  } >> "$GITHUB_OUTPUT"
}

write_failure_summary() {
  local status diagnostic exit_code
  status="$(safe_status "$1")"
  diagnostic="$(safe_diagnostic "$2")"
  exit_code="$3"
  {
    echo '# Database Schema Capture — TEST'
    echo
    echo "- Status: \`$status\`"
    echo "- Diagnostic code: \`$diagnostic\`"
    echo "- Exit code: \`$exit_code\`"
    echo '- Deterministic: `false`'
    echo '- SQL mutations: `NONE`'
  } > "$SUMMARY_FILE"
  if [[ -n "${GITHUB_STEP_SUMMARY-}" ]]; then
    cat "$SUMMARY_FILE" >> "$GITHUB_STEP_SUMMARY"
  fi
  write_outputs "$status" false
}

read_failure() {
  local result_file="$1" fallback_status="$2" fallback_diagnostic="$3" execution_log="${4-}"
  if [[ -s "$result_file" ]] && jq -e . "$result_file" >/dev/null 2>&1; then
    FAILURE_STATUS="$(safe_status "$(jq -r '.status // empty' "$result_file")")"
    FAILURE_DIAGNOSTIC="$(safe_diagnostic "$(jq -r '.diagnosticCode // empty' "$result_file")")"
  elif [[ -s "$execution_log" ]]; then
    local safe_line
    safe_line="$(grep -Eom1 '^SCHEMA_CAPTURE_FAILED:(SUCCESS|FAIL_DATABASE_UNREACHABLE|FAIL_METADATA_VISIBILITY|FAIL_SCHEMA_CAPTURE|FAIL_SCHEMA_CAPTURE_NONDETERMINISTIC):[A-Za-z0-9_:-]{1,128}$' "$execution_log" || true)"
    if [[ -n "$safe_line" ]]; then
      FAILURE_STATUS="$(safe_status "$(printf '%s' "$safe_line" | cut -d: -f2)")"
      FAILURE_DIAGNOSTIC="$(safe_diagnostic "$(printf '%s' "$safe_line" | cut -d: -f3-)")"
    else
      FAILURE_STATUS="$fallback_status"
      FAILURE_DIAGNOSTIC="$fallback_diagnostic"
    fi
  else
    FAILURE_STATUS="$fallback_status"
    FAILURE_DIAGNOSTIC="$fallback_diagnostic"
  fi
}

run_capture() {
  local capture_id="$1" output_directory="$2" result_file="$3" execution_log="$4"
  DB_CONNECTION="$DB_CONNECTION_VALUE" dotnet "$BUILD_DIRECTORY/DatabaseReleaseQualification.dll" capture-schema \
    --environment TEST \
    --capture-id "$capture_id" \
    --output "$output_directory" \
    --result "$result_file" >"$execution_log" 2>&1
  return $?
}

set +e
dotnet restore "$ENGINE_PROJECT" --configfile "$NUGET_CONFIG"
RESTORE_EXIT=$?
if [[ $RESTORE_EXIT -eq 0 ]]; then
  dotnet build "$ENGINE_PROJECT" --configuration Release --no-restore --output "$BUILD_DIRECTORY"
  BUILD_EXIT=$?
else
  BUILD_EXIT=1
fi
set -e

if [[ $RESTORE_EXIT -ne 0 ]]; then
  write_failure_summary FAIL_SCHEMA_CAPTURE ENGINE_RESTORE_FAILED "$RESTORE_EXIT"
  exit "$RESTORE_EXIT"
fi
if [[ $BUILD_EXIT -ne 0 ]]; then
  write_failure_summary FAIL_SCHEMA_CAPTURE ENGINE_BUILD_FAILED "$BUILD_EXIT"
  exit "$BUILD_EXIT"
fi

CAPTURE_1_RESULT="$RESULT_DIRECTORY/capture-1-result.json"
CAPTURE_2_RESULT="$RESULT_DIRECTORY/capture-2-result.json"
COMPARISON_RESULT="$RESULT_DIRECTORY/comparison-result.json"
CAPTURE_1_LOG="$RESULT_DIRECTORY/capture-1-execution.log"
CAPTURE_2_LOG="$RESULT_DIRECTORY/capture-2-execution.log"
COMPARISON_LOG="$RESULT_DIRECTORY/comparison-execution.log"

set +e
run_capture capture-1 "$CAPTURE_1_DIRECTORY" "$CAPTURE_1_RESULT" "$CAPTURE_1_LOG"
CAPTURE_1_EXIT=$?
set -e
if [[ $CAPTURE_1_EXIT -ne 0 ]]; then
  read_failure "$CAPTURE_1_RESULT" FAIL_SCHEMA_CAPTURE CAPTURE_1_FAILED "$CAPTURE_1_LOG"
  echo "Schema capture #1 failed: $FAILURE_STATUS / $FAILURE_DIAGNOSTIC" >&2
  write_failure_summary "$FAILURE_STATUS" "$FAILURE_DIAGNOSTIC" "$CAPTURE_1_EXIT"
  exit "$CAPTURE_1_EXIT"
fi

set +e
run_capture capture-2 "$CAPTURE_2_DIRECTORY" "$CAPTURE_2_RESULT" "$CAPTURE_2_LOG"
CAPTURE_2_EXIT=$?
set -e
if [[ $CAPTURE_2_EXIT -ne 0 ]]; then
  read_failure "$CAPTURE_2_RESULT" FAIL_SCHEMA_CAPTURE CAPTURE_2_FAILED "$CAPTURE_2_LOG"
  echo "Schema capture #2 failed: $FAILURE_STATUS / $FAILURE_DIAGNOSTIC" >&2
  write_failure_summary "$FAILURE_STATUS" "$FAILURE_DIAGNOSTIC" "$CAPTURE_2_EXIT"
  exit "$CAPTURE_2_EXIT"
fi

set +e
dotnet "$BUILD_DIRECTORY/DatabaseReleaseQualification.dll" compare-schema-captures \
  --environment TEST \
  --capture-1 "$CAPTURE_1_DIRECTORY" \
  --capture-2 "$CAPTURE_2_DIRECTORY" \
  --output "$COMPARISON_DIRECTORY" \
  --result "$COMPARISON_RESULT" >"$COMPARISON_LOG" 2>&1
COMPARISON_EXIT=$?
set -e

if [[ ! -s "$COMPARISON_RESULT" ]] || ! jq -e . "$COMPARISON_RESULT" >/dev/null 2>&1; then
  FAILURE_EXIT="$COMPARISON_EXIT"
  if [[ "$FAILURE_EXIT" -eq 0 ]]; then FAILURE_EXIT=6; fi
  write_failure_summary FAIL_SCHEMA_CAPTURE COMPARISON_RESULT_UNAVAILABLE "$FAILURE_EXIT"
  exit "$FAILURE_EXIT"
fi

STATUS="$(safe_status "$(jq -r '.status // "FAIL_SCHEMA_CAPTURE"' "$COMPARISON_RESULT" 2>/dev/null)")"
DIAGNOSTIC="$(safe_diagnostic "$(jq -r '.diagnosticCode // "UNKNOWN_SAFE_DIAGNOSTIC"' "$COMPARISON_RESULT" 2>/dev/null)")"
HASH_1="$(jq -r '.capture1SchemaHash // empty' "$COMPARISON_RESULT" 2>/dev/null)"
HASH_2="$(jq -r '.capture2SchemaHash // empty' "$COMPARISON_RESULT" 2>/dev/null)"
DETERMINISTIC="$(jq -r '.deterministic // false' "$COMPARISON_RESULT" 2>/dev/null)"
if [[ ! "$HASH_1" =~ ^[0-9a-f]{64}$ ]]; then HASH_1=''; fi
if [[ ! "$HASH_2" =~ ^[0-9a-f]{64}$ ]]; then HASH_2=''; fi
if [[ "$DETERMINISTIC" != 'true' ]]; then DETERMINISTIC='false'; fi

DATABASE_NAME="$(safe_value "$(jq -r '.databaseName // empty' "$CAPTURE_1_RESULT")")"
SERVER_VERSION="$(safe_value "$(jq -r '.serverVersion // empty' "$CAPTURE_1_RESULT")")"
SCHEMA_COVERAGE="$(safe_value "$(jq -r '.schemaCoverage // empty' "$CAPTURE_1_RESULT")")"
METRICS_AVAILABILITY="$(safe_value "$(jq -r '.metricsAvailability // empty' "$CAPTURE_1_RESULT")")"

{
  echo '# Database Schema Capture — TEST'
  echo
  echo "- Capture #1 hash: \`$HASH_1\`"
  echo "- Capture #2 hash: \`$HASH_2\`"
  echo "- Deterministic: \`$DETERMINISTIC\`"
  echo "- Database: \`$DATABASE_NAME\`"
  echo "- SQL Server version: \`$SERVER_VERSION\`"
  echo "- Schema coverage: \`$SCHEMA_COVERAGE\`"
  echo "- Metrics availability: \`$METRICS_AVAILABILITY\`"
  echo "- Status: \`$STATUS\`"
  echo "- Diagnostic code: \`$DIAGNOSTIC\`"
  echo '- SQL mutations: `NONE`'
  echo
  echo '## Object counts'
  jq -r '.objectCounts // {} | to_entries[] | "- `\(.key)`: \(.value)"' "$CAPTURE_1_RESULT"
  echo
  echo '## Unsupported schema features'
  if [[ "$(jq '.unsupportedSchemaFeatures // [] | length' "$CAPTURE_1_RESULT")" -eq 0 ]]; then
    echo '- None detected'
  else
    jq -r '.unsupportedSchemaFeatures[] | "- `\(.)`"' "$CAPTURE_1_RESULT"
  fi
} > "$SUMMARY_FILE"

if [[ -n "${GITHUB_STEP_SUMMARY-}" ]]; then
  cat "$SUMMARY_FILE" >> "$GITHUB_STEP_SUMMARY"
fi
write_outputs "$STATUS" "$DETERMINISTIC" "$HASH_1" "$HASH_2" "$SCHEMA_COVERAGE" "$METRICS_AVAILABILITY"

if [[ $COMPARISON_EXIT -ne 0 ]]; then
  echo "Schema capture comparison failed: $STATUS / $DIAGNOSTIC" >&2
  exit "$COMPARISON_EXIT"
fi

echo 'Schema capture TEST completado: hashes determinísticos.'
