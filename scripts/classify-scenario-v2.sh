#!/usr/bin/env bash
set -euo pipefail

# Pure Classification Contract V2. It consumes normalized facts and performs no
# discovery, SQL, network, GitHub, Registry, or filesystem access.

invocation_error() {
  printf '%s\n' "classification-v2: invalid invocation: $1" >&2
  exit 64
}

technical_error() {
  local exit_code=$?
  trap - ERR
  printf '%s\n' 'classification-v2: internal error' >&2
  exit "$exit_code"
}
trap technical_error ERR

declare -A FLAG_VARIABLE=(
  [--contract-version]=RAW_CONTRACT_VERSION
  [--database-lifecycle]=RAW_DATABASE_LIFECYCLE
  [--change-management-mode]=RAW_CHANGE_MANAGEMENT_MODE
  [--physical-existence]=RAW_PHYSICAL_EXISTENCE
  [--metadata-visibility]=RAW_METADATA_VISIBILITY
  [--physical-state]=RAW_PHYSICAL_STATE
  [--repository-state]=RAW_REPOSITORY_STATE
  [--history-state]=RAW_HISTORY_STATE
  [--repo-history-relation]=RAW_REPO_HISTORY_RELATION
  [--schema-relation]=RAW_SCHEMA_RELATION
  [--registry-state]=RAW_REGISTRY_STATE
  [--onboarding-state]=RAW_ONBOARDING_STATE
)
REQUIRED_FLAGS=(
  --contract-version --database-lifecycle --change-management-mode
  --physical-existence --metadata-visibility --physical-state
  --repository-state --history-state --repo-history-relation
  --schema-relation --registry-state --onboarding-state
)
declare -A PROVIDED_FLAGS=()

