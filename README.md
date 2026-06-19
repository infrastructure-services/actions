# Validate EF Core Migrations

Action composite para validar migraciones de **Entity Framework Core** contra una base **SQL Server** usando una connection string.

El action detecta migraciones pendientes, genera el SQL idempotente correspondiente, clasifica el riesgo de los cambios y expone outputs para que el pipeline pueda decidir si continúa, falla o requiere aprobación DBA.

---

## Objetivo

Este action permite validar migraciones EF Core antes de ejecutar un despliegue, ayudando a detectar:

- Migraciones pendientes contra la base de datos.
- Cambios de modelo sin migración generada.
- Operaciones SQL riesgosas.
- Necesidad de revisión DBA.
- SQL generado para análisis o auditoría.

---

## Funcionamiento general

El action realiza los siguientes pasos:

1. Instala el SDK de .NET si se informa `dotnet-version`.
2. Valida que exista el `startup-project`.
3. Detecta automáticamente proyectos con migraciones si no se informa `project`.
4. Ejecuta `dotnet restore`.
5. Ejecuta `dotnet build`.
6. Instala `dotnet-ef` en un tool-path temporal.
7. Lista migraciones contra la base usando la connection string.
8. Detecta migraciones pendientes.
9. Genera SQL idempotente para las migraciones pendientes.
10. Clasifica el riesgo del SQL generado.
11. Genera un resumen Markdown en el Step Summary.
12. Expone outputs para ser usados por otros jobs o steps.

---

## Inputs

| Input | Requerido | Default | Descripción |
|---|---:|---|---|
| `connection-string` | Sí | - | Connection string de SQL Server. |
| `project` | No | `""` | Proyecto donde están las migraciones. Si se deja vacío, se autodetecta. |
| `startup-project` | Sí | - | Proyecto startup usado por EF Core. Ejemplo: `./src/Api/Api.csproj`. |
| `db-context` | No | `""` | Nombre del DbContext. Requerido si hay más de un DbContext. |
| `configuration` | No | `Release` | Configuración de build. |
| `dotnet-version` | No | `8.0.x` | Versión de .NET SDK a instalar. Dejar vacío si el runner ya tiene dotnet. |
| `ef-tool-version` | No | `8.0.0` | Versión de `dotnet-ef` a instalar. |
| `validate-model-changes` | No | `true` | Ejecuta `dotnet ef migrations has-pending-model-changes`. |
| `fail-if-no-migrations` | No | `false` | Falla si no encuentra migraciones en el repo. |
| `environment-name` | No | `UNKNOWN` | Nombre del ambiente analizado. Ejemplo: `TEST`, `QA`, `PROD`. |
| `fail-on-pending` | No | `true` | Falla si existen migraciones pendientes. |
| `fail-on-risk-level` | No | `""` | Falla si el riesgo es igual o superior al nivel indicado. Valores: `NONE`, `LOW`, `MEDIUM`, `HIGH`. Vacío no falla por riesgo. |
| `show-sql-in-logs` | No | `true` | Muestra el SQL generado en los logs de GitHub Actions. |
| `sql-log-max-lines` | No | `500` | Cantidad máxima de líneas SQL a mostrar en logs. Usar `0` para mostrar todo. |

---

## Outputs

| Output | Descripción |
|---|---|
| `has-pending-db-migrations` | Indica si la base tiene migraciones pendientes. |
| `pending-migrations` | Lista de migraciones pendientes detectadas. |
| `migration-risk` | Riesgo calculado de las migraciones pendientes. |
| `requires-dba` | Indica si requiere revisión DBA. |
| `analysis-dir` | Carpeta donde se generó el SQL y el resumen de análisis. |
| `sql-files` | Archivos SQL generados. |

---

## Criterios de riesgo

El action analiza el SQL generado por EF Core y clasifica el riesgo general de la migración.

### HIGH

Se considera riesgo alto cuando el SQL contiene operaciones destructivas, cambios de comportamiento automático en base de datos o cambios con alto impacto potencial:

- `CREATE TRIGGER`
- `CREATE OR ALTER TRIGGER`
- `DROP TRIGGER`
- `ALTER TRIGGER`
- `DROP TABLE`
- `DROP COLUMN`
- `TRUNCATE TABLE`
- `DELETE FROM`
- `ALTER COLUMN`
- `sp_rename`
- `RENAME COLUMN`
- `RENAME TABLE`

Estas operaciones pueden eliminar datos, modificar estructuras existentes, alterar lógica automática ejecutada por la base de datos, generar locks o afectar dependencias de la aplicación.

### MEDIUM

Se considera riesgo medio cuando el SQL contiene cambios que pueden afectar integridad, performance o disponibilidad:

- `DROP CONSTRAINT`
- `ADD CONSTRAINT`
- `FOREIGN KEY`
- `UNIQUE`
- `CREATE INDEX`
- `UPDATE`
- Agregado o cambio de columnas `NOT NULL`

Estas operaciones pueden generar locks, validar datos existentes o impactar constraints e índices.

### LOW

