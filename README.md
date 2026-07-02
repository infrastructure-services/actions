# Validate Migrations

Composite Action para validar migraciones de Entity Framework Core contra una base SQL Server.

Este action detecta migraciones pendientes, genera el SQL correspondiente, calcula el riesgo de los cambios y determina si se requiere revisión DBA antes de aplicar las migraciones.

---

## Descripción

El action ejecuta una validación completa de migraciones EF Core contra una base de datos.

El flujo principal es:

1. Recibe una `connection-string`.
2. Configura variables de conexión para que EF Core pueda resolver la base.
3. Detecta automáticamente proyectos con migraciones o usa el proyecto informado.
4. Restaura y compila la solución o el proyecto startup.
5. Instala temporalmente `dotnet-ef`.
6. Ejecuta `dotnet ef migrations list`.
7. Detecta migraciones pendientes en la base.
8. Genera un script SQL idempotente para las migraciones pendientes.
9. Clasifica el riesgo de las operaciones SQL.
10. Si `sqlcmd` está disponible, analiza el impacto real sobre las tablas afectadas.
11. Devuelve outputs para que el workflow decida si debe aplicar migraciones o pedir aprobación DBA.
12. Genera un resumen en `GITHUB_STEP_SUMMARY`.

---

## Uso básico

```yaml id="e4yq2k"
- name: Validar migraciones SQL Server TEST
  id: validate_migrations_test
  uses: infrastructure-services/actions@validate-migrations
  with:
    connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
    startup-project: ${{ needs.Preparativos.outputs.startupProject }}
    environment-name: ${{ vars.ENVIRONMENT_NAME }}
```

---

## Uso recomendado dentro del workflow

Ejemplo usando el action para validar migraciones antes de ejecutar el action de aplicación de migraciones:

```yaml id="b0n458"
- name: Validar migraciones SQL Server TEST
  id: validate_migrations_test
  uses: infrastructure-services/actions@validate-migrations
  with:
    connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
    startup-project: ${{ needs.Preparativos.outputs.startupProject }}
    environment-name: ${{ vars.ENVIRONMENT_NAME }}
    fail-on-pending: "false"
    fail-on-risk-level: ""
    show-sql-in-logs: "true"
```

Luego, en el job se pueden exponer outputs para usarlos desde otro job:

```yaml id="kduiw9"
outputs:
  should_apply_migrations: ${{ steps.validate_migrations_test.outputs.has-pending-db-migrations }}
  migration_risk: ${{ steps.validate_migrations_test.outputs.migration-risk }}
  requires_dba: ${{ steps.validate_migrations_test.outputs.requires-dba }}
  analysis_dir: ${{ steps.validate_migrations_test.outputs.analysis-dir }}
  sql_files: ${{ steps.validate_migrations_test.outputs.sql-files }}
```

De esta forma, el action de aplicación de migraciones puede ejecutarse solo cuando existan migraciones pendientes:

```yaml id="t8w894"
if: ${{ needs.Validacion_migracion_test.outputs.should_apply_migrations == 'true' }}
```

---

## Inputs

