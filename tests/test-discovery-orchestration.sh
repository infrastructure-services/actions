#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACTION_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DISCOVERY="$ACTION_ROOT/scripts/discover-db-scenario.sh"
TEMP_DIR="$(mktemp -d)"
FAKE_BIN="$TEMP_DIR/bin"
WORKSPACE="$TEMP_DIR/workspace"
DOTNET_LOG="$TEMP_DIR/dotnet.log"

cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

mkdir -p "$FAKE_BIN" "$WORKSPACE"

cat > "$FAKE_BIN/dotnet" <<'FAKE_DOTNET'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_DOTNET_LOG"

case "${1:-}" in
  --version)
    echo "10.0.302"
    exit 0
    ;;
  restore)
    if [[ "${FAKE_RESTORE_EXIT:-0}" -ne 0 ]]; then
      echo "<temp>/project.csproj : error NU1301: No se pudo cargar <url> token=secret-value"
      exit "$FAKE_RESTORE_EXIT"
    fi
    exit 0
    ;;
  build)
    exit "${FAKE_BUILD_EXIT:-0}"
    ;;
  run)
    if [[ -n "${FAKE_SQL_STDERR:-}" ]]; then
      printf '%s\n' "$FAKE_SQL_STDERR" >&2
    fi
    exit "${FAKE_SQL_EXIT:-0}"
    ;;
  *)
    exit 0
    ;;
esac
FAKE_DOTNET
chmod +x "$FAKE_BIN/dotnet"

cat > "$FAKE_BIN/jq" <<'FAKE_JQ'
#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" == "-n" ]]; then
  echo '{}'
  exit 0
fi

exit 0
FAKE_JQ
chmod +x "$FAKE_BIN/jq"

value() {
  local FILE="$1"
  local KEY="$2"
  sed -n "s/^${KEY}=//p" "$FILE" | tail -n 1
}

run_discovery() {
  local NAME="$1"
  local RUNNER_ONLY="$2"
  local RESTORE_EXIT="$3"
  local SQL_EXIT="$4"
  local SQL_STDERR="${5:-}"
  local OUTPUT_FILE="$TEMP_DIR/${NAME}.out"
  local STDOUT_FILE="$TEMP_DIR/${NAME}.stdout"

  : > "$OUTPUT_FILE"
  : > "$DOTNET_LOG"

  PATH="$FAKE_BIN:$PATH" \
  FAKE_DOTNET_LOG="$DOTNET_LOG" \
  FAKE_RESTORE_EXIT="$RESTORE_EXIT" \
  FAKE_SQL_EXIT="$SQL_EXIT" \
  FAKE_SQL_STDERR="$SQL_STDERR" \
  GITHUB_ACTION_PATH="$ACTION_ROOT" \
  GITHUB_WORKSPACE="$WORKSPACE" \
  GITHUB_OUTPUT="$OUTPUT_FILE" \
  RUNNER_TEMP="$TEMP_DIR" \
  DATABASE_LIFECYCLE=EXISTING \
  RUNNER_VALIDATION_ONLY="$RUNNER_ONLY" \
  SECRET_FOUND=true \
  DB_CONNECTION='Server=fake;Database=fake;User ID=fake;Password=not-a-real-secret' \
  ENVIRONMENT_NAME="$NAME" \
  OUTPUT_DIRECTORY=artifacts \
  REPOSITORY_STATUS=READY \
  REPOSITORY_REASON=OK \
  REPO_HAS_MIGRATIONS=false \
  DOTNET_SETUP_OUTCOME=success \
    bash "$DISCOVERY" > "$STDOUT_FILE"

  printf '%s\n' "$OUTPUT_FILE|$STDOUT_FILE"
}

RESULT="$(run_discovery runner-only true 0 99)"
OUTPUT_FILE="${RESULT%%|*}"
[[ "$(value "$OUTPUT_FILE" consistency_status)" == "CONSISTENT" ]]
[[ "$(value "$OUTPUT_FILE" consistency_reason)" == "RUNNER_VALIDATION_OK" ]]
if grep -q '^run ' "$DOTNET_LOG"; then
  echo "FAIL: runnerValidationOnly ejecutó el helper contra SQL."
  exit 1
fi
echo "PASS: runnerValidationOnly restaura y compila sin ejecutar SQL"

RESULT="$(run_discovery restore-failure true 7 99)"
OUTPUT_FILE="${RESULT%%|*}"
STDOUT_FILE="${RESULT#*|}"
[[ "$(value "$OUTPUT_FILE" consistency_reason)" == "FAIL_SQL_DISCOVERY_HELPER" ]]
grep -Fq 'NU1301' "$STDOUT_FILE"
if grep -Fq 'secret-value' "$STDOUT_FILE"; then
  echo "FAIL: el diagnóstico sanitizado expuso un valor sensible."
  exit 1
fi
echo "PASS: restore/build failure se clasifica como helper y sanitiza diagnóstico"