Se considera riesgo bajo cuando el SQL contiene cambios principalmente aditivos:

- `CREATE TABLE`
- `ALTER TABLE ADD`
- Agregado de columnas
- Cambios sin operaciones destructivas conocidas

Aunque el riesgo sea bajo, el SQL generado debe revisarse antes de aplicar en ambientes críticos.

### NONE

Se informa `NONE` cuando no existen migraciones pendientes.

---

## Revisión DBA

El output `requires-dba` se calcula automáticamente:

| Riesgo | Requiere DBA |
|---|---:|
| `NONE` | No |
| `LOW` | No |
| `MEDIUM` | Sí |
| `HIGH` | Sí |

Esto permite condicionar aprobaciones manuales en el pipeline antes de continuar con el despliegue.

---

## Ejemplo de uso básico

```yaml
name: Validate EF Core Migrations

on:
  workflow_dispatch:

jobs:
  validate-migrations:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Validate EF Core migrations
        id: ef-migrations
        uses: infrastructure-services/actions/validate-efcore-migrations@main
        with:
          connection-string: ${{ secrets.DB_CONNECTION_STRING }}
          startup-project: ./src/Api/Api.csproj
          environment-name: TEST
          dotnet-version: 8.0.x
          ef-tool-version: 8.0.0
```

---

## Ejemplo autodetectando el proyecto de migraciones

Si no se informa `project`, el action busca archivos dentro de carpetas `Migrations` y detecta el `.csproj` asociado.

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    startup-project: ./src/Api/Api.csproj
    environment-name: QA
```

---

## Ejemplo informando proyecto de migraciones

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    project: ./src/Infrastructure/Infrastructure.csproj
    startup-project: ./src/Api/Api.csproj
    environment-name: PROD
```

---

## Ejemplo con DbContext específico

Usar este formato cuando el proyecto tenga más de un `DbContext`.

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    project: ./src/Infrastructure/Infrastructure.csproj
    startup-project: ./src/Api/Api.csproj
    db-context: AppDbContext
    environment-name: TEST
```

---

## Ejemplo sin fallar por migraciones pendientes

Por default, el action falla si detecta migraciones pendientes porque `fail-on-pending` tiene valor `true`.

Si se quiere usar el action solo para analizar y obtener el riesgo, se puede configurar:

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    startup-project: ./src/Api/Api.csproj
    environment-name: TEST
    fail-on-pending: "false"
```

---

## Ejemplo fallando por nivel de riesgo

El input `fail-on-risk-level` permite cortar el pipeline si el riesgo calculado es igual o superior al nivel definido.

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    startup-project: ./src/Api/Api.csproj
    environment-name: PROD
    fail-on-pending: "false"
    fail-on-risk-level: MEDIUM
```

En este ejemplo, el pipeline falla si el riesgo es `MEDIUM` o `HIGH`.

---

## Ejemplo usando outputs

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    startup-project: ./src/Api/Api.csproj
    environment-name: PROD
    fail-on-pending: "false"

- name: Show migration result
  run: |
    echo "Has pending migrations: ${{ steps.ef-migrations.outputs.has-pending-db-migrations }}"
    echo "Migration risk: ${{ steps.ef-migrations.outputs.migration-risk }}"
    echo "Requires DBA: ${{ steps.ef-migrations.outputs.requires-dba }}"
    echo "Pending migrations:"
    echo "${{ steps.ef-migrations.outputs.pending-migrations }}"
```

---

## Ejemplo con aprobación DBA

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ secrets.DB_CONNECTION_STRING }}
    startup-project: ./src/Api/Api.csproj
    environment-name: PROD
    fail-on-pending: "false"

- name: DBA approval required
  if: ${{ steps.ef-migrations.outputs.requires-dba == 'true' }}
  run: |
    echo "La migración requiere revisión DBA."
    echo "Riesgo: ${{ steps.ef-migrations.outputs.migration-risk }}"
```

> Para una aprobación real, se recomienda usar environments protegidos de GitHub Actions o un job separado asociado a un environment con reviewers obligatorios.

---

## Archivos generados

El action genera archivos dentro de:

```text
artifacts/ef-migrations/<environment-name>/
```

Ejemplos de archivos generados:

```text
migration-risk-summary.md
src_Infrastructure_Infrastructure.csproj_PROD_pending.sql
src_Infrastructure_Infrastructure.csproj_PROD_findings.md
```

### `migration-risk-summary.md`

Contiene el resumen general del análisis:

- Ambiente.
- Startup project.
- Proyecto de migraciones.
- Migraciones pendientes.
- Riesgo detectado.
- Hallazgos.
- Resultado final.
- Si requiere DBA.

### `*_pending.sql`

Contiene el SQL idempotente generado por EF Core para aplicar las migraciones pendientes.

### `*_findings.md`

Contiene los hallazgos de riesgo encontrados en el SQL generado.

---

## Publicar artifacts

El action genera los archivos, pero no los sube automáticamente como artifact de GitHub Actions.

Para publicarlos, agregar un step adicional:

```yaml
- name: Upload EF migration analysis
  if: always()
  uses: actions/upload-artifact@v4
  with:
    name: ef-migrations-${{ inputs.environment-name }}
    path: ${{ steps.ef-migrations.outputs.analysis-dir }}
