#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PRODUCER="$ROOT/scripts/compose-classification-evidence-v2.mjs"
ADAPTER="$ROOT/scripts/adapt-classification-evidence-v2.mjs"
FIXTURES="$SCRIPT_DIR/fixtures/classification-evidence-v2"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

node "$SCRIPT_DIR/test-classification-evidence-producer-v2.mjs"

node "$PRODUCER" < "$FIXTURES/sources/valid-complete.json" > "$TEMP_DIR/raw.json"
cmp "$TEMP_DIR/raw.json" "$FIXTURES/raw/valid-complete.json"
node "$ADAPTER" < "$TEMP_DIR/raw.json" > "$TEMP_DIR/combined.json"
node -e 'const fs=require("fs");const x=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));if(x.adapterStatus!=="CLASSIFIED"||x.classificationInvoked!==true||x.classificationResult?.inferences?.scenario!=="EXISTING_EF")process.exit(1)' "$TEMP_DIR/combined.json"

node "$PRODUCER" < "$FIXTURES/sources/incomplete-not-evaluated.json" > "$TEMP_DIR/incomplete.json"
cmp "$TEMP_DIR/incomplete.json" "$FIXTURES/raw/incomplete-not-evaluated.json"
set +e
node "$ADAPTER" < "$TEMP_DIR/incomplete.json" > "$TEMP_DIR/incomplete-result.json" 2> "$TEMP_DIR/incomplete.err"
ADAPTER_EXIT=$?
set -e
[[ "$ADAPTER_EXIT" -eq 67 ]]
node -e 'const fs=require("fs");const x=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));if(x.adapterStatus!=="INSUFFICIENT_EVIDENCE"||x.classificationInvoked!==false||x.normalizedFacts!==null)process.exit(1)' "$TEMP_DIR/incomplete-result.json"

[[ "$(grep -Fc 'spawnSync("bash"' "$ADAPTER")" -eq 1 ]]
if grep -REn 'compose-classification-evidence-v2' \
  "$ROOT/action.yml" "$ROOT/scripts/discover-db-scenario.sh" "$ROOT/scripts/discover-repository.sh" "$ROOT/scripts/classify-scenario.sh"; then
  echo 'FAIL: integración runtime accidental del productor.'
  exit 1
fi
if grep -Eiq 'DB_CONNECTION|SqlConnection|https?://|fetch\(|axios|curl|wget|child_process' "$PRODUCER"; then
  echo 'FAIL: el productor contiene SQL, red o ejecución de procesos.'
  exit 1
fi
if grep -Eq '(\?\?[[:space:]]*0|\|\|[[:space:]]*false|=[[:space:]]*\[\])' "$PRODUCER"; then
  echo 'FAIL: el productor contiene un default/coerción prohibido.'
  exit 1
fi

echo 'OK: integración sintética productor → adapter real → classifier V2 real y aislamiento estático validados'