| Input                                  | Requerido | Default    | Descripción                                                                                                                              |
| -------------------------------------- | --------: | ---------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `connection-string`                    |        Sí | -          | Connection string de SQL Server contra la cual se validan las migraciones.                                                               |
| `project`                              |        No | `""`       | Proyecto donde están las migraciones. Si se deja vacío, el action lo autodetecta.                                                        |
| `startup-project`                      |        Sí | -          | Proyecto startup usado por EF Core. Ejemplo: `./src/Api/Api.csproj`.                                                                     |
| `db-context`                           |        No | `""`       | Nombre del `DbContext`. Requerido si hay más de uno y EF Core no puede resolverlo automáticamente.                                       |
| `configuration`                        |        No | `Release`  | Configuración de build usada por `dotnet restore`, `dotnet build` y `dotnet-ef`.                                                         |
| `dotnet-version`                       |        No | `10.0.x`   | Versión de .NET SDK a instalar con `actions/setup-dotnet`. Si se deja vacío, no instala .NET.                                            |
| `ef-tool-version`                      |        No | `10.0.8`   | Versión de `dotnet-ef` que se instala temporalmente.                                                                                     |
| `validate-model-changes`               |        No | `true`     | Ejecuta `dotnet ef migrations has-pending-model-changes`.                                                                                |
| `fail-if-no-migrations`                |        No | `false`    | Si es `true`, falla cuando no se encuentran proyectos con migraciones.                                                                   |
| `environment-name`                     |        No | `UNKNOWN`  | Nombre del ambiente analizado. Ejemplo: `TEST`, `QA` o `PROD`.                                                                           |
| `fail-on-pending`                      |        No | `false`    | Si es `true`, falla cuando existen migraciones pendientes.                                                                               |
| `fail-on-risk-level`                   |        No | `""`       | Falla si el riesgo calculado es igual o superior al nivel indicado. Valores: `NONE`, `LOW`, `MEDIUM`, `HIGH`. Vacío no falla por riesgo. |
| `show-sql-in-logs`                     |        No | `true`     | Muestra el SQL generado en los logs de GitHub Actions.                                                                                   |
| `sql-log-max-lines`                    |        No | `500`      | Cantidad máxima de líneas SQL a mostrar en logs. Usar `0` para mostrar todo.                                                             |
| `impact-medium-row-threshold`          |        No | `1000000`  | Cantidad de registros potencialmente impactados desde la cual una migración eleva riesgo a `MEDIUM`.                                     |
| `impact-high-row-threshold`            |        No | `10000000` | Cantidad de registros potencialmente impactados desde la cual una migración eleva riesgo a `HIGH`.                                       |
| `impact-medium-size-mb-threshold`      |        No | `10240`    | Tamaño reservado en MB desde el cual una tabla afectada eleva riesgo a `MEDIUM`.                                                         |
| `impact-high-size-mb-threshold`        |        No | `51200`    | Tamaño reservado en MB desde el cual una tabla afectada eleva riesgo a `HIGH`.                                                           |
| `skip-impact-analysis-on-sqlcmd-error` |        No | `false`    | Si es `true`, salta el análisis de impacto cuando `sqlcmd` no está disponible o falla. Si es `false`, eleva el riesgo a `MEDIUM`.        |
| `sqlcmd-retry-count`                   |        No | `3`        | Cantidad de reintentos al ejecutar `sqlcmd`. Mínimo `1`.                                                                                 |
| `sqlcmd-retry-delay-seconds`           |        No | `2`        | Segundos de espera entre reintentos de `sqlcmd`.                                                                                         |

---

## Outputs

| Output                      | Descripción                                                                               |
| --------------------------- | ----------------------------------------------------------------------------------------- |
| `has-pending-db-migrations` | Indica si la base tiene migraciones pendientes.                                           |
| `pending-migrations`        | Lista de migraciones pendientes detectadas.                                               |
| `migration-risk`            | Riesgo calculado de las migraciones pendientes. Valores: `NONE`, `LOW`, `MEDIUM`, `HIGH`. |
| `requires-dba`              | Indica si requiere revisión DBA. Es `true` para riesgos `MEDIUM` o `HIGH`.                |
| `analysis-dir`              | Carpeta donde se generaron el SQL y los archivos de análisis.                             |
| `sql-files`                 | Lista de archivos SQL generados.                                                          |
| `sqlcmd-available`          | Indica si `sqlcmd` está disponible en el runner.                                          |

Ejemplo de uso de outputs:

```yaml id="91khtb"
- name: Mostrar resultado de validación
  run: |
    echo "Tiene migraciones pendientes: ${{ steps.validate_migrations_test.outputs.has-pending-db-migrations }}"
    echo "Riesgo: ${{ steps.validate_migrations_test.outputs.migration-risk }}"
    echo "Requiere DBA: ${{ steps.validate_migrations_test.outputs.requires-dba }}"
    echo "Directorio de análisis: ${{ steps.validate_migrations_test.outputs.analysis-dir }}"
    echo "SQL generado:"
    echo "${{ steps.validate_migrations_test.outputs.sql-files }}"
```