while (( $# > 0 )); do
  flag="$1"
  [[ "$flag" == --* ]] || invocation_error 'unexpected positional argument'
  [[ -n "${FLAG_VARIABLE[$flag]-}" ]] || invocation_error 'unknown flag'
  [[ -z "${PROVIDED_FLAGS[$flag]-}" ]] || invocation_error 'duplicate flag'
  (( $# >= 2 )) || invocation_error 'flag without value'
  [[ "$2" != --* ]] || invocation_error 'flag without value'
  printf -v "${FLAG_VARIABLE[$flag]}" '%s' "$2"
  PROVIDED_FLAGS["$flag"]=1
  shift 2
done
for flag in "${REQUIRED_FLAGS[@]}"; do
  [[ -n "${PROVIDED_FLAGS[$flag]-}" ]] || invocation_error 'required flag missing'
done

RULES_VERSION=1
BLOCK_PRIORITY=(
  BLOCKED_UNSUPPORTED_CONTRACT_VERSION BLOCKED_INVALID_LIFECYCLE
  BLOCKED_CHANGE_MODE_REQUIRED BLOCKED_INVALID_CHANGE_MODE
  BLOCKED_DECLARATION_CONFLICT BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING
  BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN BLOCKED_METADATA_VISIBILITY
  BLOCKED_PHYSICAL_STATE_UNKNOWN BLOCKED_NEW_NOT_EMPTY
  BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED BLOCKED_NEW_WITHOUT_MIGRATIONS
  BLOCKED_NEW_UNEXPECTED_HISTORY BLOCKED_EF_REPOSITORY_REQUIRED
  BLOCKED_EF_REPOSITORY_INVALID BLOCKED_AMBIGUOUS_MIGRATION_PROJECT
  BLOCKED_EF_HISTORY_REQUIRED BLOCKED_EF_HISTORY_EMPTY
  BLOCKED_EF_HISTORY_UNREADABLE BLOCKED_EF_HISTORY_INVALID_STRUCTURE
  BLOCKED_HISTORY_WITHOUT_REPOSITORY BLOCKED_HISTORY_AHEAD
  BLOCKED_HISTORY_REORDERED BLOCKED_HISTORY_DIVERGED
  BLOCKED_HISTORY_UNKNOWN_ID BLOCKED_HISTORY_RELATION_CONFLICT
  BLOCKED_LEGACY_NOT_DECLARED
  BLOCKED_LEGACY_WITH_EF_REPOSITORY BLOCKED_LEGACY_WITH_EF_HISTORY
  BLOCKED_LEGACY_EMPTY_UNDECIDED BLOCKED_REGISTRY_INVALID
  BLOCKED_REGISTRY_CONTRADICTION BLOCKED_PHYSICAL_DRIFT
  BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT BLOCKED_BASELINE_REQUIRED
  BLOCKED_BASELINE_APPROVAL_PENDING BLOCKED_ONBOARDING_PENDING
)
declare -A DETECTED_BLOCKS=()
UNKNOWN_FIELDS=()

add_block() { DETECTED_BLOCKS["$1"]=1; }
is_one_of() {
  local actual="$1"
  shift
  local allowed
  for allowed in "$@"; do
    [[ "$actual" == "$allowed" ]] && return 0
  done
  return 1
}
normalize_enum() {
  local output_name="$1" raw_value="$2" block="$3" unknown_field="$4"
  shift 4
  if is_one_of "$raw_value" "$@"; then
    printf -v "$output_name" '%s' "$raw_value"
    [[ "$raw_value" != UNKNOWN ]] || UNKNOWN_FIELDS+=("$unknown_field")
  else
    printf -v "$output_name" '%s' INVALID
    add_block "$block"
  fi
}

CONTRACT_VERSION_JSON=null
if [[ "$RAW_CONTRACT_VERSION" == 2 ]]; then
  CONTRACT_VERSION_JSON=2
else
  add_block BLOCKED_UNSUPPORTED_CONTRACT_VERSION
fi
normalize_enum DATABASE_LIFECYCLE "$RAW_DATABASE_LIFECYCLE" BLOCKED_INVALID_LIFECYCLE databaseLifecycle NEW EXISTING
if [[ -z "$RAW_CHANGE_MANAGEMENT_MODE" ]]; then
  CHANGE_MANAGEMENT_MODE=INVALID
  add_block BLOCKED_CHANGE_MODE_REQUIRED
else
  normalize_enum CHANGE_MANAGEMENT_MODE "$RAW_CHANGE_MANAGEMENT_MODE" BLOCKED_INVALID_CHANGE_MODE changeManagementMode EF_MIGRATIONS LEGACY_UNMANAGED
fi
normalize_enum PHYSICAL_EXISTENCE "$RAW_PHYSICAL_EXISTENCE" BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN physicalExistence EXISTS NOT_FOUND UNKNOWN
normalize_enum METADATA_VISIBILITY "$RAW_METADATA_VISIBILITY" BLOCKED_METADATA_VISIBILITY metadataVisibility SUFFICIENT INSUFFICIENT UNKNOWN
normalize_enum PHYSICAL_STATE "$RAW_PHYSICAL_STATE" BLOCKED_PHYSICAL_STATE_UNKNOWN physicalState EMPTY TECHNICAL_ONLY POPULATED UNKNOWN
normalize_enum REPOSITORY_STATE "$RAW_REPOSITORY_STATE" BLOCKED_EF_REPOSITORY_INVALID repositoryState PRESENT_VALID ABSENT INVALID AMBIGUOUS UNKNOWN
normalize_enum HISTORY_STATE "$RAW_HISTORY_STATE" BLOCKED_EF_HISTORY_INVALID_STRUCTURE historyState ABSENT EMPTY PRESENT UNREADABLE INVALID_STRUCTURE UNKNOWN
normalize_enum REPO_HISTORY_RELATION "$RAW_REPO_HISTORY_RELATION" BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT repoHistoryRelation NOT_APPLICABLE EXACT_MATCH VALID_PREFIX HISTORY_AHEAD DIVERGED REORDERED UNKNOWN_ID UNKNOWN
normalize_enum SCHEMA_RELATION "$RAW_SCHEMA_RELATION" BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT schemaRelation NOT_EVALUATED CONSISTENT DRIFT_DETECTED INSUFFICIENT_EVIDENCE UNKNOWN
normalize_enum REGISTRY_STATE "$RAW_REGISTRY_STATE" BLOCKED_REGISTRY_INVALID registryState NOT_EVALUATED TARGET_NOT_REGISTERED BASELINE_REQUIRED CERTIFIED INVALID CONTRADICTORY UNKNOWN
normalize_enum ONBOARDING_STATE "$RAW_ONBOARDING_STATE" BLOCKED_ONBOARDING_PENDING onboardingState NOT_REQUIRED REQUIRED PENDING MANAGED BLOCKED

if [[ "$DATABASE_LIFECYCLE" == NEW && "$CHANGE_MANAGEMENT_MODE" == LEGACY_UNMANAGED ]]; then
  add_block BLOCKED_DECLARATION_CONFLICT
fi
if [[ "$DATABASE_LIFECYCLE" == EXISTING && "$CHANGE_MANAGEMENT_MODE" == INVALID
  && "$REPOSITORY_STATE" == ABSENT && "$HISTORY_STATE" == ABSENT ]]; then
  add_block BLOCKED_LEGACY_NOT_DECLARED
fi
case "$PHYSICAL_EXISTENCE" in
  NOT_FOUND)
    if [[ "$DATABASE_LIFECYCLE" == NEW ]]; then
      add_block BLOCKED_DATABASE_NOT_FOUND_NO_PROVISIONING
    else
      add_block BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN
    fi
    ;;
  UNKNOWN|INVALID) add_block BLOCKED_PHYSICAL_EXISTENCE_UNKNOWN ;;
esac
[[ "$METADATA_VISIBILITY" == SUFFICIENT ]] || add_block BLOCKED_METADATA_VISIBILITY
[[ "$PHYSICAL_STATE" != UNKNOWN && "$PHYSICAL_STATE" != INVALID ]] || add_block BLOCKED_PHYSICAL_STATE_UNKNOWN
if [[ "$METADATA_VISIBILITY" != SUFFICIENT && "$PHYSICAL_STATE" == EMPTY ]]; then
  add_block BLOCKED_PHYSICAL_STATE_UNKNOWN
fi

# A repo-history relation is meaningful only when its inputs support it.
if [[ "$HISTORY_STATE" == ABSENT || "$HISTORY_STATE" == EMPTY ]]; then
  [[ "$REPO_HISTORY_RELATION" == NOT_APPLICABLE ]] || add_block BLOCKED_HISTORY_RELATION_CONFLICT
elif [[ "$HISTORY_STATE" == PRESENT && "$REPOSITORY_STATE" == PRESENT_VALID ]]; then
  [[ "$REPO_HISTORY_RELATION" != NOT_APPLICABLE ]] || add_block BLOCKED_HISTORY_RELATION_CONFLICT
elif ! is_one_of "$REPO_HISTORY_RELATION" NOT_APPLICABLE UNKNOWN INVALID; then
  add_block BLOCKED_HISTORY_RELATION_CONFLICT
fi

if [[ "$DATABASE_LIFECYCLE" == NEW && "$CHANGE_MANAGEMENT_MODE" == EF_MIGRATIONS ]]; then
  case "$PHYSICAL_STATE" in
    POPULATED) add_block BLOCKED_NEW_NOT_EMPTY ;;
    TECHNICAL_ONLY) add_block BLOCKED_NEW_TECHNICAL_ONLY_UNDECIDED ;;
  esac
  case "$REPOSITORY_STATE" in
    ABSENT|UNKNOWN) add_block BLOCKED_NEW_WITHOUT_MIGRATIONS ;;
    INVALID) add_block BLOCKED_EF_REPOSITORY_INVALID ;;
    AMBIGUOUS) add_block BLOCKED_AMBIGUOUS_MIGRATION_PROJECT ;;
  esac
  [[ "$HISTORY_STATE" == ABSENT ]] || add_block BLOCKED_NEW_UNEXPECTED_HISTORY
