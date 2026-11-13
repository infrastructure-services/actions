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

`kind` (`auto`, `code` o `docs`) es opcional: en `auto` se usa `docs` si el repo tiene `mkdocs.yml`. Sin `overmindMcpUrl` o `appId` el paso se omite.

Prueba local contra un Overmind sin autenticación: `bash overmind-release-local.sh <repo> <version> <app_id> [app_name]`.
