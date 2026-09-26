#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DISCOVERY="$ROOT/scripts/discover-repository-v2.mjs"

node "$SCRIPT_DIR/test-repository-discovery-v2.mjs"

if grep -Eiq 'DB_CONNECTION|SqlConnection|https?://|fetch\(|axios|curl|wget|child_process' "$DISCOVERY"; then
  echo 'FAIL: Repository Discovery V2 contiene SQL, red o ejecución de procesos.'
  exit 1
fi
if grep -REn 'discover-repository-v2' "$ROOT/action.yml" "$ROOT/scripts/discover-repository.sh" "$ROOT/scripts/discover-db-scenario.sh"; then
  echo 'FAIL: Repository Discovery V2 fue conectado al runtime legacy.'
  exit 1
fi

echo 'OK: Repository Discovery V2 es local, read-only y permanece desacoplado'