fi
if [[ "$DATABASE_LIFECYCLE" == EXISTING && "$CHANGE_MANAGEMENT_MODE" == EF_MIGRATIONS ]]; then
  case "$REPOSITORY_STATE" in
    ABSENT|UNKNOWN) add_block BLOCKED_EF_REPOSITORY_REQUIRED ;;
    INVALID) add_block BLOCKED_EF_REPOSITORY_INVALID ;;
    AMBIGUOUS) add_block BLOCKED_AMBIGUOUS_MIGRATION_PROJECT ;;
  esac
  case "$HISTORY_STATE" in
    ABSENT|UNKNOWN) add_block BLOCKED_EF_HISTORY_REQUIRED ;;
    EMPTY) add_block BLOCKED_EF_HISTORY_EMPTY ;;
    UNREADABLE) add_block BLOCKED_EF_HISTORY_UNREADABLE ;;
    INVALID_STRUCTURE|INVALID) add_block BLOCKED_EF_HISTORY_INVALID_STRUCTURE ;;
  esac
  [[ "$REPOSITORY_STATE" != ABSENT || "$HISTORY_STATE" != PRESENT ]] || add_block BLOCKED_HISTORY_WITHOUT_REPOSITORY
  case "$REPO_HISTORY_RELATION" in
    HISTORY_AHEAD) add_block BLOCKED_HISTORY_AHEAD ;;
    REORDERED) add_block BLOCKED_HISTORY_REORDERED ;;
    DIVERGED) add_block BLOCKED_HISTORY_DIVERGED ;;
    UNKNOWN_ID) add_block BLOCKED_HISTORY_UNKNOWN_ID ;;
    UNKNOWN|INVALID)
      if [[ "$HISTORY_STATE" == PRESENT && "$REPOSITORY_STATE" == PRESENT_VALID ]]; then
        add_block BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT
      fi
      ;;
  esac
