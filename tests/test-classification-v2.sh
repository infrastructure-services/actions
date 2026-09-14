#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLASSIFIER="$SCRIPT_DIR/../scripts/classify-scenario-v2.sh"
PASS_COUNT=0

classify() { bash "$CLASSIFIER" "$@"; }
classify_values() {
  [[ $# -eq 12 ]]
  classify \
    --contract-version "$1" --database-lifecycle "$2" \
    --change-management-mode "$3" --physical-existence "$4" \
    --metadata-visibility "$5" --physical-state "$6" \
    --repository-state "$7" --history-state "$8" \
    --repo-history-relation "$9" --schema-relation "${10}" \
    --registry-state "${11}" --onboarding-state "${12}"
}

validate_contract() {
  node -e '
    const fs = require("fs");
    const x = JSON.parse(fs.readFileSync(0, "utf8"));
    const fail = message => { throw new Error(message); };
    x.contractVersion === 2 || x.contractVersion === null || fail("contractVersion");
    typeof x.rulesVersion === "string" || fail("rulesVersion");
    x.declarations && typeof x.declarations === "object" || fail("declarations");
    x.observations && typeof x.observations === "object" || fail("observations");
    ["NEW_EF","EXISTING_EF","EXISTING_LEGACY","UNCLASSIFIED"].includes(x.inferences?.scenario) || fail("scenario");
    typeof x.inferences.hasPendingMigrations === "boolean" || fail("pending type");
    ["ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE","BLOCKED"].includes(x.decision?.status) || fail("status");
    Array.isArray(x.decision.blocks) || fail("blocks type");
    Array.isArray(x.warnings) || fail("warnings type");
    Array.isArray(x.unknownFields) || fail("unknownFields type");
    new Set(x.decision.blocks).size === x.decision.blocks.length || fail("duplicate blocks");
    if (x.decision.status === "BLOCKED") {
      x.decision.blocks.length > 0 || fail("blocked without blocks");
      x.decision.primaryBlock === x.decision.blocks[0] || fail("primary order");
    } else {
      x.decision.blocks.length === 0 || fail("eligible with blocks");
      x.decision.primaryBlock === null || fail("eligible primary");
    }
  '
}

json_value() {
  local json="$1" path="$2"
  printf '%s' "$json" | node -e '
    const fs=require("fs"); let value=JSON.parse(fs.readFileSync(0,"utf8"));
    for (const part of process.argv[1].split(".")) value=value[part];
    process.stdout.write(value === null ? "null" : String(value));
  ' "$path"
}

json_blocks() {
  local json="$1"
  printf '%s' "$json" | node -e 'const fs=require("fs");process.stdout.write(JSON.stringify(JSON.parse(fs.readFileSync(0,"utf8")).decision.blocks));'
}

has_block() {
  local json="$1" expected="$2"
  printf '%s' "$json" | node -e '
    const fs=require("fs"), x=JSON.parse(fs.readFileSync(0,"utf8"));
    process.exit(x.decision.blocks.includes(process.argv[1]) ? 0 : 1);
  ' "$expected"
}

assert_case() {
  local name="$1" expected_scenario="$2" expected_status="$3" expected_primary="$4" expected_pending="$5"
  shift 5
  local result
  result="$(classify_values "$@")"
  printf '%s' "$result" | validate_contract
  [[ "$(json_value "$result" inferences.scenario)" == "$expected_scenario" ]]
  [[ "$(json_value "$result" decision.status)" == "$expected_status" ]]
  [[ "$(json_value "$result" decision.primaryBlock)" == "$expected_primary" ]]
  [[ "$(json_value "$result" inferences.hasPendingMigrations)" == "$expected_pending" ]]
  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $name"
}

assert_block() {
  local name="$1" expected_block="$2"
  shift 2
  local result
  result="$(classify_values "$@")"
  printf '%s' "$result" | validate_contract
  [[ "$(json_value "$result" decision.status)" == BLOCKED ]]
  has_block "$result" "$expected_block"
  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $name"
}

assert_invalid_invocation() {
  local name="$1"
  shift
  local stderr_file stdout exit_code stderr
  stderr_file="$(mktemp)"
  if stdout="$(classify "$@" 2>"$stderr_file")"; then
    rm -f "$stderr_file"
    echo "FAIL: $name aceptó una invocación inválida"
    return 1
  else
    exit_code=$?
  fi
  stderr="$(<"$stderr_file")"
  rm -f "$stderr_file"
  [[ "$exit_code" -ne 0 && -z "$stdout" ]]
  [[ "$stderr" == classification-v2:\ invalid\ invocation:* ]]
  PASS_COUNT=$((PASS_COUNT + 1))
  echo "PASS: $name"
}

# Values: version lifecycle mode existence metadata physical repo history relation schema registry onboarding.
NEW_VALID=(2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED)
EXACT=(2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH CONSISTENT CERTIFIED MANAGED)
PREFIX=(2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT VALID_PREFIX CONSISTENT CERTIFIED MANAGED)
LEGACY_BASELINE=(2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED BASELINE_REQUIRED REQUIRED)
LEGACY_MANAGED=(2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE CONSISTENT CERTIFIED MANAGED)

assert_case "NEW_EF válido" NEW_EF ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE null true "${NEW_VALID[@]}"
assert_case "EXISTING_EF exacto" EXISTING_EF ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE null false "${EXACT[@]}"
assert_case "EXISTING_EF con pendientes" EXISTING_EF ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE null true "${PREFIX[@]}"
assert_case "legacy bloqueado por baseline" EXISTING_LEGACY BLOCKED BLOCKED_BASELINE_REQUIRED false "${LEGACY_BASELINE[@]}"
assert_case "legacy managed coherente" EXISTING_LEGACY ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE null false "${LEGACY_MANAGED[@]}"

assert_block "versión ausente" BLOCKED_UNSUPPORTED_CONTRACT_VERSION "" NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "versión inválida" BLOCKED_UNSUPPORTED_CONTRACT_VERSION 3 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "lifecycle ausente" BLOCKED_INVALID_LIFECYCLE 2 "" EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "lifecycle inválido" BLOCKED_INVALID_LIFECYCLE 2 OTHER EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "mode ausente" BLOCKED_CHANGE_MODE_REQUIRED 2 EXISTING "" EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED REQUIRED
assert_block "mode inválido" BLOCKED_INVALID_CHANGE_MODE 2 EXISTING SQL EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED REQUIRED
assert_block "NEW legacy contradictorio" BLOCKED_DECLARATION_CONFLICT 2 NEW LEGACY_UNMANAGED EXISTS SUFFICIENT EMPTY ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED REQUIRED
assert_block "base NEW inexistente" BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING 2 NEW EF_MIGRATIONS NOT_FOUND UNKNOWN UNKNOWN PRESENT_VALID UNKNOWN UNKNOWN NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "existencia desconocida" BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN 2 EXISTING EF_MIGRATIONS UNKNOWN UNKNOWN UNKNOWN PRESENT_VALID UNKNOWN UNKNOWN UNKNOWN UNKNOWN BLOCKED
assert_block "metadata insuficiente" BLOCKED_METADATA_VISIBILITY 2 NEW EF_MIGRATIONS EXISTS INSUFFICIENT EMPTY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "metadata desconocida" BLOCKED_METADATA_VISIBILITY 2 NEW EF_MIGRATIONS EXISTS UNKNOWN UNKNOWN PRESENT_VALID UNKNOWN UNKNOWN NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "estado físico desconocido" BLOCKED_PHYSICAL_STATE_UNKNOWN 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT UNKNOWN PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "NEW populated" BLOCKED_NEW_NOT_EMPTY 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "NEW technical-only" BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT TECHNICAL_ONLY PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "NEW sin migrations" BLOCKED_NEW_WITHOUT_MIGRATIONS 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "NEW con history vacío" BLOCKED_NEW_UNEXPECTED_HISTORY 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID EMPTY NOT_APPLICABLE NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "NEW con history" BLOCKED_NEW_UNEXPECTED_HISTORY 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID PRESENT EXACT_MATCH NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_block "EF sin repo" BLOCKED_EF_REPOSITORY_REQUIRED 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED ABSENT PRESENT UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "repo inválido" BLOCKED_EF_REPOSITORY_INVALID 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED INVALID PRESENT UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "repo ambiguo" BLOCKED_AMBIGUOUS_MIGRATION_PROJECT 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED AMBIGUOUS PRESENT UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "history requerido" BLOCKED_EF_HISTORY_REQUIRED 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID ABSENT NOT_APPLICABLE CONSISTENT CERTIFIED MANAGED
assert_block "history vacío" BLOCKED_EF_HISTORY_EMPTY 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID EMPTY NOT_APPLICABLE CONSISTENT CERTIFIED MANAGED
assert_block "history ilegible" BLOCKED_EF_HISTORY_UNREADABLE 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID UNREADABLE UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "history inválido" BLOCKED_EF_HISTORY_INVALID_STRUCTURE 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID INVALID_STRUCTURE UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "history sin repo" BLOCKED_HISTORY_WITHOUT_REPOSITORY 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED ABSENT PRESENT UNKNOWN CONSISTENT CERTIFIED MANAGED
assert_block "history adelantado" BLOCKED_HISTORY_AHEAD 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT HISTORY_AHEAD CONSISTENT CERTIFIED MANAGED
assert_block "history reordenado" BLOCKED_HISTORY_REORDERED 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT REORDERED CONSISTENT CERTIFIED MANAGED
assert_block "history divergente" BLOCKED_HISTORY_DIVERGED 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT DIVERGED CONSISTENT CERTIFIED MANAGED
assert_block "history con ID desconocido" BLOCKED_HISTORY_UNKNOWN_ID 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT UNKNOWN_ID CONSISTENT CERTIFIED MANAGED
assert_block "ausencia EF no infiere legacy" BLOCKED_CHANGE_MODE_REQUIRED 2 EXISTING "" EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED BASELINE_REQUIRED REQUIRED
assert_block "legacy con repo EF" BLOCKED_LEGACY_WITH_EF_REPOSITORY 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED PRESENT_VALID ABSENT NOT_APPLICABLE NOT_EVALUATED BASELINE_REQUIRED REQUIRED
assert_block "legacy con history" BLOCKED_LEGACY_WITH_EF_HISTORY 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT PRESENT UNKNOWN NOT_EVALUATED BASELINE_REQUIRED REQUIRED
assert_block "legacy vacía" BLOCKED_LEGACY_EMPTY_UNDECIDED 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT EMPTY ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED BASELINE_REQUIRED REQUIRED
assert_block "registry inválido" BLOCKED_REGISTRY_INVALID 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED INVALID REQUIRED
assert_block "registry contradictorio" BLOCKED_REGISTRY_CONTRADICTION 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE NOT_EVALUATED CONTRADICTORY REQUIRED
assert_block "drift físico" BLOCKED_PHYSICAL_DRIFT 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH DRIFT_DETECTED CERTIFIED MANAGED
assert_case "schema insuficiente conserva código" EXISTING_EF BLOCKED BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT false 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH INSUFFICIENT_EVIDENCE CERTIFIED MANAGED
assert_block "baseline pendiente" BLOCKED_BASELINE_APPROVAL_PENDING 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT NOT_APPLICABLE CONSISTENT CERTIFIED PENDING
assert_block "onboarding pendiente" BLOCKED_ONBOARDING_PENDING 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH CONSISTENT CERTIFIED PENDING
assert_block "repository enum inválido" BLOCKED_EF_REPOSITORY_INVALID 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED OTHER PRESENT EXACT_MATCH CONSISTENT CERTIFIED MANAGED
assert_block "history enum inválido" BLOCKED_EF_HISTORY_INVALID_STRUCTURE 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID OTHER EXACT_MATCH CONSISTENT CERTIFIED MANAGED
assert_block "relación enum inválida" BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT OTHER CONSISTENT CERTIFIED MANAGED
assert_block "schema enum inválido" BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH OTHER CERTIFIED MANAGED
assert_block "registry enum inválido" BLOCKED_REGISTRY_INVALID 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH CONSISTENT OTHER MANAGED
assert_block "onboarding enum inválido" BLOCKED_ONBOARDING_PENDING 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED PRESENT_VALID PRESENT EXACT_MATCH CONSISTENT CERTIFIED OTHER

# Invocation failures: non-zero, empty stdout, sanitized stderr.
assert_invalid_invocation "flags ausentes"
assert_invalid_invocation "flag desconocido" --unknown value
assert_invalid_invocation "flag duplicado" --contract-version 2 --contract-version 2
assert_invalid_invocation "flag sin valor" --contract-version
assert_invalid_invocation "argumento posicional" EXTRA
assert_invalid_invocation "argumento adicional" \
  --contract-version 2 --database-lifecycle NEW \
  --change-management-mode EF_MIGRATIONS --physical-existence EXISTS \
  --metadata-visibility SUFFICIENT --physical-state EMPTY \
  --repository-state PRESENT_VALID --history-state ABSENT \
  --repo-history-relation NOT_APPLICABLE --schema-relation NOT_EVALUATED \
  --registry-state NOT_EVALUATED --onboarding-state NOT_REQUIRED EXTRA

# Invalid raw data is normalized and never reflected into JSON.
CONTROL_RESULT="$(classify_values 2 EXISTING EF_MIGRATIONS EXISTS SUFFICIENT POPULATED $'BAD\x01VALUE' PRESENT EXACT_MATCH CONSISTENT CERTIFIED MANAGED)"
printf '%s' "$CONTROL_RESULT" | validate_contract
[[ "$(json_value "$CONTROL_RESULT" observations.repositoryState)" == INVALID ]]
has_block "$CONTROL_RESULT" BLOCKED_EF_REPOSITORY_INVALID
[[ "$CONTROL_RESULT" != *$'\x01'* ]]
echo "PASS: control U+0001 normalizado sin romper JSON"
PASS_COUNT=$((PASS_COUNT + 1))

# Contextual contradictions fail closed.
assert_case "NEW ABSENT + EXACT_MATCH" UNCLASSIFIED BLOCKED BLOCKED_HISTORY_RELATION_CONFLICT false 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT EXACT_MATCH NOT_EVALUATED NOT_EVALUATED NOT_REQUIRED
assert_case "legacy ABSENT + EXACT_MATCH" UNCLASSIFIED BLOCKED BLOCKED_HISTORY_RELATION_CONFLICT false 2 EXISTING LEGACY_UNMANAGED EXISTS SUFFICIENT POPULATED ABSENT ABSENT EXACT_MATCH CONSISTENT CERTIFIED MANAGED

RELATION_PRIORITY="$(classify_values 2 NEW EF_MIGRATIONS EXISTS SUFFICIENT EMPTY PRESENT_VALID ABSENT EXACT_MATCH INSUFFICIENT_EVIDENCE INVALID BLOCKED)"
printf '%s' "$RELATION_PRIORITY" | validate_contract
[[ "$(json_value "$RELATION_PRIORITY" decision.primaryBlock)" == BLOCKED_HISTORY_RELATION_CONFLICT ]]
has_block "$RELATION_PRIORITY" BLOCKED_HISTORY_RELATION_CONFLICT
has_block "$RELATION_PRIORITY" BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT
has_block "$RELATION_PRIORITY" BLOCKED_REGISTRY_INVALID
has_block "$RELATION_PRIORITY" BLOCKED_ONBOARDING_PENDING
echo "PASS: conflicto history precede bloqueos posteriores"
PASS_COUNT=$((PASS_COUNT + 1))

# Real flag-order invariance and primary stability when secondary blocks are added.
ORDER_A="$(classify --contract-version 2 --database-lifecycle NEW --change-management-mode LEGACY_UNMANAGED --physical-existence UNKNOWN --metadata-visibility INSUFFICIENT --physical-state UNKNOWN --repository-state INVALID --history-state UNKNOWN --repo-history-relation UNKNOWN --schema-relation UNKNOWN --registry-state INVALID --onboarding-state BLOCKED)"
ORDER_B="$(classify --onboarding-state BLOCKED --registry-state INVALID --schema-relation UNKNOWN --repo-history-relation UNKNOWN --history-state UNKNOWN --repository-state INVALID --physical-state UNKNOWN --metadata-visibility INSUFFICIENT --physical-existence UNKNOWN --change-management-mode LEGACY_UNMANAGED --database-lifecycle NEW --contract-version 2)"
[[ "$ORDER_A" == "$ORDER_B" ]]
printf '%s' "$ORDER_A" | validate_contract
[[ "$(json_value "$ORDER_A" decision.primaryBlock)" == BLOCKED_DECLARATION_CONFLICT ]]
EXPECTED_ORDER='["BLOCKED_DECLARATION_CONFLICT","BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN","BLOCKED_METADATA_VISIBILITY","BLOCKED_PHYSICAL_STATE_UNKNOWN","BLOCKED_REGISTRY_INVALID","BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT","BLOCKED_ONBOARDING_PENDING"]'
[[ "$(json_blocks "$ORDER_A")" == "$EXPECTED_ORDER" ]]
SECONDARY="$(classify_values 2 NEW LEGACY_UNMANAGED UNKNOWN INSUFFICIENT UNKNOWN INVALID UNKNOWN UNKNOWN UNKNOWN CONTRADICTORY BLOCKED)"
[[ "$(json_value "$SECONDARY" decision.primaryBlock)" == BLOCKED_DECLARATION_CONFLICT ]]
has_block "$SECONDARY" BLOCKED_REGISTRY_CONTRADICTION
echo "PASS: orden de flags, prioridad y bloqueos secundarios"
PASS_COUNT=$((PASS_COUNT + 1))

if grep -En 'classify-scenario-v2|test-classification-v2' "$SCRIPT_DIR/../action.yml" 2>/dev/null; then
  echo "FAIL: V2 fue conectado a una action pública"
  exit 1
fi
echo "PASS: V2 permanece desacoplado"
PASS_COUNT=$((PASS_COUNT + 1))

echo "OK: $PASS_COUNT casos de clasificación V2"
