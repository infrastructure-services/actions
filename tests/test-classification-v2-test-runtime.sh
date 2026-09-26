#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACTION_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

node "$SCRIPT_DIR/test-classification-v2-test-runtime.mjs"

ACTION_FILE="$ACTION_ROOT/classification-v2-test-runtime/action.yml"
RUNNER="$ACTION_ROOT/scripts/run-classification-v2-test-runtime.mjs"

grep -A3 -F 'environment-name:' "$ACTION_FILE" | grep -Fq 'required: true'
if grep -A4 -F 'environment-name:' "$ACTION_FILE" | grep -Fq 'default:'; then
  echo 'FAIL: environment-name no puede tener default implícito.' >&2
  exit 1
fi
grep -Fq 'GOVERNED_TEST_ENVIRONMENT_REQUIRED' "$RUNNER"

if grep -Ein 'dotnet ef|database update|deploy\.sql|rollback\.sql|kubectl|helm|argo|restart|sqlcmd|invoke-sqlcmd|registry write|state store write' "$ACTION_FILE" "$RUNNER"; then
  echo 'FAIL: HG6 contiene una operación potencialmente mutante.' >&2
  exit 1
fi

if grep -Ein 'upload-artifact|environment-name:.*(QA|PROD)|default: (QA|PROD)' "$ACTION_FILE"; then
  echo 'FAIL: HG6 habilita ambiente o publicación fuera de TEST.' >&2
  exit 1
fi

echo 'OK: HG6 público, TEST-only, read-only y sin deployment'
