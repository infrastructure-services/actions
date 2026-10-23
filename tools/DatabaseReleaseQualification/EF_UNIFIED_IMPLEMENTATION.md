# EF unificado — implementación y límites reales

Fecha: 2026-10-22. Estado del desarrollo: **EF_UNIFIED_CODE_COMPLETE_PENDING_EXTERNAL_GATES**, con consumer preparado sólo en la rama local de Workflow codex/ef-unified-consumer-local. Estado operativo: **EXTERNAL_CONTRACT_BLOCKED**. No se declara `EF_UNIFIED_READY_FOR_INTERNAL_VALIDATION`.

## Checkpoint y Git

Se conservó el workspace y se continuó sobre `codex/ef-unified-completion` en ambos repositorios. Base Actions: `4c81ecc747d82aa5eacb49cbd02285da974ce590` (PR #41 integrado). Base Workflow: `f6fecdb63ca1e9aa5cf336d9a9ba9dbc17413fe3` (PR #45 integrado). No se repitió fetch en la reanudación. La publicación mediante commits, push y ambos PR está autorizada. El merge sigue siendo un hard gate humano; no se habilita ejecución.

Workflow cambia exclusivamente el pin de PLAN y sus comprobaciones al merge de Actions #41. El pin de SQL Discovery sigue siendo el merge #40. Ningún pin apunta a esta rama local ni a un SHA ficticio. Los ejecutores históricos TRADITIONAL permanecen iguales al baseline comparado por el guard del DAG.

## Matriz A–I

| Bloque | Código local y consumidor | Validación dirigida | Límite / pendiente |
| --- | --- | --- | --- |
| A | Classifier V2 y adapter extienden opcionalmente rulesVersion 2; A2.1 consume resultado tipado, vinculado a PRE/source/bootstrap/sesión. | 12 casos Node V1–V6; 2 grupos .NET; V2 también atraviesa recuperación y PLAN. | RulesVersion 1 conserva compatibilidad. El compositor corporativo actual conserva rulesVersion 1; el productor corporativo debe adoptar y acreditar el envelope nuevo después del gate de integración; Workflow todavía consume las revisiones integradas anteriores. |
| B | Bootstrap preprovisionado ABSENT/EMPTY en A2.1; EMPTY exige columnas y PK del contrato EF History. | 13 grupos bootstrap, uno/múltiples cambios iniciales. | Variantes de estructura distintas requieren otra política revisada. Sin CREATE. |
| C | Entrada optativa NEW_EF en RehearsalEngine; RecoveryCoverage, A1 y A2.1 verifican la misma política y las cuatro fases. | 5 grupos iniciales más 42 de cobertura. | Observer y productores reales no acreditados; no se ensayó SQL. |
| D | A1/A2.1 conservan prefijo ordenado, delta, baseline, drift y NO_CHANGES; continuidad usa el store existente. | A1 14, A2.1 13, baseline 3 grupos. | La transición de gobierno NEW→EXISTING se representa explícitamente en fixtures; no modifica Registry. |
| E | PreSources opcional conecta Discovery V2, Schema Capture y proyecciones autorizadas con A2.1 y el host. | 2 grupos normalización; 11 grupos host. | No existe verifier corporativo de productores. Workflow no admite todavía selectores que su pin integrado no entiende. |
| F | PLAN inmutable conserva payload, PRE, POST esperado, riesgo, impacto, recuperación y receipts; análisis/motor existentes. | A1/A2.1 y recuperación dirigida. | Evidencia offline no equivale a qualification física acreditada. |
| G | ApplyProtectedAsync coordina autorización, revalidación, claim único y el contrato existente IRehearsalDatabase.ExecuteSqlAsync. | 5 grupos: exact bytes, concurrencia, POSTCHECK, negativos, incertidumbre. | Sin host productivo registrado, SQL adapter corporativo ni journal durable aprobado. CLI y workflows no exponen esta operación. |
| H | POSTCHECK→CertificationDecisionEngine→CertifiedStateRecordBuilder→CertificationCommitProtocol/store existentes. | 3 grupos: commit, idempotencia, segundo release, NO_CHANGES y negativos. | Persistencia sólo in-memory en tests; store externo y transición de Registry requieren autoridad separada. |
| I | Reusable existente conserva PLAN aislado y repin integrado #41. | 33 tests Node; 152.064 casos DAG; 32 casos publicación; guard estático. | Integración completa de contratos locales requiere primero publicación/revisión/merge de Actions y luego repin y adaptación del consumer de Workflow. |

## Matriz V1–V6

| Variante | Decisión | Evidencia / resultado offline |
| --- | --- | --- |
| V1 | BLOCKED / DATABASE_PROVISIONING_REQUIRED | Ausencia confirmada por lookup; errores de transporte, autenticación, timeout o cancelación no se convierten en ausencia. Normalización bloquea antes de PLAN. |
| V2 | NEW_EF / INITIAL_MIGRATIONS_PENDING | Bootstrap ABSENT, clasificación tipada, cuatro fases y PLAN simulado positivo. |
| V3 | NEW_EF / EMPTY_HISTORY_INITIAL_STATE | Bootstrap autorizado con estructura completa y cero filas; sin política válida: INITIAL_HISTORY_STATE_UNVERIFIED. |
| V4 | EXISTING_EF / PENDING_MIGRATIONS | Prefijo válido, baseline certificado, delta de una/múltiples migraciones y revalidación. |
| V5 | NO_CHANGES | Source/history/baseline coherentes; SQL vacío; sin PLAN ficticio ni write. |
| V6 | BLOCKED | Drift, duplicados, divergencias, captura parcial, bindings inconsistentes o contenido alterado. |

La clasificación sólo habilita etapas de lectura/preparación. Expone etapas permitidas y prohíbe APPLY, CERTIFY y CREATE_DATABASE. El parámetro puro del clasificador no concede autoridad: rulesVersion 2 se selecciona en gobierno adquirido y A2.1 verifica independientemente el bootstrap y las referencias utilizadas.

## Recuperación inicial: excepción exacta

La API original `QualifyAsync` conserva comparación estricta. `QualifyEfInitialAsync` exige NEW_EF/NEW/EF/TEST y política `NEW_EF_ABSENT_EMPTY_V1`, ligada a release, operation, scope, target aislado, incarnation, source, payload UP/DOWN, bootstrap y contrato de proyección. No se modificó globalmente SameState.

PRE, POST1, PRE2 y POST2 contienen observaciones independientes; A2.1 adquiere las capturas y seguridad bajo propósitos autorizados. Se verifican hashes, identidad, vigencia, cobertura, filas de History e inventario de seguridad. La equivalencia sustituye únicamente el securable de History de PRE2 por su ausencia realmente observada en PRE, después de comprobar estructura y seguridad exactas contra la política. Todos los demás principals, SID, roles/memberships, grants/denies, grantors, propietarios y objetos permanecen en la comparación. POST1/POST2 conserva igualdad estricta de schema, datos, seguridad e historial. El diff estructural bruto se conserva.

Pruebas negativas: GRANT modificado, DENY, propietario de schema/base, principal, membership, otro objeto, hash manipulado, evidencia faltante/parcial, incarnation diferente, replay y reaplicación divergente. La evidencia incompleta devuelve BLOCKED. No se duplicó rehearsal ni se editó RecoverySecurityEvidence.cs. Models.cs estaba autorizado pero no fue necesario modificarlo.

## Compatibilidad y productores

Los contratos opcionales nulos no se serializan; la ruta anterior mantiene sus requisitos. La validación EMPTY ahora rechaza capturas que sólo informaban el nombre de la tabla sin columnas/PK; ese rechazo es intencional y debe revisarse con los productores. El layout admitido es dbo.__EFMigrationsHistory, MigrationId nvarchar(150), ProductVersion nvarchar(32), ambas NOT NULL y PK(MigrationId); maxLength del catálogo se expresa en bytes.

Los nuevos roles son EF_INITIAL_RECOVERY_POLICY, EF_INITIAL_PRE/POST1/PRE2/POST2, SQL_DISCOVERY_V2, SCHEMA_CAPTURE, DATA_PROJECTION_RESULT, SECURITY_PROJECTION_RESULT, EF_APPLY_AUTHORIZATION, CERTIFICATION_COMMIT_AUTHORIZATION y CERTIFIED_EF_LINEAGE. Referencias, SHA y receipts prueban integridad/binding; por sí solos no acreditan identidad. No se registró ningún provider productivo.

## Ejecución protegida y certificación

El coordinador verifica PLAN, autorización independiente con riesgo/recuperación consistentes, source/PRE frescos, target/incarnation y bytes exactos. El host debe reclamar duraderamente la operación y comprobar el PRE físico bajo la frontera de escritura antes de devolver el contrato existente. La implementación de ese host no está incluida: faltan contratos corporativos de identidad, credenciales y persistencia. La prueba concurrente usa CAS in-memory y demuestra la lógica del contrato, no exclusión durable distribuida.

Hay una sola invocación de UP, sin retry ni DOWN automático. Timeout, cancelación, claim incierto o fallo del recibo producen incertidumbre y requieren reconciliación. El éxito necesita POSTCHECK independiente; no certifica. El método de commit requiere otra autorización y reutiliza el protocolo CAS/idempotente existente. El predecessor debe corresponder al registro certificado exacto, no sólo a un schema hash. La secuencia de dos releases y NO_CHANGES usa persistencia en memoria y transición de gobierno simulada explícitamente.

## Integración real y trabajo restante

Actions: clasificación/normalización/recuperación están conectadas con A2.1/A1; ejecución y commit son APIs host-only consumidas por pruebas de contrato. El host operacional sigue sin provider, executionHost ni certificationStore productivos. No se añadieron comandos APPLY o CERTIFY.

Workflow: el código ejecutable sigue PLAN-only para la ruta nueva, con Actions #41 integrado. La adaptación de selectores PreSources, gate gobernado rulesVersion 2 y DAG ya está preparada offline en codex/ef-unified-consumer-local. Después del merge resta colocar el SHA integrado en los lugares documentados y validar el checkout integrado. No debe publicarse un consumer con pin a una rama sin merge. La acreditación de los productores de esos artifacts es una dependencia externa adicional, no algo que resuelva un repin.

No quedan habilitaciones operativas implícitas. No se declara adapter SQL, journal durable, autenticación corporativa, Registry write ni qualification física implementados o acreditados. La validación interna integral está preparada, no ejecutada. Las pruebas SQL siguen prohibidas hasta autorización específica.

## Alcance y propuesta de revisión

Cambios compartidos productivos: únicamente QualificationEngine.cs y RecoveryCoverage.cs, bajo las autorizaciones expresas para EF. Classifier/adapter conservan su comportamiento predeterminado; los demás cambios productivos son EF. No se editó Luana, piloto, ejecutores LEGACY/TRADITIONAL ni infraestructura.

Alcance autorizado de publicación: un commit/PR de Actions para el incremento EF y sus tests/documentación; otro de Workflow exclusivamente para el repin al merge #41 y el guard asociado. La integración de los contratos nuevos de Actions en Workflow debe revisarse después del primer merge, en una propuesta dependiente con SHA real. Ningún merge automático.

La lista exacta de archivos y los resultados reproducibles se mantienen en el cierre de la tarea y en EF_UNIFIED_INTERNAL_VALIDATION_PLAN.md.

## Inventario exacto de cambios locales

Actions: 27 archivos respecto de la base del PR (incluye EfOperationalTests.cs en la revisión final), relativos a C:\Repos\actions.

| Archivo | Justificación |
| --- | --- |
| `scripts/adapt-classification-evidence-v2.mjs` | Adapter opcional rulesVersion 2; salida y etapas verificadas |
| `scripts/classify-scenario-v2.sh` | V1–V6 EF optativas; comportamiento anterior preservado |
| `tests/DatabaseReleaseQualification.Tests/EfBaselineCommitTests.cs` | Commit, idempotencia, continuidad y negativos |
| `tests/DatabaseReleaseQualification.Tests/EfBootstrapTests.cs` | Negativos de estructura EMPTY |
| `tests/DatabaseReleaseQualification.Tests/EfInitialRecoveryTests.cs` | Cuatro fases, PLAN y adversariales semánticos |
| `tests/DatabaseReleaseQualification.Tests/EfNormalizationTests.cs` | Productores raw y fallos de evidencia |
| `tests/DatabaseReleaseQualification.Tests/EfPreparationTests.cs` | Fixture con predecessor certificado exacto |
| `tests/DatabaseReleaseQualification.Tests/EfProtectedExecutionTests.cs` | Bytes, aprobación, concurrencia, incertidumbre y POSTCHECK |
| `tests/DatabaseReleaseQualification.Tests/EfReleasePlanTests.cs` | Fixture de History con columnas/PK completos |
| `tests/DatabaseReleaseQualification.Tests/EfUnifiedClassificationTests.cs` | Consumidor A2.1 con bindings de clasificación |
| `tests/DatabaseReleaseQualification.Tests/EfOperationalTests.cs` | Host de prueba para requests PreSources/rules2 consumidos por Workflow |
| `tests/DatabaseReleaseQualification.Tests/Program.cs` | Selección dirigida y preparación del agregador integral |
| `tests/test-ef-unified-classification.mjs` | Matriz V1–V6 contra el clasificador real |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_IMPLEMENTATION.md` | Estado, matrices, compatibilidad y alcance |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_INTERNAL_VALIDATION_PLAN.md` | Resultados y preparación de la etapa posterior |
| `tools/DatabaseReleaseQualification/EF_UNIFIED_TEST_INFRA_REQUIREMENTS.md` | Fronteras externas, sin habilitación operativa |
| `tools/DatabaseReleaseQualification/EfBaselineCommit.cs` | Integración del protocolo/store corporativos existentes |
| `tools/DatabaseReleaseQualification/EfEvidenceNormalization.cs` | Normalización tipada de productores existentes |
| `tools/DatabaseReleaseQualification/EfEvidencePreparation.cs` | Conexión de contratos con A2.1 y registro host-only |
| `tools/DatabaseReleaseQualification/EfInitialRecovery.cs` | Política, hashes, schema, inventario y fases EF |
| `tools/DatabaseReleaseQualification/EfOperationalHost.cs` | Validación de referencias opcionales PreSources |
| `tools/DatabaseReleaseQualification/EfPreprovisionedBootstrap.cs` | Estructura EF History completa y reutilizable |
| `tools/DatabaseReleaseQualification/EfProtectedExecution.cs` | Coordinación host-only sobre contrato exact-byte existente |
| `tools/DatabaseReleaseQualification/EfReleasePlan.cs` | Equivalencia inicial únicamente adquirida y verificada |
| `tools/DatabaseReleaseQualification/EfUnifiedClassification.cs` | Validación del resultado V2 contra inputs autorizados |
| `tools/DatabaseReleaseQualification/QualificationEngine.cs` | Excepción compartida autorizada: entrada exclusiva NEW_EF |
| `tools/DatabaseReleaseQualification/RecoveryCoverage.cs` | Excepción compartida autorizada: seguridad semántica y cuatro fases |

Workflow: 3 archivos, relativos a C:\Repos\workflow.

| Archivo | Justificación |
| --- | --- |
| `.github/workflows/ef-test-migration-controlled.yml` | Pin PLAN y comprobación HEAD al merge #41 integrado |
| `.github/workflows/scripts/ef-plan-route.mjs` | Mismo SHA admitido por el guard |
| `.github/workflows/tests/test-ef-plan-dag.py` | Pin esperado por la regresión |

Control final: OUT_OF_SCOPE_MODIFICATIONS = 0. Luana y piloto conservan working trees limpios. No se modificaron Models.cs, RecoverySecurityEvidence.cs ni ejecutores LEGACY/TRADITIONAL. La revisión de contenido y el scan de patrones comunes de credenciales no detectaron secretos en los cambios; no se leyeron secrets del entorno.

## Revisión final y consumer local (2026-10-22)

Se revisaron clasificación, bootstrap, A1/A2.1, recuperación semántica, ejecución, POSTCHECK y baseline. Correcciones reproducidas antes del fix:
- Adapter que ignora cancelación después del write: ahora EXECUTION_UNCERTAIN/UNKNOWN, sin receipt SUCCEEDED ni retry.
- Índice adicional de History no rechazado en bootstrap: ahora exige único índice PK activo, KEY MigrationId y constraint coincidente; también bloquea índice ausente/deshabilitado. El reader real incluye estos metadatos; capturas antiguas incompletas deben actualizarse.
- Confirmación CAS perdida: el wrapper conserva BASELINE_COMMIT_UNCERTAIN cuando el protocolo informa outcome Unknown; no afirma ausencia de persistencia.
- PreSources requiere ClassificationRulesVersion=2 obtenido de GOVERNANCE; no hay override de caller. Rules1 sin PreSources conserva compatibilidad.

La salida real Node/Bash se consume en A2.1 para V3/V4/V5. V2 atraviesa las cuatro fases y A1/A2.1; V1/V6 tienen negativos. El consumer Workflow local transporta las cuatro referencias originales hasta el host, consume PLAN/NO_CHANGES sanitizados y preserva las APIs de continuación REVALIDATE/VERIFY_POST. No expone APPLY ni CERTIFY. UNIFIED_ACTIONS_REVISION=null bloquea su activación antes del merge humano; los pins existentes no cambiaron.

Workflow #46 está MERGEABLE pero REVIEW_REQUIRED; no conflicto Git ni checks reportados. Su HEAD permanece 1932b3451096dbd4ba8c80062e8d98bd5bdf0505 y no contiene la adaptación dependiente. Guía local: C:/Repos/workflow/.github/workflows/EF_UNIFIED_LOCAL_INTEGRATION.md. La rama local no se publica en esta tarea.

Los gates externos son reales: identidad/roles de productores (Seguridad/Gobierno), capturas físicas independientes y proyecciones completas (DBA), host SQL y journal durable/fencing (Plataforma), store CAS y transición Registry (Persistencia/Gobierno). El contrato exacto está en EF_UNIFIED_TEST_INFRA_REQUIREMENTS.md y la guía del consumer. No se afirma runtime corporativo habilitado.