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
        copilotToken: ${{ secrets.COPILOT_GITHUB_TOKEN || secrets.ARQUITECTURA_DEPLOY }}
        copilotModel: ${{ vars.OVERMIND_RELEASE_MODEL }}
```

Con un gateway MCP de LiteLLM: `overmindMcpUrl` es la URL del gateway y `overmindApiKey` su virtual key (sin OAuth); si hay otros servidores con herramientas del mismo nombre, `overmindToolPrefix` fija el prefijo de Overmind (por ejemplo `overmind-`).

En repositorios de código el action además:
- extrae las librerías directas de cada capa (csproj/fsproj/vbproj y packages.config para C#; package.json para JS y React; go.mod; composer.json para PHP; pyproject.toml y requirements*.txt para Python) y los valores de despliegue de `.devops/*.yaml`, saneados por un script (sin credenciales, tokens, claves ni IPs; los secretos solo por nombre). El agente lee solo ese resultado, nunca los archivos originales;
- el Release es uno por versión desplegada: `gitops:prod:release:<app_id>:<versión>` (sin `v` inicial); un nuevo despliegue de la misma versión lo actualiza. Se enlaza con `SUPERSEDES` a la versión anterior (el tag previo sin sufijo de ambiente) y con `REFERENCES` a los Releases por PR del rango que haya creado el worker;
- agrega al perfil los tags `library:<slug>` (sin versión) y `resource-type:<tipo>` (sqlserver, postgres, mongodb, redis, kafka...) con `update_document`, y registra cada librería con versión concreta como nodo `Library` (`lib:<ecosistema>:<paquete>:<versión>`) con una relación `DEPENDS_ON` desde la Release (`role` = capa, `version`, `basis` = `repo_scan`). Se omiten rangos y versiones no resolubles. La key del gateway necesita permiso sobre `get_document`, `ingest_document`, `update_document`, `build_document_graph` y `relate_documents` (y `execute_read_query` para repos de documentación).

`kind` (`auto`, `code` o `docs`) es opcional: en `auto` se usa `docs` si el repo tiene `mkdocs.yml`. Sin `overmindMcpUrl` o `appId` el paso se omite.

**Monitoreo en APM.** El action envía a Elastic APM (`https://apm-mon.andreani.com` por defecto, sin secret token), al final de cada ejecución (también si falla), una transacción `overmind-release` con un span por paso (configuración y checkout, preparar contexto, instalar la CLI, configurar el MCP, recolectar dependencias o sincronizar documentación, análisis del agente e ingesta, aplicar dependencias). El resultado es `success` o `failure` según los pasos, y lleva como labels `app_id`, `app_name`, `version`, `project`, `repository`, `model`, `release_id`, `libraries_versioned`, `library_tags` y `run_url`; no incluye secretos ni valores de configuración. Inputs opcionales: `apmServerUrl` (otro servidor), `apmSecretToken` (si el servidor lo exige, se envía como Bearer), `apmEnvironment` (por defecto `prod`) y `apmServiceName` (por defecto `overmind-release`). Un fallo del reporte solo genera un warning.

Prueba local contra un Overmind sin autenticación: `bash overmind-release-local.sh <repo> <version> <app_id> [app_name]`.
