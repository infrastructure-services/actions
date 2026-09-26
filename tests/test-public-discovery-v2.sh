#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

node "$SCRIPT_DIR/test-public-discovery-v2.mjs"

ROUTING_ROOT="$(mktemp -d)"
ROOT_WIN="$(cygpath -w "$ROOT")"
ROUTING_ROOT_WIN="$(cygpath -w "$ROUTING_ROOT")"
cleanup() {
  local resolved temp_root
  resolved="$(cd "$ROUTING_ROOT" && pwd -P)"
  temp_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
  case "$resolved" in
    "$temp_root"/*) rm -rf -- "$resolved" ;;
    *) echo "FAIL: refusing unsafe routing-test cleanup: $resolved"; exit 1 ;;
  esac
}
trap cleanup EXIT

dotnet restore "$ROOT_WIN/tools/SqlDiscovery/SqlDiscovery.csproj" \
  --configfile "$ROOT_WIN/tools/SqlDiscovery/NuGet.Config" >/dev/null
dotnet build "$ROOT_WIN/tools/SqlDiscovery/SqlDiscovery.csproj" \
  --configuration Release --no-restore \
  --property:BaseOutputPath="$ROUTING_ROOT_WIN/bin/" >/dev/null

SQL_DISCOVERY_DLL="$ROUTING_ROOT_WIN/bin/Release/net10.0/SqlDiscovery.dll"
run_route() {
  local expected_exit="$1" expected_diagnostic="$2"
  shift 2
  local stdout_file="$ROUTING_ROOT/stdout" stderr_file="$ROUTING_ROOT/stderr" exit_code
  set +e
  env -u DB_CONNECTION -u SQL_SERVER_CONNECTION -u SQL_DATABASE_NAME \
    dotnet "$SQL_DISCOVERY_DLL" "$@" >"$stdout_file" 2>"$stderr_file"
  exit_code=$?
  set -e
  [[ "$exit_code" -eq "$expected_exit" ]] || { echo "FAIL: routing exit $exit_code, expected $expected_exit"; exit 1; }
  [[ ! -s "$stdout_file" ]] || { echo "FAIL: routing emitted unexpected stdout"; exit 1; }
  grep -Fxq "$expected_diagnostic" "$stderr_file" || { echo "FAIL: routing diagnostic mismatch"; exit 1; }
}

run_route 2 DB_CONNECTION_REQUIRED
run_route 64 SQL_DISCOVERY_INPUT_REQUIRED --v2
run_route 64 ARGUMENTS_INVALID --unknown
echo "OK: Program.cs routing proves no-args V1, explicit --v2 and fail-closed unknown args"

for protected in \
  "$ROOT/action.yml" \
  "$ROOT/classification-v2-test-runtime/action.yml" \
  "$ROOT/scripts/run-classification-v2-test-runtime.mjs" \
  "$ROOT/scripts/compose-governed-classification-v2.mjs"
do
  git -c safe.directory="$ROOT" -C "$ROOT" diff --quiet -- "$protected" || { echo "FAIL: protected file modified: $protected"; exit 1; }
done

if grep -REni 'INSERT[[:space:]]+INTO|UPDATE[[:space:]]+[^[:space:]]+[[:space:]]+SET|DELETE[[:space:]]+FROM|MERGE[[:space:]]+INTO|CREATE[[:space:]]+(TABLE|DATABASE)|ALTER[[:space:]]+(TABLE|DATABASE)|DROP[[:space:]]+(TABLE|DATABASE)|TRUNCATE[[:space:]]+TABLE' \
  "$ROOT/tools/SqlDiscovery/SqlDiscoveryPublicCli.cs" \
  "$ROOT/scripts/run-sql-discovery-v2-public.mjs" \
  "$ROOT/sql-discovery-v2/action.yml"
then
  echo "FAIL: public SQL Discovery boundary contains a mutation token"
  exit 1
fi

echo "OK: public Discovery V2 actions are TEST-only, read-only and protected boundaries remain intact"