---

## Detección de proyectos con migraciones

Si no se informa el input `project`, el action busca automáticamente proyectos con migraciones dentro del repositorio.

La detección se realiza buscando archivos:

```text id="ukxfiv"
*/Migrations/*.cs
```

Excluye:

```text id="ki9mvw"
bin/
obj/
*.Designer.cs
*ModelSnapshot.cs
```

Luego busca el `.csproj` más cercano que contenga:

```xml id="jxzzvl"
<Project Sdk=
```

Ejemplo de proyecto detectado:

```text id="byv6so"
./src/Infrastructure/Infrastructure.csproj
```

Si se quiere validar un proyecto específico, se puede informar:

```yaml id="g1w41d"
with:
  project: ./src/Infrastructure/Infrastructure.csproj
```

---

## Startup project

El input `startup-project` es obligatorio.

Debe apuntar al proyecto que EF Core usa para levantar configuración, dependencias y connection string.

Ejemplo:

```yaml id="no411h"
startup-project: ./src/Api/LuanaApi.csproj
```

El action falla si este valor no se informa.

---

## Variables de conexión configuradas

El action recibe la connection string por input:

```yaml id="bggssn"
connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
```

Luego la enmascara en logs y la exporta como:

```bash id="9l400n"
DB_CONNECTION
DataAccessRegistry__TransactionalConnection
DataAccessRegistry__ReadOnlyConnection
```

Esto permite que EF Core y la aplicación resuelvan la conexión durante la validación.

---

## Validación de migraciones pendientes

El action ejecuta:

```bash id="o0lra0"
dotnet ef migrations list
```

Con los siguientes argumentos:

```bash id="exf80h"
--project <migration-project>
--startup-project <startup-project>
--configuration <configuration>
--no-build
--connection <connection-string>
```

Si se informa `db-context`, también agrega:

```bash id="r477kt"
--context <db-context>
```

El action considera migraciones pendientes aquellas que EF Core devuelve con:

```text id="g4p5kh"
(Pending)
```

---

## Generación de SQL

Cuando existen migraciones pendientes, el action genera un SQL idempotente con:

```bash id="3w35wd"
dotnet ef migrations script <last-applied> <latest-pending> --idempotent
```

El archivo se genera en:

```text id="u37fxv"
artifacts/ef-migrations/<ENVIRONMENT_NAME>
```

Ejemplo:

```text id="mhtyhw"
artifacts/ef-migrations/TEST/src_Infrastructure_Infrastructure.csproj_TEST_pending.sql
```

Además, el action puede mostrar el SQL en los logs.

Por defecto muestra hasta 500 líneas:

```yaml id="h1lyxh"
show-sql-in-logs: "true"
sql-log-max-lines: "500"
```

Para mostrar todo el SQL:

```yaml id="dvcc4m"
sql-log-max-lines: "0"
```

Para no mostrar SQL en logs:

```yaml id="qk2v74"
show-sql-in-logs: "false"
```

---

## Clasificación de riesgo

El action clasifica las migraciones pendientes según el contenido del SQL generado.

### Riesgo `NONE`

Se usa cuando no hay migraciones pendientes.

```text id="jaflnh"
migration-risk=NONE
requires-dba=false
```

---

### Riesgo `LOW`

Operaciones consideradas de bajo riesgo:

| Operación detectada                     | Riesgo |
| --------------------------------------- | ------ |
| `CREATE TABLE`                          | `LOW`  |
| Agregado de columnas                    | `LOW`  |
| Sin operaciones destructivas detectadas | `LOW`  |

---

### Riesgo `MEDIUM`

Operaciones consideradas de riesgo medio:

| Operación detectada                     | Riesgo   |
| --------------------------------------- | -------- |
| `DROP CONSTRAINT`                       | `MEDIUM` |
| `ADD CONSTRAINT`                        | `MEDIUM` |
| `FOREIGN KEY`                           | `MEDIUM` |
| `UNIQUE`                                | `MEDIUM` |
| `CREATE INDEX`                          | `MEDIUM` |
| `UPDATE` de datos                       | `MEDIUM` |
| Agregado o cambio de columna `NOT NULL` | `MEDIUM` |