fi
if [[ "$DATABASE_LIFECYCLE" == EXISTING && "$CHANGE_MANAGEMENT_MODE" == LEGACY_UNMANAGED ]]; then
  [[ "$REPOSITORY_STATE" == ABSENT ]] || add_block BLOCKED_LEGACY_WITH_EF_REPOSITORY
  [[ "$HISTORY_STATE" == ABSENT ]] || add_block BLOCKED_LEGACY_WITH_EF_HISTORY
  [[ "$PHYSICAL_STATE" != EMPTY ]] || add_block BLOCKED_LEGACY_EMPTY_UNDECIDED
fi

case "$REGISTRY_STATE" in
  INVALID|UNKNOWN|TARGET_NOT_REGISTERED) add_block BLOCKED_REGISTRY_INVALID ;;
  CONTRADICTORY) add_block BLOCKED_REGISTRY_CONTRADICTION ;;
  BASELINE_REQUIRED) add_block BLOCKED_BASELINE_REQUIRED ;;
  NOT_EVALUATED) [[ "$DATABASE_LIFECYCLE" != EXISTING ]] || add_block BLOCKED_REGISTRY_INVALID ;;
esac
case "$SCHEMA_RELATION" in
  DRIFT_DETECTED) add_block BLOCKED_PHYSICAL_DRIFT ;;
  INSUFFICIENT_EVIDENCE|UNKNOWN|INVALID) add_block BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT ;;
  NOT_EVALUATED)
    if [[ "$DATABASE_LIFECYCLE" == EXISTING && "$CHANGE_MANAGEMENT_MODE" == EF_MIGRATIONS ]]; then
      add_block BLOCKED_SCHEMA_EVIDENCE_INSUFFICIENT
    fi
    ;;
esac
if [[ "$CHANGE_MANAGEMENT_MODE" == LEGACY_UNMANAGED && "$ONBOARDING_STATE" == PENDING ]]; then
  add_block BLOCKED_BASELINE_APPROVAL_PENDING
fi
if is_one_of "$ONBOARDING_STATE" REQUIRED PENDING BLOCKED INVALID; then
  add_block BLOCKED_ONBOARDING_PENDING
elif [[ "$DATABASE_LIFECYCLE" == EXISTING && "$ONBOARDING_STATE" == NOT_REQUIRED ]]; then
  add_block BLOCKED_ONBOARDING_PENDING
fi

SCENARIO=UNCLASSIFIED
HAS_PENDING_MIGRATIONS=false
if [[ "$DATABASE_LIFECYCLE" == NEW
  && "$CHANGE_MANAGEMENT_MODE" == EF_MIGRATIONS
  && "$PHYSICAL_EXISTENCE" == EXISTS
  && "$METADATA_VISIBILITY" == SUFFICIENT
  && "$PHYSICAL_STATE" == EMPTY
  && "$REPOSITORY_STATE" == PRESENT_VALID
  && "$HISTORY_STATE" == ABSENT
  && "$REPO_HISTORY_RELATION" == NOT_APPLICABLE ]]; then
  SCENARIO=NEW_EF
  HAS_PENDING_MIGRATIONS=true
