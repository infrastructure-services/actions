#!/usr/bin/env bash
# Prueba local de action.yml: ejecuta sus mismos pasos sobre un repo ya clonado, contra un Overmind sin autenticación.
# Uso: [KIND=docs|code] overmind-release-local.sh <repo> <version> <app_id> [app_name]
# Sin KIND se detecta como en la action: docs si el repo tiene mkdocs.yml, si no code.
# Requiere git, node, uv y Copilot CLI (copilot login o COPILOT_GITHUB_TOKEN). OVERMIND_MCP_URL por defecto http://localhost:8081/mcp.
# Con un gateway MCP de LiteLLM: OVERMIND_MCP_URL=<url del gateway> y OVERMIND_API_KEY=<virtual key>.
set -euo pipefail
[[ $# -ge 3 ]] || { sed -n '2,4p' "$0"; exit 2; }
repo="$(cd "$1" && pwd)"; version="$2"; app_id="$3"; app_name="${4:-$(basename "$repo")}"
action="$(cd "$(dirname "$0")" && pwd)/action.yml"
mcp_url="${OVERMIND_MCP_URL:-http://localhost:8081/mcp}"
native() { cygpath -m "$1" 2>/dev/null || echo "$1"; }  # node y copilot son nativos de Windows

tmp="$(mktemp -d)"
steps="$tmp/steps"; mkdir -p "$steps"
export RUNNER_TEMP; RUNNER_TEMP="$(native "$tmp/runner")"; mkdir -p "$RUNNER_TEMP"
export GITHUB_ENV; GITHUB_ENV="$(native "$tmp/env")"; : > "$tmp/env"
export GITHUB_REPOSITORY="local/$app_name"

uv run --with pyyaml python - "$(native "$action")" "$(native "$steps")" <<'PY'
import sys, yaml
steps = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))["runs"]["steps"]
by_name = {s["name"]: s["run"] for s in steps if "run" in s}
for name, file in (("Prepare context", "prepare"), ("Write agent prompt", "prompt"), ("Write documentation prompt", "prompt-docs"),
                   ("Configure Overmind MCP", "mcp"), ("Collect dependencies", "collect"), ("Sync documentation", "docs-sync"),
                   ("Analyze and ingest", "analyze"), ("Apply dependencies", "apply")):
    open(f"{sys.argv[2]}/{file}.sh", "w", encoding="utf-8", newline="\n").write(by_name[name])
PY

echo "== Prepare context ($repo @ $(git -C "$repo" rev-parse --short HEAD), versión $version)"
(cd "$repo" && INPUT_KIND="${KIND:-auto}" APP_VERSION="$version" APP_ID="$app_id" APP_NAME="$app_name" PROJECT=local \
  RUN_URL="local-run" bash "$steps/prepare.sh")
export work; work="$(sed -n 's/^work=//p' "$tmp/env")"
kind="$(sed -n 's/^KIND=//p' "$tmp/env")"

echo "== Write agent prompt ($kind) + Configure Overmind MCP ($mcp_url)"
if [[ "$kind" == docs ]]; then bash "$steps/prompt-docs.sh"; else bash "$steps/prompt.sh"; fi
OVERMIND_MCP_URL="$mcp_url" OVERMIND_TOKEN_URL="" bash "$steps/mcp.sh"

if [[ "$kind" != docs ]]; then
  echo "== Collect dependencies"
  (cd "$repo" && bash "$steps/collect.sh")
fi

if [[ "$kind" == docs ]]; then
  echo "== Sync documentation"
  (cd "$work" && APP_ID="$app_id" bash "$steps/docs-sync.sh")
  cat "$work/ctx/docs-report.md"
fi

echo "== Contexto que ve el agente"
cat "$work/ctx/meta.txt"; echo "-- commits: $(wc -l < "$work/ctx/commits.txt"), archivos en src: $(find "$work/src" -type f | wc -l)"

echo "== Analyze and ingest (Copilot CLI)"
(cd "$work" && GITHUB_STEP_SUMMARY="$work/summary.md" bash "$steps/analyze.sh")
if [[ "$kind" != docs ]]; then
  echo "== Apply dependencies"
  (cd "$work" && APP_ID="$app_id" bash "$steps/apply.sh")
fi

echo "== Documentos en Overmind"
api="${mcp_url%/mcp}/api/v1/documents"
release_id="$(sed -n 's/^release_id: //p' "$work/ctx/meta.txt")"
for id in "gitops:prod:app-profile:$app_id" "$release_id"; do
  echo "-- $id"; curl -fsS "$api/$id" || echo "(no existe)"; echo
done
echo "Archivos de la corrida: $work"