También puede elevar a `MEDIUM` si el análisis de impacto detecta:

| Condición                                     |        Default |
| --------------------------------------------- | -------------: |
| Tabla con registros potencialmente impactados | `>= 1.000.000` |
| Tabla con tamaño reservado                    | `>= 10.240 MB` |
| Tabla con 10 o más índices                    |        `>= 10` |
| Tabla con FKs o triggers                      |          `> 0` |

Además, si `sqlcmd` no está disponible o falla y `skip-impact-analysis-on-sqlcmd-error` está en `false`, el riesgo se eleva a `MEDIUM`.

---

### Riesgo `HIGH`

Operaciones consideradas de alto riesgo:

| Operación detectada       | Riesgo |
| ------------------------- | ------ |
| `CREATE TRIGGER`          | `HIGH` |
| `ALTER TRIGGER`           | `HIGH` |
| `CREATE OR ALTER TRIGGER` | `HIGH` |
| `DROP TRIGGER`            | `HIGH` |
| `DROP TABLE`              | `HIGH` |
| `DROP COLUMN`             | `HIGH` |
| `TRUNCATE TABLE`          | `HIGH` |
| `DELETE FROM`             | `HIGH` |
| `ALTER COLUMN`            | `HIGH` |
| Rename de tabla o columna | `HIGH` |

También puede elevar a `HIGH` si el análisis de impacto detecta:

| Condición                                     |         Default |
| --------------------------------------------- | --------------: |
| Tabla con registros potencialmente impactados | `>= 10.000.000` |
| Tabla con tamaño reservado                    |  `>= 51.200 MB` |

---

## Revisión DBA

El output `requires-dba` se calcula automáticamente según el riesgo total.

| Riesgo   | Requiere DBA |
| -------- | ------------ |
| `NONE`   | `false`      |
| `LOW`    | `false`      |
| `MEDIUM` | `true`       |
| `HIGH`   | `true`       |

Ejemplo para usarlo en un job posterior:

```yaml id="dafq19"
if: ${{ needs.Validacion_migracion_test.outputs.requires_dba == 'true' }}
```

---

## Análisis de impacto con `sqlcmd`

Si `sqlcmd` está disponible en el runner, el action consulta metadata real de SQL Server para las tablas afectadas.

Analiza:

* Registros potencialmente impactados.
* Tamaño reservado en MB.
* Cantidad de índices.
* Cantidad de foreign keys.
* Cantidad de triggers.

Para eso genera una query contra vistas del sistema como:

```text id="bnq5k9"
sys.dm_db_partition_stats
sys.indexes
sys.foreign_keys
sys.triggers
```

El resultado se guarda como archivo Markdown dentro del directorio de análisis.

Ejemplo:

```text id="yud84n"
artifacts/ef-migrations/TEST/src_Infrastructure_Infrastructure.csproj_TEST_impact.md
```

---

## Connection string compatible con `sqlcmd`

Para el análisis de impacto, el action intenta parsear la connection string y extraer:

| Campo    | Keys soportadas                                               |
| -------- | ------------------------------------------------------------- |
| Server   | `server`, `data source`, `address`, `addr`, `network address` |
| Database | `database`, `initial catalog`                                 |
| User     | `user id`, `uid`, `user`, `username`                          |
| Password | `password`, `pwd`                                             |

Si no puede parsearla y `skip-impact-analysis-on-sqlcmd-error` es `false`, eleva el riesgo a `MEDIUM`.

Si se configura:

```yaml id="z63b53"
skip-impact-analysis-on-sqlcmd-error: "true"
```

el action continúa sin elevar el riesgo por este motivo.

---

## Validación de cambios de modelo

Por defecto, el action ejecuta:

```bash id="q5sh9g"
dotnet ef migrations has-pending-model-changes
```

Esto valida si hay cambios en el modelo que todavía no tienen una migración generada.

Está habilitado por default:

