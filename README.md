## Database Release Qualification V1 — hardened engine

Action piloto, TEST-only y analyze-only para calificar releases SQL Server sin ejecutar SQL. Las fuentes EF y SQL convergen previamente en dos archivos provistos por el autor: `forward.sql` y `rollback.sql`.

### Flujo del engine

1. Verifica que discovery sea `CONSISTENT`.
2. Conserva exactamente los bytes de forward/rollback y calcula sus SHA256.
3. Parsea ambos scripts con `Microsoft.SqlServer.TransactSql.ScriptDom.TSql180Parser`.
4. Obtiene operaciones, targets y columnas desde el AST; los errores, targets ambiguos, SQL dinámico y statements no soportados degradan `analysisConfidence`.
5. Cruza forward contra PRE y conserva un screening preliminar de rollback contra PRE.
6. En rehearsal, después de capturar POST1, vuelve a analizar rollback contra POST1; ése es el análisis autoritativo para qualification.
7. Calcula por separado `forwardRisk`, `rollbackRisk`, riesgos de dependencia/operación POST1, `dataRisk` y `operationalRisk`; `finalRisk` es siempre el máximo observado.
8. Escribe un payload estable y una attestation específica del ambiente/run.

La action pública no recibe connection strings, no crea un adaptador de ejecución y no ejecuta rehearsal. Su resultado normal es `ANALYZED_NOT_REHEARSED`; una inconsistencia de discovery o confianza insuficiente queda bloqueada.

### Payload y attestations

El payload promovible no contiene ambiente:

```text
artifacts/db-release/<release-id>/payload/
  forward.sql
  rollback.sql
  forward.sha256
  rollback.sha256
  payload.json
```

Su identidad depende exclusivamente de metadata estable y de los hashes de ambos scripts. Un payload existente se verifica byte a byte y nunca se reescribe.

Cada qualification agrega evidencia separada:

```text
artifacts/db-release/<release-id>/attestations/<environment>/<attestation-id>/
  qualification-attestation.json
  dependency-analysis.json
  risk-analysis.json
  preliminary-dependency-analysis.json
  preliminary-risk-analysis.json
  post1-rollback-analysis.json
  pre-schema.json
  pre-schema.sha256
  post-schema.json
  post-schema.sha256
  schema-diff.json
```

Las attestations pueden variar entre TEST, QA y PROD sin cambiar la identidad del payload. Esta iteración no implementa promoción ni workflows QA/PROD.

### Schema versus datos

El fingerprint certifica exclusivamente estructura. Por eso el resultado separa:

- `schemaRollbackValidity`: `VALID`, `INVALID`, `NOT_TESTED`;
- `dataRollbackValidity`: `NOT_APPLICABLE`, `VALID`, `INVALID`, `UNVERIFIED`, `NOT_TESTED`;
- `rollbackCapability`: `FULL_REVERSIBLE`, `SCHEMA_ONLY`, `FORWARD_FIX_ONLY`, `RESTORE_REQUIRED`, `UNKNOWN`.

`PRE_SCHEMA == PRE2_SCHEMA` sólo puede validar la dimensión estructural. Cuando forward/rollback contiene DML o forward puede destruir datos, la ausencia de un `IDataRollbackValidationContract` produce `UNVERIFIED` y bloquea la certificación completa. La aprobación DBA no convierte un rollback inválido o no verificado en válido.

### Rehearsal abstracto

`RehearsalEngine` modela:

```text
PRE → analyze FORWARD/PRE → FORWARD → POST1 → analyze ROLLBACK/POST1
    → ROLLBACK → PRE2 → FORWARD exacto → POST2
```

pero sólo depende de `IRehearsalDatabase`. No existe implementación SQL real. El screening rollback/PRE nunca reemplaza el análisis rollback/POST1. Discovery bloqueado, PROD, análisis insuficiente o data rollback no verificable cortan antes del siguiente paso mutante aplicable.

Después de POST1, `RiskEngine` recalcula `rollbackDependencyRisk` y `rollbackOperationalRisk` usando objetos y métricas POST1. La attestation principal usa ese análisis y conserva el preliminar como evidencia separada. Un riesgo POST1 mayor eleva `finalRisk`; no altera los scripts ni la identidad del payload. Si PRE2 difiere de PRE, el rollback sigue siendo `INVALID/BLOCKED` y no se vuelve aprobable por ese riesgo.

### Canonical schema y cobertura

`SqlServerSchemaReader` está implementado exclusivamente con `SELECT` y modela:

- schemas y propietarios;
- tablas y temporalidad;
- columnas, tipos, nullability, identity y computed columns;
- defaults, PK/UQ, checks y FK;
- índices rowstore, keys, INCLUDE, filtros, locking/options, data space y estado disabled;
- triggers, views, schema binding y expression dependencies.

Las métricas de filas, tamaño reservado, índices, LOB, particiones y dependencias se usan para riesgo, pero no entran al fingerprint.

Las siguientes categorías se detectan explícitamente como cobertura parcial cuando existen, porque V1 no conserva todavía toda su semántica en el canonical model:

- sequences;
- user-defined types;
- synonyms;
- partition functions y partition schemes;
- data compression;
- metadata temporal extendida no representada por el modelo básico;
- columnstore, XML, spatial y otros índices especiales;
- indexed views;
- full-text indexes;
- estadísticas creadas manualmente;
- otros tipos persistentes no modelados.

Su presencia se publica en `unsupportedSchemaFeatures`, degrada `schemaCoverage` y puede llevar `analysisConfidence` a `PARTIAL` o `INSUFFICIENT` si la release toca el objeto afectado.

### Target preflight

`TargetRiskEngine` es un componente puro que combina:

```text
FINAL_TARGET_RISK = MAX(QUALIFIED_RELEASE_RISK, TARGET_PREFLIGHT_RISK)
```

El preflight futuro podrá recibir métricas propias de QA/PROD sin modificar el payload ni el schema fingerprint. En esta iteración no se conecta a esos ambientes.

### Límites del AST

ScriptDom aporta parsing sintáctico real, pero no resuelve por sí solo semántica runtime, SQL dinámico, efectos internos de procedures, nombres dependientes de default schema, objetos cross-database ni toda resolución de aliases/CTE complejos. Esos casos nunca quedan automáticamente `COMPLETE/LOW`: se degradan o bloquean.

### Validación local

```bash
dotnet restore tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj \
  --configfile tools/DatabaseReleaseQualification/NuGet.Config
dotnet run --project tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj
bash tests/test-database-release-static.sh
```
