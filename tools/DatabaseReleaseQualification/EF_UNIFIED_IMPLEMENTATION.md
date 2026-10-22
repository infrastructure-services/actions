# EF unificado: incremento de bootstrap preprovisionado

Fecha: 2026-10-22. Incremento local: **CODE_INCREMENT_READY**; propuestas de PR
preparadas y publicación pendiente de autorización. Workstream global: **CODE_INCOMPLETE**.
No usar `EF_UNIFIED_CODE_COMPLETE_READY_FOR_INTERNAL_VALIDATION` ni
`CODE_IMPLEMENTED_AWAITING_EXTERNAL_TRUST`: todavía falta código además de confianza externa.

## Baseline verificado

| Repositorio | Estado inicial / base de trabajo | Rama del incremento |
| --- | --- | --- |
| Actions | Inicial limpio en `a6dfa37369a57f30ef0ebead4dd26575bad285c5`, rama `codex/a2-3-discovery-identity-errors`; origin/main local atrasado en `f51e0e80b668b0cb84810a8b9a302d95b0a2aa2a`. Tras fetch autorizado, base integrada `fbb48a6cf482270fc35dff0f872e229fad90647b`; árbol idéntico al head inicial. | `codex/ef-unified-preprovisioned` |
| Workflow | Limpio, main y origin/main local `b81d71783b216ad77e7df5bad4825139e7555e30`. Sin fetch de Workflow. | `codex/ef-unified-discovery-repin` |
| Piloto, sólo lectura | main y origin/main local `0a01d61f81af8e142c49308c6a6abd67ec6d4936`, limpio | Sin cambios |
| Luana, sólo lectura | main y origin/main local `2bd491451664904994e52133bfc954134443146b`, limpio | Sin cambios |

