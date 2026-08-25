## Discover DB Scenario

Composite action de discovery TEST que combina evidencia estática del repositorio con metadata de SQL Server para inferir `NEW_EF`, `EXISTING_EF` o `EXISTING_SQL`.

El usuario declara únicamente `database-lifecycle=NEW|EXISTING`. La action no ejecuta código de la aplicación, comandos `dotnet-ef`, scripts SQL ni operaciones de deployment.

### Discovery estático EF

La detección usa, en orden:

- atributos `[Migration("...")]` presentes en fuentes generadas;
- pares `Migration.cs` / `Migration.Designer.cs` con ID válido;
- `ModelSnapshot` como evidencia EF adicional.

Los IDs aceptados usan el formato determinista `yyyyMMddHHmmss_Name` y se ordenan lexicográficamente. Un snapshot, atributo o archivo relacionado con migrations que no permita construir una lista válida produce `BLOCKED_EF_REPOSITORY_INCONSISTENT`; nunca se interpreta como `EXISTING_SQL`.

Si la evidencia pertenece a más de un `.csproj`, devuelve `BLOCKED_AMBIGUOUS_MIGRATION_PROJECT`. Este incremento no crea ni inspecciona `DbContext`.

### Seguridad y permisos SQL

El helper solicita routing `ReadWrite` para inspeccionar el primary autoritativo y ejecuta únicamente `SELECT`. `ApplicationIntent` no se usa como barrera de seguridad.

Permisos mínimos efectivos sobre la base inspeccionada:

- acceso de login y `CONNECT` a la base;
- `VIEW DEFINITION` sobre la base;
- `SELECT` sobre `dbo.__EFMigrationsHistory`, si existe.

La action comprueba visibilidad de metadata antes de interpretar ausencia de objetos o history. Visibilidad insuficiente produce `FAIL_METADATA_VISIBILITY`.

Una base NEW solo se considera vacía cuando no encuentra estructura de usuario fuera de la allowlist técnica. Se inspeccionan todos los objetos visibles de `sys.objects`, schemas de usuario, tablas, tipos definidos por usuario, assemblies, DDL triggers, XML schema collections, partition functions/schemes y full-text catalogs.

Exclusiones técnicas:

- schemas `sys`, `INFORMATION_SCHEMA` y `cicd`;
- schemas asociados a roles fijos de base;
- `dbo.__EFMigrationsHistory` y sus objetos hijos;
- objetos con `is_ms_shipped=1`.

### Estados

- `NEW_EF`
- `EXISTING_EF`
- `EXISTING_SQL`
- `BLOCKED_BASELINE_REQUIRED`
- `BLOCKED_HISTORY_WITHOUT_REPO`
- `BLOCKED_EF_SEQUENCE_DIVERGED`
- `BLOCKED_NEW_WITHOUT_MIGRATIONS`
- `BLOCKED_NEW_NOT_EMPTY`
- `BLOCKED_AMBIGUOUS_MIGRATION_PROJECT`
- `BLOCKED_EF_REPOSITORY_INCONSISTENT`
- `FAIL_SECRET_REQUIRED`
- `FAIL_DATABASE_UNREACHABLE`
- `FAIL_METADATA_VISIBILITY`
- `FAIL_SQL_DISCOVERY_HELPER`

### Outputs y artifacts

Los outputs son compactos: escenario, status/reason, contadores, indicador de base vacía, primera/última evidencia relevante, SHA256 de las dos listas y proyecto seleccionado.

Las listas completas quedan únicamente en el artifact:

- `repo-migrations.txt`
- `db-migrations.txt`
- `discovery.json`
- `summary.md`

No se publican stdout crudos de herramientas ni de la aplicación.

### Pruebas locales

```bash
bash tests/test-classification.sh
bash tests/test-repository-discovery.sh
bash tests/test-sql-read-only.sh
```
