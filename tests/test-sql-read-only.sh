#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROGRAM="$SCRIPT_DIR/../tools/SqlDiscovery/Program.cs"
REPOSITORY_DISCOVERY="$SCRIPT_DIR/../scripts/discover-repository.sh"
ACTION_ROOT="$SCRIPT_DIR/.."
MUTATING_PATTERN='(^|[^[:alnum:]_])(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE)([^[:alnum:]_]|$)'

if grep -Eiq "$MUTATING_PATTERN" "$PROGRAM"; then
  echo "FAIL: el helper SQL contiene una sentencia no permitida."
  grep -Ein "$MUTATING_PATTERN" "$PROGRAM" || true
  exit 1
fi

if grep -Eq 'ApplicationIntent[[:space:]]*=[[:space:]]*ApplicationIntent\.ReadOnly' "$PROGRAM"; then
  echo "FAIL: el helper no debe solicitar routing ReadOnly."
  exit 1
fi

if grep -Fq 'DB_CONNECTION' "$REPOSITORY_DISCOVERY"; then
  echo "FAIL: la inspección estática del repositorio recibió la connection string."
  exit 1
fi

if grep -rEiq 'dotnet[[:space:]]+ef|dbcontext[[:space:]]+list|migrations[[:space:]]+list' \
  "$ACTION_ROOT/action.yml" "$ACTION_ROOT/scripts"; then
  echo "FAIL: el discovery todavía ejecuta herramientas EF o código de la aplicación."
  exit 1
fi

if grep -rEq 'dbcontexts\.json|repo-migrations\.json|dotnet-ef\.stderr' \
  "$ACTION_ROOT/action.yml" "$ACTION_ROOT/scripts"; then
  echo "FAIL: el discovery conserva stdout crudo de herramientas EF."
  exit 1
fi

for REQUIRED_EVIDENCE in \
  'HAS_PERMS_BY_NAME' \
  'VIEW DEFINITION' \
  'sys.objects' \
  'sys.schemas' \
  'sys.tables' \
  'sys.types' \
  'sys.assemblies' \
  'sys.triggers' \
  'sys.partition_functions' \
  'sys.partition_schemes' \
  'sys.fulltext_catalogs'
do
  if ! grep -Fq "$REQUIRED_EVIDENCE" "$PROGRAM"; then
    echo "FAIL: falta evidencia estructural o de permisos: $REQUIRED_EVIDENCE"
    exit 1
  fi
done

echo "OK: helper SQL protegido por guardas estáticas SELECT-only"