```

---

## Consideraciones de seguridad

La connection string se enmascara en los logs usando `add-mask`.

```bash
echo "::add-mask::$DB_CONNECTION"
```

Aun así, se recomienda:

- Guardar la connection string en GitHub Secrets o Azure Key Vault.
- No hardcodear credenciales en workflows.
- Evitar mostrar SQL completo en logs productivos si contiene información sensible.
- Usar `show-sql-in-logs: "false"` en ambientes críticos si corresponde.

---

## Control de SQL en logs

Por default, el action muestra hasta 500 líneas del SQL generado.

```yaml
show-sql-in-logs: "true"
sql-log-max-lines: "500"
```

Para no mostrar SQL:

```yaml
show-sql-in-logs: "false"
```

Para mostrar todo el SQL:

```yaml
sql-log-max-lines: "0"
```

---

## Validación de cambios de modelo

Cuando `validate-model-changes` está en `true`, el action ejecuta:

```bash
dotnet ef migrations has-pending-model-changes
```

Esto permite detectar cambios en el modelo que todavía no tienen una migración generada.

Para desactivar esta validación:

```yaml
validate-model-changes: "false"
```

---

## Comportamiento cuando no hay migraciones

Si no se detectan migraciones en el repositorio:

- `has-pending-db-migrations=false`
- `migration-risk=NONE`
- `requires-dba=false`

Por default, el action no falla.

Para forzar que falle si no encuentra migraciones:

```yaml
fail-if-no-migrations: "true"
```

---

## Requisitos del runner

El runner debe tener acceso a:

- Código fuente del repositorio.
- Internet o feed privado necesario para hacer `dotnet restore`.
- Base SQL Server indicada en la connection string.
- GitHub Packages, NuGet privado o feeds internos que use la solución.
- SDK de .NET compatible con el proyecto.
- Permisos de red hacia la base de datos.

---

## Errores frecuentes

### `startup-project no informado`

El input `startup-project` es obligatorio.

```yaml
startup-project: ./src/Api/Api.csproj
```

### `No existe el startup-project`

El path informado no coincide con el archivo real.

Validar con:

```bash
find . -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*"
```

### Error de restore por paquetes privados

Si el proyecto usa GitHub Packages, Azure Artifacts u otro feed privado, validar que el runner tenga credenciales configuradas.

Ejemplo:

```text
401 Unauthorized
NU1301 Unable to load the service index
```

### Error de conexión SQL Server

Si `dotnet ef migrations list` no puede conectarse a la base, validar:

- Connection string.
- Usuario y password.
- Firewall.
- DNS.
- Conectividad desde el runner.
- Puerto SQL Server.
- Permisos del usuario sobre la base.

### Hay más de un DbContext

Si EF Core no puede resolver qué contexto usar, informar `db-context`.

```yaml
db-context: AppDbContext
```

---

## Uso recomendado en pipeline de deployment

Ejemplo de integración antes del despliegue:

```yaml
- name: Validate EF Core migrations
  id: ef-migrations
  uses: infrastructure-services/actions/validate-efcore-migrations@main
  with:
    connection-string: ${{ steps.get-secret.outputs.connection-string }}
    startup-project: ./src/Api/Api.csproj
    environment-name: PROD
    fail-on-pending: "false"
    fail-on-risk-level: ""

- name: Stop deployment if high risk
  if: ${{ steps.ef-migrations.outputs.migration-risk == 'HIGH' }}
  run: |
    echo "Migración de riesgo alto detectada."
    echo "Se requiere revisión antes de continuar."
    exit 1

- name: Continue deployment
  if: ${{ steps.ef-migrations.outputs.has-pending-db-migrations == 'false' || steps.ef-migrations.outputs.requires-dba == 'false' }}
  run: |
    echo "Continuando despliegue..."
```

---

## Resultado esperado

Al finalizar, el action deja disponible:

- Estado de migraciones pendientes.
- Riesgo calculado.
- Indicador de revisión DBA.
- SQL generado.
- Resumen visible en GitHub Step Summary.
- Outputs reutilizables por el workflow.

Ejemplo de salida:

```text
Tiene pendientes: true
Riesgo: MEDIUM
Requiere DBA: true
Análisis generado en: /home/runner/work/repo/repo/artifacts/ef-migrations/PROD
```

---

## Notas

- El SQL generado es idempotente.
- El action ignora referencias a `__EFMigrationsHistory` al clasificar riesgo.
- El riesgo se calcula por proyecto de migración y luego se toma el riesgo más alto como riesgo general.
- Si hay riesgo `MEDIUM` o `HIGH`, el output `requires-dba` devuelve `true`.
- `CREATE TRIGGER`, `CREATE OR ALTER TRIGGER`, `DROP TRIGGER` y `ALTER TRIGGER` se clasifican como riesgo `HIGH`.
