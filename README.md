# Apply DB Migrations

Composite Action para aplicar migraciones de Entity Framework Core contra una base de datos SQL Server.

El action obtiene la connection string desde Azure Key Vault, detecta o recibe el proyecto de migraciones, genera scripts SQL idempotentes como evidencia y aplica las migraciones pendientes usando `dotnet-ef`.

---

## Descripción

Este action permite ejecutar migraciones EF Core de forma segura dentro de un workflow de GitHub Actions.

El flujo principal es:

1. Valida el valor de `migration-risk`.
2. Si el riesgo es `NONE`, saltea la ejecución.
3. Si el riesgo es `LOW`, `MEDIUM` o `HIGH`, obtiene la connection string desde Azure Key Vault.
4. Configura variables de conexión para la aplicación.
5. Valida que exista el `startup-project`.
6. Valida que el runner tenga instalado el SDK de .NET esperado.
7. Configura NuGet temporalmente si se informan credenciales.
8. Detecta proyectos con migraciones EF Core o usa el proyecto informado.
9. Restaura y compila la solución o proyecto.
10. Instala `dotnet-ef` en un directorio temporal.
11. Lista migraciones pendientes antes de aplicar.
12. Genera un script SQL idempotente.
13. Aplica las migraciones con `dotnet ef database update`.
14. Valida que no queden migraciones pendientes.
15. Publica el resultado en outputs y en el `GITHUB_STEP_SUMMARY`.

---

## Uso

```yaml
- name: Apply DB Migrations
  id: apply_db_migrations
  uses: infrastructure-services/actions@apply-db-migrations
  with:
    migration-risk: ${{ needs.validate_migrations.outputs.migration-risk }}
    keyvault-name: ${{ secrets.KEYVAULT_NAME }}
    client-id: ${{ secrets.AZURE_CLIENT_ID }}
    client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
    tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    secret-name: ${{ needs.get_db_secret.outputs.secret-name }}
    project: auto
    startup-project: ./src/Api/LuanaApi.csproj
    environment-name: TEST
    nuget-username: ${{ secrets.NUGET_USERNAME }}
    nuget-token: ${{ secrets.NUGET_TOKEN }}
```

> Ajustar el valor de `uses` según la forma en la que esté publicado el action en el repositorio.

---

## Inputs

| Input              | Requerido | Default   | Descripción                                                                                                                                                              |
| ------------------ | --------: | --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `migration-risk`   |        No | `NONE`    | Riesgo de migración recibido desde la validación previa. Valores permitidos: `NONE`, `LOW`, `MEDIUM`, `HIGH`.                                                            |
| `keyvault-name`    |        Sí | -         | Nombre del Azure Key Vault desde donde se obtiene la connection string.                                                                                                  |
| `client-id`        |        Sí | -         | Client ID de Azure utilizado para consultar Key Vault.                                                                                                                   |
| `client-secret`    |        Sí | -         | Client Secret de Azure utilizado para consultar Key Vault.                                                                                                               |
| `tenant-id`        |        Sí | -         | Tenant ID de Azure.                                                                                                                                                      |
| `secret-name`      |        Sí | -         | Nombre del secret en Key Vault que contiene la connection string.                                                                                                        |
| `project`          |        No | `auto`    | Proyecto de migraciones. Si se usa `auto`, el action detecta automáticamente proyectos con migraciones. También permite informar uno o más proyectos separados por coma. |
| `startup-project`  |        Sí | -         | Proyecto startup usado por EF Core para ejecutar las migraciones.                                                                                                        |
| `db-context`       |        No | `""`      | Nombre del `DbContext`. Si se deja vacío, EF Core intentará resolverlo automáticamente.                                                                                  |
| `configuration`    |        No | `Release` | Configuración de build utilizada por `dotnet restore`, `dotnet build` y `dotnet-ef`.                                                                                     |
| `dotnet-version`   |        No | `10.0.x`  | Versión esperada del SDK de .NET. El action valida el major version instalado en el runner.                                                                              |
| `ef-tool-version`  |        No | `10.0.5`  | Versión de `dotnet-ef` a instalar temporalmente.                                                                                                                         |
| `environment-name` |        Sí | -         | Nombre del ambiente donde se aplican las migraciones. Ejemplo: `TEST`, `QA` o `PROD`.                                                                                    |
| `nuget-username`   |        No | `""`      | Usuario para autenticarse contra el feed privado de NuGet.                                                                                                               |
| `nuget-token`      |        No | `""`      | Token para autenticarse contra el feed privado de NuGet.                                                                                                                 |