elif [[ "$DATABASE_LIFECYCLE" == EXISTING
  && "$CHANGE_MANAGEMENT_MODE" == EF_MIGRATIONS
  && "$PHYSICAL_EXISTENCE" == EXISTS
  && "$METADATA_VISIBILITY" == SUFFICIENT
  && "$REPOSITORY_STATE" == PRESENT_VALID
  && "$HISTORY_STATE" == PRESENT
  && ( "$REPO_HISTORY_RELATION" == EXACT_MATCH || "$REPO_HISTORY_RELATION" == VALID_PREFIX ) ]]; then
  SCENARIO=EXISTING_EF
  [[ "$REPO_HISTORY_RELATION" != VALID_PREFIX ]] || HAS_PENDING_MIGRATIONS=true
elif [[ "$DATABASE_LIFECYCLE" == EXISTING
  && "$CHANGE_MANAGEMENT_MODE" == LEGACY_UNMANAGED
  && "$PHYSICAL_EXISTENCE" == EXISTS
  && "$METADATA_VISIBILITY" == SUFFICIENT
  && "$PHYSICAL_STATE" == POPULATED
  && "$REPOSITORY_STATE" == ABSENT
  && "$HISTORY_STATE" == ABSENT
  && "$REPO_HISTORY_RELATION" == NOT_APPLICABLE ]]; then
  SCENARIO=EXISTING_LEGACY
fi

BLOCKS=()
for block in "${BLOCK_PRIORITY[@]}"; do
  [[ -z "${DETECTED_BLOCKS[$block]-}" ]] || BLOCKS+=("$block")
done
STATUS=ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE
PRIMARY_BLOCK=
if (( ${#BLOCKS[@]} > 0 )); then
  STATUS=BLOCKED
  PRIMARY_BLOCK="${BLOCKS[0]}"
fi

json_string() {
  [[ "$1" =~ ^[A-Za-z0-9_]+$ ]] || return 70
  printf '"%s"' "$1"
}
json_array() {
  local separator= value
  printf '['
  for value in "$@"; do
    printf '%s' "$separator"
    json_string "$value"
    separator=,
  done
  printf ']'
}
PRIMARY_BLOCK_JSON=null
[[ -z "$PRIMARY_BLOCK" ]] || PRIMARY_BLOCK_JSON="$(json_string "$PRIMARY_BLOCK")"
OUTPUT_JSON="$(
  printf '{\n'
  printf '  "contractVersion": %s,\n' "$CONTRACT_VERSION_JSON"
  printf '  "rulesVersion": %s,\n' "$(json_string "$RULES_VERSION")"
  printf '  "declarations": {"databaseLifecycle": %s, "changeManagementMode": %s},\n' "$(json_string "$DATABASE_LIFECYCLE")" "$(json_string "$CHANGE_MANAGEMENT_MODE")"
  printf '  "observations": {"physicalExistence": %s, "metadataVisibility": %s, "physicalState": %s, "repositoryState": %s, "historyState": %s, "repoHistoryRelation": %s, "schemaRelation": %s, "registryState": %s, "onboardingState": %s},\n' \
    "$(json_string "$PHYSICAL_EXISTENCE")" "$(json_string "$METADATA_VISIBILITY")" "$(json_string "$PHYSICAL_STATE")" \
    "$(json_string "$REPOSITORY_STATE")" "$(json_string "$HISTORY_STATE")" "$(json_string "$REPO_HISTORY_RELATION")" \
    "$(json_string "$SCHEMA_RELATION")" "$(json_string "$REGISTRY_STATE")" "$(json_string "$ONBOARDING_STATE")"
  printf '  "inferences": {"scenario": %s, "hasPendingMigrations": %s},\n' "$(json_string "$SCENARIO")" "$HAS_PENDING_MIGRATIONS"
  printf '  "decision": {"status": %s, "primaryBlock": %s, "blocks": %s},\n' "$(json_string "$STATUS")" "$PRIMARY_BLOCK_JSON" "$(json_array "${BLOCKS[@]}")"
  printf '  "warnings": [],\n'
  printf '  "unknownFields": %s\n' "$(json_array "${UNKNOWN_FIELDS[@]}")"
  printf '}\n'
)"
printf '%s\n' "$OUTPUT_JSON"
