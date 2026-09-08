#!/usr/bin/env bash
set -euo pipefail

WORKSPACE="${GITHUB_WORKSPACE:-$PWD}"
OUTPUT_ROOT="${OUTPUT_DIRECTORY:-artifacts/db-discovery}"
SAFE_ENVIRONMENT="$(printf '%s' "${ENVIRONMENT_NAME:-TEST}" | sed 's#[^A-Za-z0-9._-]#_#g')"

if [[ -z "$OUTPUT_ROOT" || "$OUTPUT_ROOT" == /* || "$OUTPUT_ROOT" =~ (^|/)\.\.(/|$) ]]; then
  echo "output-directory debe ser un path relativo dentro del workspace."
  exit 1
fi

REPORT_RELATIVE_DIRECTORY="${OUTPUT_ROOT%/}/$SAFE_ENVIRONMENT"
REPORT_DIRECTORY="$WORKSPACE/$REPORT_RELATIVE_DIRECTORY"
REPO_MIGRATIONS_FILE="$REPORT_DIRECTORY/repo-migrations.txt"
TEMP_ROOT="${RUNNER_TEMP:-/tmp}/db-repository-discovery-${GITHUB_RUN_ID:-local}-${GITHUB_JOB:-job}-${RANDOM}"
EVIDENCE_FILES="$TEMP_ROOT/evidence-files.txt"
PROJECTS_FILE="$TEMP_ROOT/projects.txt"
ATTRIBUTE_IDS="$TEMP_ROOT/attribute-ids.txt"
CANONICAL_IDS="$TEMP_ROOT/canonical-ids.txt"
INVALID_EVIDENCE="$TEMP_ROOT/invalid-evidence.txt"

mkdir -p "$REPORT_DIRECTORY" "$TEMP_ROOT"
: > "$REPO_MIGRATIONS_FILE"
: > "$EVIDENCE_FILES"
: > "$PROJECTS_FILE"
: > "$ATTRIBUTE_IDS"
: > "$CANONICAL_IDS"
: > "$INVALID_EVIDENCE"

cleanup() {
  rm -rf "$TEMP_ROOT"
}
trap cleanup EXIT

sha256_file() {
  local FILE="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$FILE" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$FILE" | awk '{print $1}'
  else
    echo "No existe una herramienta SHA256 en el runner."
    exit 1
  fi
}

emit_outputs() {
  local STATUS="$1"
  local REASON="$2"
  local REPO_HAS="$3"
  local SELECTED_PROJECT="$4"
  local SNAPSHOT_EXISTS="$5"
  local COUNT LATEST SHA

  COUNT="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
  LATEST="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | tail -n 1)"
  SHA="$(sha256_file "$REPO_MIGRATIONS_FILE")"

  {
    echo "repository_status=$STATUS"
    echo "repository_reason=$REASON"
    echo "repo_has_migrations=$REPO_HAS"
    echo "repo_migration_count=$COUNT"
    echo "repo_migrations_sha256=$SHA"
    echo "latest_repo_migration=$LATEST"
    echo "selected_migration_project=$SELECTED_PROJECT"
    echo "model_snapshot_exists=$SNAPSHOT_EXISTS"
    echo "repo_migrations_file=$REPO_MIGRATIONS_FILE"
    echo "report_relative_directory=$REPORT_RELATIVE_DIRECTORY"
  } >> "$GITHUB_OUTPUT"
}

is_valid_migration_id() {
  [[ "$1" =~ ^[0-9]{14}_[A-Za-z0-9_]+$ ]]
}

find "$WORKSPACE" -type f -name "*ModelSnapshot.cs" \
  ! -path "*/bin/*" ! -path "*/obj/*" ! -path "*/.git/*" \
  -print >> "$EVIDENCE_FILES"

grep -rlE '\[[^]]*Migration(Attribute)?[[:space:]]*\([[:space:]]*"[^"]+"' "$WORKSPACE" \
  --include="*.cs" --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git \
  >> "$EVIDENCE_FILES" 2>/dev/null || true

while IFS= read -r FILE; do
  NAME="$(basename "$FILE")"
  ID="${NAME%.cs}"
  ID="${ID%.Designer}"
  if is_valid_migration_id "$ID"; then
    printf '%s\n' "$FILE" >> "$EVIDENCE_FILES"
  fi
done < <(find "$WORKSPACE" -type f -name "*.cs" \
  ! -path "*/bin/*" ! -path "*/obj/*" ! -path "*/.git/*" -print)

sort -u "$EVIDENCE_FILES" -o "$EVIDENCE_FILES"
MODEL_SNAPSHOT_COUNT="$(find "$WORKSPACE" -type f -name "*ModelSnapshot.cs" \
  ! -path "*/bin/*" ! -path "*/obj/*" ! -path "*/.git/*" -print | wc -l | tr -d ' ')"
MODEL_SNAPSHOT_EXISTS=false
[[ "$MODEL_SNAPSHOT_COUNT" -gt 0 ]] && MODEL_SNAPSHOT_EXISTS=true
EVIDENCE_COUNT="$(sed '/^[[:space:]]*$/d' "$EVIDENCE_FILES" | wc -l | tr -d ' ')"

if [[ "$EVIDENCE_COUNT" -eq 0 ]]; then
  emit_outputs READY OK false "" false
  exit 0
fi

MISSING_PROJECT=false
while IFS= read -r EVIDENCE_FILE; do
  [[ -n "$EVIDENCE_FILE" ]] || continue
  SEARCH_DIR="$(dirname "$EVIDENCE_FILE")"
  FOUND_PROJECT=false

  while [[ "$SEARCH_DIR" == "$WORKSPACE" || "$SEARCH_DIR" == "$WORKSPACE/"* ]]; do
    mapfile -t LOCAL_PROJECTS < <(find "$SEARCH_DIR" -maxdepth 1 -type f -name "*.csproj" -print | sort)
    if [[ "${#LOCAL_PROJECTS[@]}" -gt 0 ]]; then
      printf '%s\n' "${LOCAL_PROJECTS[@]}" >> "$PROJECTS_FILE"
      FOUND_PROJECT=true
      break
    fi
    [[ "$SEARCH_DIR" == "$WORKSPACE" ]] && break
    SEARCH_DIR="$(dirname "$SEARCH_DIR")"
  done

  if [[ "$FOUND_PROJECT" != true ]]; then
    MISSING_PROJECT=true
  fi
done < "$EVIDENCE_FILES"

sort -u "$PROJECTS_FILE" -o "$PROJECTS_FILE"
PROJECT_COUNT="$(sed '/^[[:space:]]*$/d' "$PROJECTS_FILE" | wc -l | tr -d ' ')"

if [[ "$MISSING_PROJECT" == true || "$PROJECT_COUNT" -eq 0 ]]; then
  emit_outputs BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT false "" "$MODEL_SNAPSHOT_EXISTS"
  exit 0
fi

if [[ "$PROJECT_COUNT" -gt 1 ]]; then
  emit_outputs BLOCKED BLOCKED_AMBIGUOUS_MIGRATION_PROJECT false "" "$MODEL_SNAPSHOT_EXISTS"
  exit 0
fi

SELECTED_PROJECT_ABSOLUTE="$(head -n 1 "$PROJECTS_FILE")"
SELECTED_PROJECT="./${SELECTED_PROJECT_ABSOLUTE#"$WORKSPACE"/}"

while IFS= read -r EVIDENCE_FILE; do
  [[ -n "$EVIDENCE_FILE" ]] || continue

  while IFS= read -r ATTRIBUTE; do
    ID="$(printf '%s\n' "$ATTRIBUTE" | sed -E 's/^.*Migration(Attribute)?[[:space:]]*\([[:space:]]*"([^"]+)".*$/\2/')"
    printf '%s\n' "$ID" >> "$ATTRIBUTE_IDS"

    FILE_NAME="$(basename "$EVIDENCE_FILE")"
    FILE_ID="${FILE_NAME%.cs}"
    FILE_ID="${FILE_ID%.Designer}"
    if ! is_valid_migration_id "$ID" || [[ "$FILE_ID" != "$ID" ]]; then
      printf '%s\n' "$EVIDENCE_FILE" >> "$INVALID_EVIDENCE"
    fi
  done < <(grep -Eo '\[[^]]*Migration(Attribute)?[[:space:]]*\([[:space:]]*"[^"]+"' "$EVIDENCE_FILE" || true)

  FILE_NAME="$(basename "$EVIDENCE_FILE")"
  FILE_ID="${FILE_NAME%.cs}"
  FILE_ID="${FILE_ID%.Designer}"
  if is_valid_migration_id "$FILE_ID"; then
    if [[ "$FILE_NAME" == *.Designer.cs ]]; then
      PAIR_FILE="${EVIDENCE_FILE%.Designer.cs}.cs"
    else
      PAIR_FILE="${EVIDENCE_FILE%.cs}.Designer.cs"
    fi

    if [[ ! -f "$PAIR_FILE" ]]; then
      printf '%s\n' "$EVIDENCE_FILE" >> "$INVALID_EVIDENCE"
    else
      printf '%s\n' "$FILE_ID" >> "$CANONICAL_IDS"
    fi
  fi
done < "$EVIDENCE_FILES"

while IFS= read -r ID; do
  [[ -n "$ID" ]] || continue
  if is_valid_migration_id "$ID"; then
    printf '%s\n' "$ID" >> "$CANONICAL_IDS"
  else
    printf '%s\n' "$ID" >> "$INVALID_EVIDENCE"
  fi
done < "$ATTRIBUTE_IDS"

sort -u "$CANONICAL_IDS" -o "$REPO_MIGRATIONS_FILE"
CANONICAL_COUNT="$(sed '/^[[:space:]]*$/d' "$REPO_MIGRATIONS_FILE" | wc -l | tr -d ' ')"
INVALID_COUNT="$(sed '/^[[:space:]]*$/d' "$INVALID_EVIDENCE" | wc -l | tr -d ' ')"

if [[ "$CANONICAL_COUNT" -eq 0 || "$INVALID_COUNT" -gt 0 ]]; then
  : > "$REPO_MIGRATIONS_FILE"
  emit_outputs BLOCKED BLOCKED_EF_REPOSITORY_INCONSISTENT false "$SELECTED_PROJECT" "$MODEL_SNAPSHOT_EXISTS"
  exit 0
fi

emit_outputs READY OK true "$SELECTED_PROJECT" "$MODEL_SNAPSHOT_EXISTS"