RESULT="$(run_discovery connection-failure false 0 3)"
OUTPUT_FILE="${RESULT%%|*}"
[[ "$(value "$OUTPUT_FILE" consistency_reason)" == "FAIL_DATABASE_UNREACHABLE" ]]
echo "PASS: SQL exit 3 se clasifica como database unreachable"

RESULT="$(run_discovery metadata-failure false 0 5)"
OUTPUT_FILE="${RESULT%%|*}"
[[ "$(value "$OUTPUT_FILE" consistency_reason)" == "FAIL_METADATA_VISIBILITY" ]]
echo "PASS: SQL exit 5 se clasifica como metadata visibility"

RESULT="$(run_discovery execution-failure false 0 1)"
OUTPUT_FILE="${RESULT%%|*}"
[[ "$(value "$OUTPUT_FILE" consistency_reason)" == "FAIL_SQL_DISCOVERY_HELPER" ]]
echo "PASS: fallo local de ejecución no se confunde con conectividad SQL"

assert_safe_diagnostic() {
  local NAME="$1"
  local DIAGNOSTIC="$2"
  local EXPECTED="$3"
  local RESULT OUTPUT_FILE STDOUT_FILE SUMMARY_FILE

  RESULT="$(run_discovery "$NAME" false 0 4 "$DIAGNOSTIC")"
  OUTPUT_FILE="${RESULT%%|*}"
  STDOUT_FILE="${RESULT#*|}"
  SUMMARY_FILE="$WORKSPACE/artifacts/$NAME/summary.md"

  [[ "$(value "$OUTPUT_FILE" consistency_reason)" == "FAIL_SQL_DISCOVERY_HELPER" ]]
  grep -Fxq 'SqlDiscovery no pudo ejecutarse correctamente.' "$STDOUT_FILE"
  grep -Fxq 'sql-execution-exit=4' "$STDOUT_FILE"
  grep -Fxq "sql-diagnostic-code=$EXPECTED" "$STDOUT_FILE"
  grep -Fq "| SQL execution exit | \`4\` |" "$SUMMARY_FILE"
  grep -Fq "| SQL diagnostic code | \`$EXPECTED\` |" "$SUMMARY_FILE"
}

assert_safe_diagnostic argument-exception \
  'SQL_DISCOVERY_FAILED:ArgumentException' \
  'SQL_DISCOVERY_FAILED:ArgumentException'
echo "PASS: nombre de excepción permitido se conserva"

assert_safe_diagnostic sql-number \
  'SQL_DISCOVERY_FAILED:18456' \
  'SQL_DISCOVERY_FAILED:18456'
echo "PASS: número SQL permitido se conserva"

assert_safe_diagnostic empty-metadata \
  'SQL_DISCOVERY_FAILED:EMPTY_METADATA_RESULT' \
  'SQL_DISCOVERY_FAILED:EMPTY_METADATA_RESULT'
echo "PASS: resultado de metadata vacío permitido se conserva"

assert_safe_diagnostic arbitrary-stderr \
  'línea arbitraria que no debe publicarse' \
  'UNKNOWN_SAFE_DIAGNOSTIC'
STDOUT_FILE="$TEMP_DIR/arbitrary-stderr.stdout"
if grep -RFq 'línea arbitraria' \
  "$STDOUT_FILE" "$WORKSPACE/artifacts/arbitrary-stderr"; then
  echo "FAIL: una línea arbitraria de stderr fue publicada."
  exit 1
fi
echo "PASS: stderr arbitrario no se publica"

assert_safe_diagnostic connection-string-stderr \
  'Server=forbidden-host;User ID=forbidden-user;Password=forbidden-value' \
  'UNKNOWN_SAFE_DIAGNOSTIC'
STDOUT_FILE="$TEMP_DIR/connection-string-stderr.stdout"
if grep -REq 'forbidden-host|forbidden-user|forbidden-value' \
  "$STDOUT_FILE" "$WORKSPACE/artifacts/connection-string-stderr"; then
  echo "FAIL: una connection string de prueba fue publicada."
  exit 1
fi
echo "PASS: líneas con formato de connection string no se publican"

assert_safe_diagnostic exception-details \
  $'SQL_DISCOVERY_FAILED:ArgumentException\nSystem.ArgumentException: forbidden-message\n   at SqlDiscovery.Main()' \
  'SQL_DISCOVERY_FAILED:ArgumentException'
STDOUT_FILE="$TEMP_DIR/exception-details.stdout"
if grep -REq 'forbidden-message|SqlDiscovery\.Main' \
  "$STDOUT_FILE" "$WORKSPACE/artifacts/exception-details"; then
  echo "FAIL: exception.Message o stack trace fueron publicados."
  exit 1
fi
echo "PASS: exception.Message y stack trace no se publican"

assert_safe_diagnostic no-diagnostic '' 'UNKNOWN_SAFE_DIAGNOSTIC'
echo "PASS: ausencia de diagnóstico seguro devuelve UNKNOWN_SAFE_DIAGNOSTIC"

echo "OK: orquestación de SqlDiscovery validada"
