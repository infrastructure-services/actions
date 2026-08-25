#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "Database Release Qualification no pudo preparar el package: $1" >&2
  exit 1
}

require_value() {
  local name="$1"
  [[ -n "${!name:-}" ]] || fail "MISSING_${name}"
}

for required in RELEASE_ID ATTESTATION_ID ENVIRONMENT_NAME SOURCE_KIND SCENARIO DATABASE_LIFECYCLE \
  DISCOVERY_STATUS DISCOVERY_REASON FORWARD_SQL ROLLBACK_SQL SCHEMA_SNAPSHOT \
  OUTPUT_DIRECTORY RESULT_FILE; do
  require_value "$required"
done

[[ "$ENVIRONMENT_NAME" == "TEST" ]] || fail "ENVIRONMENT_NOT_ALLOWED"
[[ -f "$FORWARD_SQL" ]] || fail "FORWARD_SQL_NOT_FOUND"
[[ -f "$ROLLBACK_SQL" ]] || fail "ROLLBACK_SQL_NOT_FOUND"
[[ -f "$SCHEMA_SNAPSHOT" ]] || fail "SCHEMA_SNAPSHOT_NOT_FOUND"

build_root="$(mktemp -d "${RUNNER_TEMP:?RUNNER_TEMP_REQUIRED}/database-release-qualification.XXXXXX")"
project="$GITHUB_ACTION_PATH/tools/DatabaseReleaseQualification/DatabaseReleaseQualification.csproj"
nuget_config="$GITHUB_ACTION_PATH/tools/DatabaseReleaseQualification/NuGet.Config"
cleanup() {
  rm -rf -- "$build_root"
}
trap cleanup EXIT
mkdir -p "$build_root/obj" "$build_root/bin"

dotnet restore "$project" \
  --configfile "$nuget_config" \
  -p:BaseIntermediateOutputPath="$build_root/obj/" \
  -p:BaseOutputPath="$build_root/bin/" \
  --nologo

dotnet build "$project" \
  --no-restore \
  -c Release \
  -p:BaseIntermediateOutputPath="$build_root/obj/" \
  -p:BaseOutputPath="$build_root/bin/" \
  --nologo

dll="$build_root/bin/Release/net10.0/DatabaseReleaseQualification.dll"
[[ -f "$dll" ]] || fail "ENGINE_BINARY_NOT_FOUND"

set +e
dotnet "$dll" analyze \
  --release-id "$RELEASE_ID" \
  --attestation-id "$ATTESTATION_ID" \
  --environment "$ENVIRONMENT_NAME" \
  --source-kind "$SOURCE_KIND" \
  --scenario "$SCENARIO" \
  --database-lifecycle "$DATABASE_LIFECYCLE" \
  --discovery-status "$DISCOVERY_STATUS" \
  --discovery-reason "$DISCOVERY_REASON" \
  --forward "$FORWARD_SQL" \
  --rollback "$ROLLBACK_SQL" \
  --schema "$SCHEMA_SNAPSHOT" \
  --output "$OUTPUT_DIRECTORY" \
  --result "$RESULT_FILE"
engine_exit=$?
set -e

[[ -f "$RESULT_FILE" ]] || fail "ENGINE_RESULT_NOT_AVAILABLE"

status="$(jq -r '.qualificationStatus // "UNKNOWN"' "$RESULT_FILE")"
final_risk="$(jq -r '.finalRisk // "UNKNOWN"' "$RESULT_FILE")"
requires_dba="$(jq -r '.requiresDbaApproval // false' "$RESULT_FILE")"
package_directory="$(jq -r '.packageDirectory // ""' "$RESULT_FILE")"
payload_directory="$(jq -r '.payloadDirectory // ""' "$RESULT_FILE")"
attestation_directory="$(jq -r '.attestationDirectory // ""' "$RESULT_FILE")"
payload_hash="$(jq -r '.payloadHash // ""' "$RESULT_FILE")"

{
  echo "qualification_status=$status"
  echo "final_risk=$final_risk"
  echo "requires_dba_approval=$requires_dba"
  echo "package_directory=$package_directory"
  echo "payload_directory=$payload_directory"
  echo "attestation_directory=$attestation_directory"
  echo "payload_hash=$payload_hash"
} >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT_REQUIRED}"

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo "## Database Release Qualification V1"
    echo
    echo "- Release: \`$RELEASE_ID\`"
    echo "- Environment: \`$ENVIRONMENT_NAME\`"
    echo "- Discovery: \`$DISCOVERY_STATUS\` / \`$DISCOVERY_REASON\`"
    echo "- Qualification: \`$status\`"
    echo "- Final risk: \`$final_risk\`"
    echo "- Requires DBA approval: \`$requires_dba\`"
    echo "- Rehearsal: \`NOT_EXECUTED_IN_V1_ACTION\`"
  } >> "$GITHUB_STEP_SUMMARY"
fi

exit "$engine_exit"