[Actions PR #40](https://github.com/infrastructure-services/actions/pull/40) está
integrado como `fbb48a6cf482270fc35dff0f872e229fad90647b`. El fetch fue autorizado
expresamente durante esta tarea. No se hicieron commits, push, PR ni merge.

## Problema y consumidor real

`EfEvidencePreparationV1.PrepareAsync` leía los bytes del productor autorizado
`NEW_BOOTSTRAP`, pero no interpretaba su contenido. Una referencia opaca satisfacía
ese paso. Ahora deserializa estrictamente `EfPreprovisionedBootstrapV1` y valida su
contenido antes de analizar SQL o leer qualification.

El consumidor sigue siendo:

`ef-test-migration-controlled.yml (PLAN)` → `ef-release-plan/action.yml` →
`run-ef-release-plan.mjs` → `prepare-ef-release` → `EfOperationalHostV1` →
`EfEvidencePreparationV1` → `EfReleasePlannerV1`.

La nueva validación está conectada al servicio de Actions; **el pin corporativo de
PLAN todavía no consume este cambio local**. Se mantiene en
`f51e0e80b668b0cb84810a8b9a302d95b0a2aa2a` hasta publicación autorizada e integración
humana. El host productivo continúa devolviendo provider nulo y
`EXECUTED_HOST_ATTESTATION_REQUIRED`; ningún test instala un proveedor productivo.

El orden prioriza la frontera que necesitará V3: antes de ampliar el clasificador
para admitir historia EMPTY se necesita evidencia de bootstrap verificable.
No se cambió el clasificador ni se saltó su bloqueo actual.

## Contrato implementado

`Version=1`, `Kind=EF_PREPROVISIONED_BOOTSTRAP`. El rol del host `NEW_BOOTSTRAP`
debe estar autorizado independientemente por propósito, target, source y sesión.
La autoridad no se obtiene de un campo del documento. Los receipts del proveedor,
sus revisiones y los hashes quedan en Acquisition y en el hash del conjunto de evidencia.

El documento vincula:

- Scope exacto: target, repositorio/SHA de source y sesión.
- Referencias exactas a gobierno, onboarding, política, source y documento PRE.
- Contrato versionado de proyecciones de datos/seguridad.
- Estado PRE completo: target, ambiente, endpoint, servidor, base, incarnation,
  historia ordenada, hashes de schema/datos/seguridad, capture y fecha.
- Vigencia limitada por la política gobernada; PRE dentro de esa ventana.
- Taxonomía física completa, cero objetos de negocio y cero objetos técnicos inesperados.
- Cobertura de datos explícita COMPLETE y contador nullable de filas de negocio igual
  a cero. Ausencia del contador no equivale a cero. Historia EMPTY requiere tabla
  `dbo.__EFMigrationsHistory`, métrica de cero filas y sólo hijos de tipos admitidos.

`HISTORY_ABSENT_V1` y `EMPTY_HISTORY_ATTESTED_V1` son políticas distintas. El
productor acreditado debe justificar la procedencia del estado EMPTY; el string
por sí solo no acredita nada. Un recibo CREATE histórico, documento arbitrario,
política desconocida, binding distinto o evidencia vencida bloquean.

EXISTING_EF rechaza bootstrap inesperado y conserva sus requisitos de baseline.
No se cambió `EfReleasePlannerV1.Build`, que sigue siendo una API pura de consistencia,
sin autoridad para ejecutar. La vía operacional utiliza adquisición autenticada.

## Recuperación y CREATE

No hay llamada de creación ni dependencia de creation receipt en este contrato.
El CLI histórico `create-new-ef-database` sigue existiendo en `Program.cs`; no se
borró ni modificó. No es llamado por la ruta PLAN corporativa inspeccionada.
Esto no afirma que todos los entrypoints históricos hayan sido retirados.

La equivalencia de recuperación ABSENT→EMPTY **no está implementada**. Las
comparaciones generales PRE/PRE2 siguen estrictas y los tests nuevos comprueban el
bloqueo de cambios de estado de historia. Falta una política de equivalencia
específica que preserve schema, datos, seguridad e identidad y se conecte al motor
de rehearsal existente. No se falsean los hashes para hacer pasar la recuperación.

## Repin independiente en Workflow

Sólo `sql-discovery-v2` en `database-discovery-v2-test.yml` cambia de
`af83061c98a26263c8684e044ee81a535dde5896` al merge integrado #40. Su provenance cambia
al mismo SHA. El hash del manifest no cambia:
`0f78e36728fea1cc737bd690b5f13fb8ee8259b98e760389a79e86e3c150b93d`.

Schema Capture, Repository Discovery, clasificación, bootstrap histórico y motor
PLAN mantienen sus revisiones. El guard del DAG permite exclusivamente esos dos
reemplazos y exige igualdad exacta del resto contra el baseline.

## SCOPE_CONTROL

Rutas de Actions relativas a su raíz; las de Workflow se indican explícitamente.

| Archivo | Motivo / consumidor EF | Alcance / regresión |
| --- | --- | --- |
| `tools/DatabaseReleaseQualification/EfPreprovisionedBootstrap.cs` | Contrato y validación consumidos por A2.1 | Bootstrap EF; aditivo |
| `tools/DatabaseReleaseQualification/EfEvidencePreparation.cs` | Interpretar el documento adquirido | EF; evidencia opaca anterior deja de admitirse |
| `tests/DatabaseReleaseQualification.Tests/EfBootstrapTests.cs` | Casos positivos y adversariales mediante el servicio real y provider falso | Tests EF |
| `tests/DatabaseReleaseQualification.Tests/EfPreparationTests.cs` | Publicar bootstrap tipado en fixtures NEW | Tests EF |
| `tests/DatabaseReleaseQualification.Tests/EfReleasePlanTests.cs` | Corregir fixture inicial: historia vacía tiene cero filas | Sólo fixture NEW |
| `tests/DatabaseReleaseQualification.Tests/Program.cs` | Selectores dirigidos; excluir continuidad multi-release del comando dirigido | Registro de tests EF |
| `tests/test-ef-release-plan-static.sh` | Incluir el nuevo contrato en la prohibición estática de I/O/writes | Guard EF |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_IMPLEMENTATION.md` | Estado, alcance y propuesta de revisión | Documentación EF |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_INTERNAL_VALIDATION_PLAN.md` | Checks ejecutables y pendientes por nivel | Documentación EF |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_TEST_INFRA_REQUIREMENTS.md` | Dependencias externas concretas | Documentación EF |
| Workflow: `.github/workflows/database-discovery-v2-test.yml` | Consumir corrección integrada de adquisición de identidad | Discovery consumido por EF; sin cambio de permisos |
| Workflow: `.github/workflows/tests/test-database-discovery-v2-static.sh` | Afirmar pin/provenance correctos | Guard local |
| Workflow: `.github/workflows/tests/test-ef-plan-dag.py` | Allowlist exacta del repin en comparación histórica | Guard local |

Sin eliminaciones. Sin cambios en LEGACY, ejecutor TRADITIONAL, Registry,
Onboarding, proyectos compartidos, paquetes, Luana o piloto.
Control final: **OUT_OF_SCOPE_MODIFICATIONS = 0**. Trece archivos del incremento:
diez en Actions (cinco nuevos) y tres en Workflow; sin eliminaciones.

## VARIANT_MATRIX

| Variante | Estado real de implementación | Tests preparados / ejecutados en este incremento | Resultado |
| --- | --- | --- | --- |
| V1 ausente | Discovery conserva ausencia confirmada; router funcional con reason solicitado pendiente | Guards Discovery; no nuevo test V1 de extremo a extremo | No declarar completa |
| V2 ABSENT | Admisión de bootstrap tipado implementada; recuperación inicial completa pendiente | Contrato de admisión ejecutado, sin qualification inventada | Bloqueo posterior correcto |
| V3 EMPTY | Bootstrap acreditado por host soportado en A2.1; clasificación corporativa aún bloquea EMPTY | PLAN sintético con 1/3 migraciones y negativas ejecutados | Sin autoridad de ejecución |
| V4 pendientes | A1/A2.1 existentes con baseline, prefijo, SQL exacto y recovery | Regresión dirigida ejecutada | PASS de lógica |
| V5 NO_CHANGES | A2.1 existente; sin SQL ni PLAN ficticio | Regresión dirigida ejecutada | PASS de lógica |
| V6 inválido | Gates existentes más validaciones de bootstrap | Replay, sustituciones, cobertura, expiración, rol y cancelación ejecutados | BLOCKED esperado |

No existe aún una matriz interna integral runnable para las seis variantes.
Las suites de este incremento no demuestran runtime ni certificación real.

## REUSED / SECURITY / REGRESSION

Se reutilizan A1, A2.1, el registro de proveedores del host, `Observe`, taxonomía
`EmptyForNewEfV1`, hashes, parser estricto, receipt de productor, SQL semantic
analysis, recovery, NO_CHANGES, revalidación y POSTCHECK. No hay router ni ejecutor
duplicado. Se conservan `ExecutionAuthorized=false`, `Certified=false` en host,
provider productivo bloqueado y separación PLAN/APPLY.

## REMAINING_IMPLEMENTATION

1. Extender el clasificador corporativo y sus contratos/adapters para V1–V6,
   transportando evidencia de bootstrap sin convertir un flag del caller en autoridad.
2. Implementar la política versionada y el adapter de recuperación ABSENT→EMPTY;
   verificar diferencias exactas, no exclusiones globales.
3. Completar normalización real de schema/datos/seguridad/bootstrap/baseline hacia A2.1
   hasta las fronteras acreditables y el binding de freshness/incarnation del baseline.
4. Completar los contratos de aprobación independiente, APPLY exact-byte inactivo,
   incertidumbre sin retry y transición gobernada NEW→EXISTING.
5. Preparar fixtures ejecutables de continuidad integral y conectar consumers reales
   a cada nuevo SHA integrado; conservar los gates externos.

Estas tareas son código pendiente, no sólo acreditación corporativa. Este incremento
no cierra los cinco incrementos del mandato. La implementación del proveedor
productivo además requiere el contrato externo descrito en el documento de infraestructura.

## Propuestas locales de PR (no creadas)

Actions — `fix(ef): validate preprovisioned bootstrap evidence before PLAN`

> A2.1 accepted opaque authorized NEW_BOOTSTRAP bytes without validating their
> contents. It now requires a versioned preprovisioned document bound to governance,
> policy, source, session and the complete PRE state, with explicit ABSENT/EMPTY
> policy, freshness and empty-data evidence. Invalid bootstrap stops before SQL
> analysis/qualification. Production provider registration remains blocked.
>
> Validation: Release build without restore; directed bootstrap, A2.1, A1 and
> operational-host tests; static no-I/O/no-authority guards. No SQL or workflow run.

Workflow — `fix(ef): consume integrated SQL discovery identity error handling`

> Pin SQL Discovery V2 and its provenance to integrated Actions #40. Preserve other
> producer/engine revisions and workflow behavior. Guards allow only this exact
> two-location repin while retaining the historical DAG/execution boundary checks.
>
> Validation: Discovery static guard, parsed YAML/DAG 152,064 cases and publication
> matrix 32 cases. No workflow dispatch or SQL.

## READINESS / NEXT_EXACT_STEP

Desarrollo: incremento local listo para revisión; workstream incompleto. Offline
dirigido: ver documento de validación. Integrales: pendientes y no ejecutadas.
SQL, acreditación corporativa, TEST, QA y PROD: no habilitados.

Publicación requiere autorización expresa; integración requiere merge humano.
Después, consumir el SHA integrado real al ampliar la clasificación corporativa.
No sustituir el pin PLAN por una rama o un SHA local sin integrar.