```yaml id="c7h4q5"
validate-model-changes: "true"
```

Para deshabilitarlo:

```yaml id="fmyiiy"
validate-model-changes: "false"
```

Si EF Core detecta cambios pendientes en el modelo, el action falla.

---

## Fallar por migraciones pendientes

Por default, el action no falla si encuentra migraciones pendientes.

Esto permite usarlo como step de validación y luego decidir si se aplican migraciones o si se pide aprobación DBA.

```yaml id="lnxdkn"
fail-on-pending: "false"
```

Si se quiere que falle cuando existan migraciones pendientes:

```yaml id="ygb6u4"
fail-on-pending: "true"
```

---

## Fallar por nivel de riesgo

El input `fail-on-risk-level` permite cortar el workflow cuando el riesgo calculado es igual o superior al nivel indicado.

Ejemplo: fallar si el riesgo es `MEDIUM` o `HIGH`:

```yaml id="99k8qg"
fail-on-risk-level: "MEDIUM"
```

Ejemplo: fallar solo si el riesgo es `HIGH`:

```yaml id="b4lbsg"
fail-on-risk-level: "HIGH"
```

Por default está vacío, por lo que no falla por riesgo:

```yaml id="zfe481"
fail-on-risk-level: ""
```

---

## Archivos generados

El action genera los archivos dentro de:

```text id="w4ly16"
artifacts/ef-migrations/<ENVIRONMENT_NAME>
```

Puede generar:

| Archivo                     | Descripción                                            |
| --------------------------- | ------------------------------------------------------ |
| `migration-risk-summary.md` | Resumen general del análisis.                          |
| `*_pending.sql`             | SQL idempotente generado para migraciones pendientes.  |
| `*_findings.md`             | Hallazgos usados para clasificar el riesgo.            |
| `*_affected_tables.txt`     | Tablas afectadas detectadas desde el SQL.              |
| `*_impact_query.sql`        | Query usada para calcular impacto con `sqlcmd`.        |
| `*_impact.csv`              | Resultado crudo del análisis de impacto.               |
| `*_impact.md`               | Resultado del análisis de impacto en formato Markdown. |

---

## Publicar archivos como artifact

El action genera los archivos de análisis, pero no los sube automáticamente como artifact.

Para conservarlos en GitHub Actions:

```yaml id="up4uvu"
- name: Publicar análisis de migraciones
  if: ${{ steps.validate_migrations_test.outputs.analysis-dir != '' }}
  uses: actions/upload-artifact@v4
  with:
    name: ef-migrations-analysis-${{ vars.ENVIRONMENT_NAME }}
    path: ${{ steps.validate_migrations_test.outputs.analysis-dir }}
```

---

## Resumen en GitHub Actions

El action escribe un resumen en `GITHUB_STEP_SUMMARY`.

El resumen incluye:

* Ambiente.
* Startup project.
* Proyecto de migraciones analizado.
* Migraciones pendientes.
* SQL generado.
* Hallazgos de riesgo.
* Impacto sobre tablas afectadas.
* Riesgo total.
* Si requiere DBA.

---

## Resultados posibles

### Sin proyectos de migraciones

```text id="rn8pm6"
has-pending-db-migrations=false
migration-risk=NONE
requires-dba=false
```

Si `fail-if-no-migrations` está en `true`, el action falla.

---

### Sin migraciones pendientes

```text id="zqkn2s"
has-pending-db-migrations=false
migration-risk=NONE
requires-dba=false
```

---

### Migraciones pendientes de bajo riesgo

```text id="uf3wy4"
has-pending-db-migrations=true
migration-risk=LOW
requires-dba=false
```

---

### Migraciones pendientes de riesgo medio

```text id="x9gilb"
has-pending-db-migrations=true
migration-risk=MEDIUM
requires-dba=true
```

---

### Migraciones pendientes de alto riesgo

```text id="2242s0"
has-pending-db-migrations=true
migration-risk=HIGH
requires-dba=true
```

---

## Ejemplo completo de job

