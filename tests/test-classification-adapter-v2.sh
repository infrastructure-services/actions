#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADAPTER="$SCRIPT_DIR/../scripts/adapt-classification-evidence-v2.mjs"
PASS_COUNT=0

run_case() {
  local name="$1" lifecycle="$2" mode="$3" physical="$4" repository="$5" history="$6" relation="$7" schema="$8" registry="$9" onboarding="${10}" expected_scenario="${11}" expected_status="${12}"
  local input result
  input="$(node -e '
    const [lifecycle,mode,physical,repository,history,relation,schema,registry,onboarding]=process.argv.slice(1);
    const ids=["20240101010101_Initial","20240202020202_AddOrders"];
    const root={contractVersion:2,declarations:{databaseLifecycle:lifecycle,changeManagementMode:mode},connection:{status:"SUCCEEDED"},databaseLookup:{status:"FOUND"},targetConnection:{status:"SUCCEEDED"},metadata:{status:"SUFFICIENT"},physical:{status:"OBSERVED",businessObjectCount:physical==="EMPTY"?0:5,technicalObjectCount:physical==="TECHNICAL_ONLY"?1:0},history:{status:history},repository:{status:repository},schema:{status:schema},registry:{status:registry},onboarding:{status:onboarding}};
    if(history==="PRESENT"){root.history.migrationIds=relation==="VALID_PREFIX"?[ids[0]]:ids;root.history.migrationCount=root.history.migrationIds.length;}
    if(repository==="PRESENT_VALID"){root.repository.migrations={count:ids.length,ids};}
    process.stdout.write(JSON.stringify(root));
  ' "$lifecycle" "$mode" "$physical" "$repository" "$history" "$relation" "$schema" "$registry" "$onboarding")"
  result="$(printf '%s' "$input" | node "$ADAPTER")"
  printf '%s' "$result" | node -e '
    const fs=require("fs"),x=JSON.parse(fs.readFileSync(0,"utf8"));
    if(x.adapterStatus!=="CLASSIFIED"||x.classificationInvoked!==true)process.exit(1);
    if(x.classificationResult.inferences.scenario!==process.argv[1])process.exit(1);
    if(x.classificationResult.decision.status!==process.argv[2])process.exit(1);
  ' "$expected_scenario" "$expected_status"
  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $name"
}

run_case "NEW_EF" NEW EF_MIGRATIONS EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED NEW_EF ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE
run_case "EXISTING_EF" EXISTING EF_MIGRATIONS POPULATED PRESENT_VALID PRESENT EXACT_MATCH CONSISTENT CERTIFIED MANAGED EXISTING_EF ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE
run_case "EXISTING_EF bloqueado" EXISTING EF_MIGRATIONS POPULATED PRESENT_VALID PRESENT EXACT_MATCH INSUFFICIENT_EVIDENCE CERTIFIED MANAGED EXISTING_EF BLOCKED
run_case "EXISTING_LEGACY" EXISTING LEGACY_UNMANAGED POPULATED ABSENT ABSENT NOT_APPLICABLE CONSISTENT CERTIFIED MANAGED EXISTING_LEGACY ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE
run_case "UNCLASSIFIED real" NEW LEGACY_UNMANAGED EMPTY ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED UNCLASSIFIED BLOCKED

ERROR_STDOUT="$(mktemp)"
ERROR_STDERR="$(mktemp)"
cleanup() { rm -f "$ERROR_STDOUT" "$ERROR_STDERR"; }
trap cleanup EXIT
set +e
printf '{' | node "$ADAPTER" >"$ERROR_STDOUT" 2>"$ERROR_STDERR"
exit_code=$?
set -e
[[ "$exit_code" -eq 65 ]]
node -e 'const fs=require("fs"),x=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));if(x.classificationInvoked!==false||x.normalizedFacts!==null||"classificationResult" in x)process.exit(1)' "$ERROR_STDOUT"
[[ "$(<"$ERROR_STDERR")" == "classification-adapter-v2: JSON_INVALID" ]]
PASS_COUNT=$((PASS_COUNT + 1))
echo "PASS: error JSON por stdout y diagnóstico sanitizado por stderr"

if grep -REn 'adapt-classification-evidence-v2' "$SCRIPT_DIR/../action.yml" "$SCRIPT_DIR/../scripts/discover-db-scenario.sh" "$SCRIPT_DIR/../scripts/classify-scenario.sh"; then
  echo "FAIL: adapter conectado al runtime"
  exit 1
fi
PASS_COUNT=$((PASS_COUNT + 1))
echo "PASS: adapter sin consumidor runtime"

node "$SCRIPT_DIR/test-classification-adapter-v2.mjs"
PASS_COUNT=$((PASS_COUNT + 1))
echo "PASS: suite unitaria del adapter"

echo "OK: $PASS_COUNT casos de integración del adapter V2"