---

## Outputs

| Output                   | Descripción                                                 |
| ------------------------ | ----------------------------------------------------------- |
| `skipped`                | Indica si el action se salteó.                              |
| `applied`                | Indica si se aplicó al menos una migración.                 |
| `had-pending-migrations` | Indica si existían migraciones pendientes antes de aplicar. |
| `sql-files`              | Lista de archivos SQL generados.                            |
| `analysis-dir`           | Directorio donde se generaron los scripts SQL.              |

Ejemplo de uso de outputs:

```yaml
- name: Mostrar resultado
  run: |
    echo "Skipped: ${{ steps.apply_db_migrations.outputs.skipped }}"
    echo "Applied: ${{ steps.apply_db_migrations.outputs.applied }}"
    echo "Had pending migrations: ${{ steps.apply_db_migrations.outputs.had-pending-migrations }}"
    echo "SQL files:"
    echo "${{ steps.apply_db_migrations.outputs.sql-files }}"
```

---

## Comportamiento según `migration-risk`

| Valor    | Comportamiento                                                                         |
| -------- | -------------------------------------------------------------------------------------- |
| `NONE`   | No consulta Key Vault y no aplica migraciones. El action finaliza como `skipped=true`. |
| `LOW`    | Consulta Key Vault, genera SQL y aplica migraciones si existen pendientes.             |
| `MEDIUM` | Consulta Key Vault, genera SQL y aplica migraciones si existen pendientes.             |
| `HIGH`   | Consulta Key Vault, genera SQL y aplica migraciones si existen pendientes.             |

> La aprobación previa para riesgos `MEDIUM` o `HIGH` debe resolverse en el workflow que llama a este action, por ejemplo usando environments con approval o un job previo de aprobación DBA.

---

## Detección de proyectos de migraciones

Si `project` tiene el valor `auto`, el action busca archivos `.csproj` dentro del repositorio y detecta como proyecto de migraciones aquellos que tengan archivos `.cs` asociados a:

```text
/Migrations/
ModelSnapshot.cs
```

Si se quiere evitar la detección automática, se puede informar explícitamente el proyecto:

```yaml
with:
  project: ./src/Infrastructure/Infrastructure.csproj
```

También se pueden informar múltiples proyectos separados por coma:

```yaml
with:
  project: ./src/Infrastructure/Infrastructure.csproj,./src/Other.Infrastructure/Other.Infrastructure.csproj
```

---

## Connection string

La connection string se obtiene desde Azure Key Vault usando el action:

```yaml
infrastructure-services/actions@get-keyvault-secret
```

El valor recuperado se enmascara en los logs y se exporta en las siguientes variables para que EF Core y la aplicación puedan resolver la conexión:

```bash
DataAccessRegistry__TransactionalConnection
DataAccessRegistry__ReadOnlyConnection
DB_CONNECTION
ConnectionStrings__DefaultConnection
ConnectionStrings__SqlServer
ConnectionStrings__SqlConnection
```

---

## Generación de SQL

Cuando existen migraciones pendientes, el action genera un script SQL idempotente antes de aplicar la migración.

Los archivos se generan en:

```text
artifacts/ef-migrations-apply/<ENVIRONMENT_NAME>
```

Ejemplo:

```text
artifacts/ef-migrations-apply/TEST/Infrastructure-TEST-idempotent.sql
```

El SQL se genera desde la última migración aplicada hasta la última migración pendiente detectada.

---

## Aplicación de migraciones

Por cada proyecto de migraciones detectado o informado, el action ejecuta:

```bash
dotnet ef migrations list
dotnet ef migrations script --idempotent
dotnet ef database update
dotnet ef migrations list
```

Después de aplicar las migraciones, vuelve a listar el estado de EF Core para validar que no queden migraciones pendientes.

Si todavía quedan migraciones pendientes, el action falla.

---

## Validaciones de seguridad

El action corta la ejecución si detecta alguno de estos escenarios:

* `migration-risk` inválido.
* `startup-project` vacío o inexistente.
* `dotnet` no instalado en el runner.
* SDK de .NET esperado no instalado.
* No se puede obtener una connection string válida.
* No se puede listar el estado real de migraciones.
* EF Core no puede consultar correctamente la base.
* Error de conexión, timeout, login fallido o base inaccesible.
* El script SQL no se genera o queda vacío.
* Quedan migraciones pendientes después de ejecutar `database update`.

---

## Manejo de Key Vault

