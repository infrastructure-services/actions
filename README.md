Actions para equipo DevOps

# Overmind Release

Paso del job de deploy que, tras publicar en producción, analiza la versión con un agente (Copilot CLI) y la registra en Overmind por MCP. En repositorios con `mkdocs.yml` sincroniza antes los documentos.

```yaml
Overmind_release:
  needs: [Preparativos, Deploy_to_production]
  if: ${{ ! cancelled() && ! failure() && inputs.prod }}
  runs-on:
    group: Production
  continue-on-error: true
  timeout-minutes: 30
  steps:
    - uses: infrastructure-services/actions@overmind-ingest
      with:
        appVersion: ${{ inputs.appVersion }}
        appId: ${{ vars.APP_ID }}
        appName: ${{ vars.APPNAME }}
        project: ${{ vars.PROJECT }}
        overmindMcpUrl: ${{ vars.OVERMIND_MCP_URL }}
        overmindTokenUrl: ${{ vars.OVERMIND_TOKEN_URL }}
        overmindScope: ${{ vars.OVERMIND_SCOPE }}
        overmindClientId: ${{ secrets.OVERMIND_CLIENT_ID }}
        overmindClientSecret: ${{ secrets.OVERMIND_CLIENT_SECRET }}
        copilotToken: ${{ secrets.COPILOT_GITHUB_TOKEN || github.token }}
        copilotModel: ${{ vars.OVERMIND_RELEASE_MODEL }}
```

Con un gateway MCP de LiteLLM: `overmindMcpUrl` es la URL del gateway y `overmindApiKey` su virtual key (sin OAuth); si hay otros servidores con herramientas del mismo nombre, `overmindToolPrefix` fija el prefijo de Overmind (por ejemplo `overmind-`).

En repositorios de código el action además:
- extrae las librerías directas de cada capa (csproj, package.json, go.mod, requirements) y los valores de despliegue de `.devops/*.yaml`, saneados por un script (sin credenciales, tokens, claves ni IPs; los secretos solo por nombre). El agente lee solo ese resultado, nunca los archivos originales;
- el Release es uno por versión desplegada: `gitops:prod:release:<app_id>:<versión>` (sin `v` inicial); un nuevo despliegue de la misma versión lo actualiza. Se enlaza con `SUPERSEDES` a la versión anterior (el tag previo sin sufijo de ambiente) y con `REFERENCES` a los Releases por PR del rango que haya creado el worker;
- agrega al perfil los tags `library:<slug>` (sin versión) y `resource-type:<tipo>` (sqlserver, postgres, mongodb, redis, kafka...) con `update_document`, y registra cada librería con versión concreta como nodo `Library` (`lib:<ecosistema>:<paquete>:<versión>`) con una relación `DEPENDS_ON` desde la Release (`role` = capa, `version`, `basis` = `repo_scan`). Se omiten rangos y versiones no resolubles. La key del gateway necesita permiso sobre `get_document`, `ingest_document`, `update_document`, `build_document_graph` y `relate_documents` (y `execute_read_query` para repos de documentación).

`kind` (`auto`, `code` o `docs`) es opcional: en `auto` se usa `docs` si el repo tiene `mkdocs.yml`. Sin `overmindMcpUrl` o `appId` el paso se omite.

Prueba local contra un Overmind sin autenticación: `bash overmind-release-local.sh <repo> <version> <app_id> [app_name]`.