```yaml id="u9bvsu"
jobs:
  Validacion_migracion_test:
    name: Validación Migración TEST
    runs-on: ubuntu-latest

    outputs:
      should_apply_migrations: ${{ steps.validate_migrations_test.outputs.has-pending-db-migrations }}
      migration_risk: ${{ steps.validate_migrations_test.outputs.migration-risk }}
      requires_dba: ${{ steps.validate_migrations_test.outputs.requires-dba }}
      analysis_dir: ${{ steps.validate_migrations_test.outputs.analysis-dir }}
      sql_files: ${{ steps.validate_migrations_test.outputs.sql-files }}

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Obtener connection string
        id: get_connection_string
        uses: infrastructure-services/actions@get-keyvault-secret
        with:
          keyvault-name: ${{ secrets.KV_NAME_DBA }}
          client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
          client-secret: ${{ secrets.KV_SECRET_DBA }}
          tenant-id: ${{ secrets.KV_TENANT_DBA }}
          secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}

      - name: Validar migraciones SQL Server TEST
        id: validate_migrations_test
        uses: infrastructure-services/actions@validate-migrations
        with:
          connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
          startup-project: ${{ needs.Preparativos.outputs.startupProject }}
          environment-name: ${{ vars.ENVIRONMENT_NAME }}
          fail-on-pending: "false"
          fail-on-risk-level: ""
          show-sql-in-logs: "true"
          sql-log-max-lines: "500"

      - name: Publicar análisis de migraciones
        if: ${{ steps.validate_migrations_test.outputs.analysis-dir != '' }}
        uses: actions/upload-artifact@v4
        with:
          name: ef-migrations-analysis-${{ vars.ENVIRONMENT_NAME }}
          path: ${{ steps.validate_migrations_test.outputs.analysis-dir }}
```

---

## Ejemplo de integración con apply-db-migrations

Después de validar migraciones, se puede ejecutar el action de aplicación solamente si hay migraciones pendientes:

```yaml id="au33fg"
- name: Ejecutar Migraciones SQL Server TEST
  id: apply_migrations_test
  if: ${{ needs.Validacion_migracion_test.outputs.should_apply_migrations == 'true' }}
  uses: infrastructure-services/actions@apply-db-migrations
  with:
    migration-risk: ${{ needs.Validacion_migracion_test.outputs.migration_risk }}
    keyvault-name: ${{ secrets.KV_NAME_DBA }}
    client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
    client-secret: ${{ secrets.KV_SECRET_DBA }}
    tenant-id: ${{ secrets.KV_TENANT_DBA }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}
    startup-project: ${{ needs.Preparativos.outputs.startupProject }}
    environment-name: ${{ vars.ENVIRONMENT_NAME }}
    nuget-username: ${{ secrets.ARQUITECTURA_USER }}
    nuget-token: ${{ secrets.ARQUITECTURA_DEPLOY }}
```

---

## Requisitos del runner

El runner debe tener:

* `bash`
* Acceso al repositorio
* Acceso de red a SQL Server
* Permisos para restaurar paquetes NuGet
* Permisos para instalar tools locales de .NET
* `.NET SDK` compatible con `dotnet-version`

Opcionalmente, para análisis de impacto:

* `sqlcmd`

El action busca `sqlcmd` en:

```text id="tz70zc"
PATH
/opt/mssql-tools18/bin/sqlcmd
```

---

## Consideraciones importantes

* Este action no aplica migraciones; solamente las valida.
* La aplicación real debe hacerse con otro action o step posterior.
* El action no obtiene la connection string desde Key Vault por sí mismo; espera recibirla en el input `connection-string`.
* Si existen paquetes privados, el workflow debe configurar el acceso a NuGet antes de ejecutar este action.
* Si hay más de un `DbContext`, se recomienda informar `db-context`.
* `requires-dba=true` cuando el riesgo calculado es `MEDIUM` o `HIGH`.
* Si `sqlcmd` no está disponible y `skip-impact-analysis-on-sqlcmd-error=false`, el riesgo puede elevarse a `MEDIUM`.
* El SQL generado sirve como evidencia para revisión técnica o aprobación DBA.