Si el secret configurado no existe en Key Vault, el action no falla. En ese caso:

* No aplica migraciones.
* Marca `skipped=true`.
* Marca `applied=false`.
* Marca `had-pending-migrations=false`.
* Agrega el motivo `SecretNotFound` al resumen del job.

Esto permite continuar el flujo cuando el ambiente no tiene secret configurado para aplicar migraciones.

---

## Manejo de NuGet privado

Si se informan `nuget-username` y `nuget-token`, el action genera un `NuGet.Config` temporal en `RUNNER_TEMP` con los siguientes sources:

```text
https://api.nuget.org/v3/index.json
https://nuget.pkg.github.com/architecture-it/index.json
```

También configura directorios temporales para paquetes y cache:

```bash
NUGET_PACKAGES
NUGET_HTTP_CACHE_PATH
```

Al finalizar, elimina los archivos temporales generados.

Si no se informan credenciales de NuGet, el action utiliza la configuración disponible en el repositorio o en el runner.

---

## Ejemplo completo en workflow

```yaml
name: Apply Migrations

on:
  workflow_dispatch:
    inputs:
      environment:
        description: Ambiente
        required: true
        type: choice
        options:
          - TEST
          - QA
          - PROD

      migrationRisk:
        description: Riesgo de migración
        required: true
        type: choice
        options:
          - NONE
          - LOW
          - MEDIUM
          - HIGH

jobs:
  apply-migrations:
    name: Apply DB Migrations
    runs-on: ubuntu-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Apply DB Migrations
        id: apply_db_migrations
        uses: infrastructure-services/actions@apply-db-migrations
        with:
          migration-risk: ${{ inputs.migrationRisk }}
          keyvault-name: ${{ secrets.KEYVAULT_NAME }}
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          secret-name: ${{ secrets.DB_SECRET_NAME }}
          project: auto
          startup-project: ./src/Api/LuanaApi.csproj
          db-context: ""
          configuration: Release
          dotnet-version: 10.0.x
          ef-tool-version: 10.0.5
          environment-name: ${{ inputs.environment }}
          nuget-username: ${{ secrets.NUGET_USERNAME }}
          nuget-token: ${{ secrets.NUGET_TOKEN }}

      - name: Upload SQL artifacts
        if: ${{ steps.apply_db_migrations.outputs.sql-files != '' }}
        uses: actions/upload-artifact@v4
        with:
          name: ef-migrations-sql-${{ inputs.environment }}
          path: ${{ steps.apply_db_migrations.outputs.analysis-dir }}
```

---

## Resumen en GitHub Actions

El action escribe un resumen en `GITHUB_STEP_SUMMARY` con información del resultado:

* Ambiente.
* Riesgo de migración.
* Si había migraciones pendientes.
* Si se aplicaron migraciones.
* Directorio de análisis.
* Proyectos procesados.
* Archivos SQL generados.
* Motivo del skip, cuando corresponde.

---

## Resultados posibles

### Sin migraciones por riesgo `NONE`

```text
skipped=true
applied=false
had-pending-migrations=false
```

### Secret no encontrado en Key Vault

```text
skipped=true
applied=false
had-pending-migrations=false
```

### No se encontraron proyectos con migraciones

```text
skipped=true
applied=false
had-pending-migrations=false
```

### Hay migraciones pendientes y se aplican correctamente

```text
skipped=false
applied=true
had-pending-migrations=true
```

### No hay migraciones pendientes

```text
skipped=false
applied=false
had-pending-migrations=false
```

---

## Requisitos del runner

El runner debe tener instalado:

* `bash`
* `.NET SDK`
* Acceso de red a la base de datos
* Acceso a Azure Key Vault
* Permisos para instalar tools locales de .NET
* Permisos para restaurar paquetes NuGet

El SDK de .NET instalado debe coincidir con el major version definido en `dotnet-version`.

Por ejemplo, si se configura:

```yaml
dotnet-version: 10.0.x
```

el runner debe tener instalado algún SDK `10.x`.

---

## Notas importantes

* El action no ejecuta migraciones si `migration-risk` es `NONE`.
* El action genera SQL idempotente antes de aplicar migraciones.
* El action valida el estado de migraciones antes y después de aplicar.
* Si EF Core no puede determinar el estado real de la base, el proceso falla por seguridad.
* El action no sube automáticamente los SQL como artifacts; para conservarlos se debe usar `actions/upload-artifact`.
* La aprobación de DBA no está dentro del action. Debe manejarse desde el workflow que lo consume.
