# EXISTING_LEGACY — revisión integral offline

Fecha: 2026-10-25. Veredicto: **EXISTING_LEGACY_CODE_READY_FOR_MERGE** para el alcance offline autorizado. Este incremento no acredita ejecución física ni cierre integrado. Los bindings productivos explícitamente ausentes son dependencias de autoridad/infraestructura, no habilitaciones implícitas por este veredicto.

## Baseline y alcance

- Actions: `4f4d853c53b87dd7cd47b4529d3d0bf0d256c1b2`; rama `codex/legacy-integral-closure` creada desde main limpio con autorización explícita. El remoto conservaba ese SHA al verificar la publicación.
- Workflow: main remoto/local `origin/main` de referencia `98a85359ee73cfaa4c246fd09145a4d5f7982d84`. Checkout existente `codex/ef-existing-adoption`, HEAD `049f5fcc4444b62246847b06532b8d16f6e8531b`, preservado. Main local `31ca4412cf663473aef2f83f1b0078238b0e9139` no se usó como baseline de integración.
- No se modificaron EF, piloto, Luana, Registry real, secrets, environments, permisos ni infraestructura. No se conectó a SQL Server; tampoco a LocalDB o contenedores.
- Únicas superficies compartidas modificadas: `SqlScriptAnalyzer.cs` y `SqlServerSchemaReader.cs`, autorizadas expresamente para procedimientos/funciones mediante perfil LEGACY opt-in. Se conservan constructores sin argumentos y comportamiento predeterminado EF. El enriquecimiento de vistas vive en `LegacySchemaCapture.cs`.
- `OUT_OF_SCOPE_MODIFICATIONS = 0` se verifica contra el baseline antes de publicar. Los cambios de formatos nuevos pertenecen a LEGACY; los hashes EF no se migran ni recalculan.

## Cambios implementados y consumidores

1. **Módulos.** `LegacyModuleAnalysis` analiza CREATE/ALTER/CREATE OR ALTER/DROP de procedimientos y funciones. `LegacyStaticSafety` admite cuerpos acotados, exige nombres resueltos, valida referencias y distingue definición de ejecución. El reader compartido sólo incorpora módulos con `LegacySqlProfile.ModulesV1`. Captura hash de definición, tipo, ANSI_NULLS, QUOTED_IDENTIFIER, schemabinding, collation, recompilación, NULL ON NULL INPUT, EXECUTE AS, firmas/native y propiedades de inlining. Metadata ausente/encriptada/firmada queda incompleta.
2. **Vistas.** `LegacySchemaCapture` compone el reader con una consulta SELECT adicional de propiedades persistidas; no cambia la consulta ni el contrato EF de vistas. Acquisition y runtime LEGACY usan esta composición. El ejecutor verifica las propiedades de sesión antes del batch exacto; no reescribe SQL.
3. **DATA.** `LegacyReadinessHash.DataScope` exige filas completas de cada tabla afectada. El qualifier bloquea CREATE/DROP TABLE y SELECT INTO cuando la tabla requerida desaparece entre fases: `DATA_OBJECT_LIFECYCLE_UNSUPPORTED`. No sustituye ausencia por un rowset vacío.
4. **SECURITY.** `LegacyRecoverySecurityReader` compone el reader corporativo con prueba independiente y estable de ausencia visible de un objeto. Conserva owner de schema y closure de principals. Acquisition, CLI por fases y runtime usan el wrapper; el reader y guard compartidos de seguridad no cambian.
5. **Autoridad.** La CLI monolítica `rehearse-legacy-package` termina con `LEGACY_PHASED_EXECUTION_REQUIRED` antes de adquirir fuentes. Un runtime que declara `REAL_TEST` por un string no puede producir promotion freeze: se exige el runtime concreto del host.
6. **Adopción read-only.** `qualify-legacy-package --adoption-request request.json --output output` acepta sólo selectores versionados, sin manifest, SQL histórico, approvals ni snapshots del caller. Acquisition adquiere fuentes y dos capturas, admite onboarding REQUIRED/PENDING sólo en esta ruta y no altera Macro 1. `LegacyAdoptionPlanner` usa `DatabaseStateEvaluator`, `DatabaseOnboardingEvaluator` y `CertificationDecisionEngine`. Emite revisión pendiente, registro requerido, reconciliación o verificación de certificación existente; nunca MANAGED/certificado por inferencia.
7. **Continuidad offline.** `LegacyCertificationContinuationV1` consume revisión independiente, observación fresca/incarnation y ejecución del target con DATA/SECURITY exigidas. Reutiliza `CertifiedStateRecordBuilder` y `CertificationCommitProtocol`, preserva predecessor y exige transición gobernada antes del sucesor. La única composición registrada usa `InMemoryCertificationCommitStore`, marca `OFFLINE_SIMULATION` y devuelve `CorporateCertified=false`. `CreateProduction()` bloquea con `LEGACY_CERTIFICATION_AUTHORITY_AND_STORE_UNBOUND`. Contrato/consumidor offline implementados; binding autenticado productivo no registrado.
8. **Replay de ejecución.** `LegacyExecutionLeaseV1` conecta claim/start/confirm/unknown con CLI por fases, ejecutor y hashes de checkpoints. Serializa el recurso canónico, prohíbe sustituir run/paquete/incarnation y conserva incertidumbre. Se completaron historial, revisión CAS y reconciliación por evidencia independiente. El binding durable real está ausente y **las fases mutantes reales quedan bloqueadas** con `DURABLE_EXECUTION_FENCE_REQUIRED`. No existe un flag para habilitarlas. El proveedor en memoria no puede registrarse en el host físico.
9. **Paquete sin intención mutante y release ya aplicada.** Forward sólo lectura se rechaza antes de SQL. `NO_CHANGES_ALREADY_CERTIFIED` se obtiene mediante el consumidor de continuidad con evidencia fresca dimensional, pointer y estado gobernado, sin volver a ejecutar ni persistir. No se infiere no-op para SQL condicional desconocido.

## Matriz L1–L12

| Caso | Estado | Evidencia conectada / límite restante |
|---|---|---|
| L1 DDL reversible | IMPLEMENTADO para subconjunto acotado | Qualification → fases → Recovery Coverage para procedimiento, función escalar y vista con delta efectivo. Índices/columnas reutilizan AST existente. No soporte universal T-SQL. |
| L2 DATA | IMPLEMENTADO con límites; BLOQUEO_INTENCIONAL de lifecycle ausente | Scope completo; PRE/POST1/PRE2/POST2; mismatch de recuperación/reapply bloquea. Proveedor actual: 1–32 tablas, máximo configurable 1–100.000 filas/tabla y 16 MiB por captura. Sólo tipos soportados por `SqlLegacyDataEvidenceReader`; columnas calculadas, ocultas/encriptadas y tablas especiales no se aceptan. |
| L3 SECURITY | IMPLEMENTADO para permisos; BLOQUEO_INTENCIONAL de cambios globales | GRANT/DENY/REVOKE acotados, owners, principals, grantor y permisos recuperados. Roles/memberships/ownership mutantes requieren otro contrato de impacto; no se habilitan por token. |
| L4 Mixtos/batches | IMPLEMENTADO para subconjunto acotado | E2E conectado: módulo + índice sobre tabla + UPDATE + GRANT/REVOKE, batches GO, PRE/POST1/PRE2/POST2 en tres dimensiones y fence por fase. No se afirma soporte universal T-SQL. |
| L5 Destructivos | BLOQUEO_INTENCIONAL donde falta recuperación | Se conservan clases FULL_REVERSIBLE/SCHEMA_ONLY/FORWARD_FIX_ONLY/RESTORE_REQUIRED/UNKNOWN. Dropear módulos requiere SECURITY; perder tabla requerida por DATA bloquea antes de mutar. Aprobación no reemplaza coverage. |
| L6 Evidencia inválida | IMPLEMENTADO | Manifest estricto, paths, commit/blob/bytes, encoding, SQLCMD, GO repetido, target, fuente y hashes; tests históricos reutilizados. |
| L7 Adopción inicial | IMPLEMENTADO OFFLINE; binding productivo externo | CLI y Workflow read-only preparados; registro ausente, BASELINE_REQUIRED, candidato/revisión, commit inicial, persistencia parcial, CAS conflictivo, replay y transición MANAGED. La fuente auténtica de review/incarnation/store aún no está autorizada. |
| L8 Continuidad | IMPLEMENTADO OFFLINE; binding productivo externo | Adopción →001→release 1→002→release 2→003→release 3→004; predecessor, ejecución independiente, cobertura dimensional, transición gobernada y CAS. Freeze no equivale a deployment ni certificado. |
| L9 NO_CHANGES/replay | IMPLEMENTADO para release exacta certificada | Observación fresca de scope, pointer y gobierno obligatorios; DATA-only no se decide por schema. Release desconocida no obtiene no-op; duplicados no autorizan segundo SQL. Fence durable real sigue sin binding. |
| L10 Inconsistencia | IMPLEMENTADO | Drift, fuentes contradictorias, EF presente, metadata insuficiente y cambios externos no se convierten a LEGACY; la adopción sin baseline se trata como revisión pendiente. |
| L11 Interrupciones | IMPLEMENTADO offline; adapter durable pendiente | Timeout/cancelación/fallo por fase, journal, checkpoint alterado, CAS incierto y reserva UNKNOWN; sin retry/rollback/cleanup automático. Durabilidad ante crash real no se afirma. |
| L12 Identidad/ausencia | IMPLEMENTADO OFFLINE; acreditación externa | DB/servidor/endpoint/metadata, ausencia visible e incarnation independiente tienen contratos y negativas. Incarnation cambiada bloquea fence y certificación. El candidato permanece NOT_VERIFIED; no hay CREATE DATABASE. |

## Matriz de familias SQL

| Familia | Positivo probado | Bloqueado / pendiente | Cobertura |
|---|---|---|---|
| Tablas/columnas | ALTER y scope de filas completas; análisis de creación/ausencia | Tabla ausente en una fase DATA; tipos/tablas especiales; combinaciones no demostradas | SCHEMA siempre, DATA al mutar/perder contenido, SECURITY cuando afecta securable |
| Índices/constraints | AST/risks/dependencias y rollback existentes | FK/cascades/expresiones indirectas no demostradas; índices especiales | SCHEMA, DATA/SECURITY según impacto |
| Vistas | ALTER/CREATE OR ALTER/DROP+CREATE; E2E ALTER con delta | QI/ANSI no soportados, metadata ausente; lectura indirecta desde views en DML | Definición + propiedades persistidas + dependencias y SECURITY |
| Procedimientos | CREATE/DROP, ALTER, CREATE OR ALTER; E2E completo y tres releases | EXEC/dinámico, EXECUTE AS, cifrado, firmas/native, opciones no admitidas, referencias externas, DDL/SECURITY/transacciones dentro del cuerpo | Definición y propiedades; SECURITY; definir un cuerpo DML no ejecuta DATA |
| Funciones | Escalar e inline TVF CREATE/DROP; ALTER escalar y E2E | Multi-statement TVF, CLR, tipos definidos por usuario, opciones no admitidas, UDF indirecta | SCHEMA y SECURITY; reapply compara huella íntegra capturada |
| DML | INSERT/UPDATE/DELETE acotados; OUTPUT INTO incluye segundo target | Triggers alcanzables, RLS, cascades, referencias externas, límites del rowset | DATA completo por tablas; schema por sí solo no demuestra datos |
| GRANT/DENY/REVOKE | Permisos acotados; cambio mixto de procedimiento y permiso | Permiso incompatible, scope/principal/grantor incompleto | SECURITY en cuatro fases |
| Roles/memberships/ownership | Captura de identidad y ownership para comparar | Mutaciones no soportadas por el contrato actual; no afirmar soporte positivo | Requiere contrato ampliado antes de aceptar SQL |
| Triggers/efectos indirectos | Detección read-only | DDL triggers habilitados, DML indirecto y creación/alteración de triggers bloqueados | No se considera segura una definición parseable con efecto ejecutable indirecto |

## Matriz de estado y autoridad

| Estado | Fuente / autoridad | Persistencia y prueba offline | Frontera real |
|---|---|---|---|
| Adopción/candidato | Governance/onboarding/Registry Git + discovery y doble captura | Archivo append-only de plan; no aprobación del caller | Identidad/lineage/SCHEMA; DATA/SECURITY/incarnation NOT_VERIFIED |
| Qualification | Resolver de fuentes y blobs exactos | Readiness/handoff, sin permiso de promoción | MANAGED y baseline certificado siguen obligatorios |
| PRE/preflight | Fuentes frescas y credencial de inspección | Checkpoint separado; tests de drift/identity | Sólo lectura; no libera fases mutantes |
| FORWARD1/ROLLBACK/FORWARD2 | Tres jobs protegidos + autoridad + lease durable | Bytes exactos, orden, checkpoint, reserva de operación; fakes offline | Binding durable no registrado: bloqueadas antes de SQL mutante |
| Receipt | Productor y cobertura de cuatro fases | Hash de integridad; corrupto/importado no concede capacidad | El string REAL_TEST no autentica |
| Promotion freeze | Productor concreto en proceso | Negativas de fakes/receipt reimportado | No es certificación ni prueba de deployment productivo |
| Certificación inicial | Revisión independiente, política dimensional, fresh state/incarnation | BootstrapApproved → builder → commit protocol in-memory | Adapter corporativo sin registrar; no nuevo store paralelo |
| Continuidad | Ejecución real separada del rehearsal, predecessor y gobierno | Tres sucesores con CAS; transición MANAGED obligatoria | Provider autenticado y store no enlazados |
| Replay/incertidumbre | Identidad de operación, registro y pointer | Idempotencia del commit; UNKNOWN no se reejecuta | No atribuir durabilidad al diccionario de prueba |

## Evidencia de adopción y recuperación

La adopción no exige leer millones de filas por defecto. El candidato acredita estructura/identidad observada y ausencia de lineage EF en las fuentes adquiridas, pero **no acredita contenido DATA ni SECURITY globales**. El ejemplo offline de review selecciona explícitamente `LEGACY_STRUCTURE_ONLY_ADOPTION_V1`; no es una política corporativa instalada. Incarnation debe venir de evidencia independiente. Una política que exija cobertura adicional debe bloquear mientras no haya proveedor suficiente.

La recuperación de una release posterior es distinta: SCHEMA siempre, DATA y SECURITY según impacto, con igualdad PRE/PRE2 y POST1/POST2. El E2E sintético preserva esta exigencia y jamás obtiene un freeze real o un store corporativo.

## Consolidación final de contratos y consumidores

Continuidad ahora requiere `LegacyCertificationObservationV1`: observación fresca, identidad, incarnation independiente y hashes de las dimensiones exigidas. `LegacyExecutedReleaseV1` enlaza PRE/POST de DATA y SECURITY con el rehearsal calificado y la observación actual. El consumidor usa los motores corporativos; sólo la composición in-memory está registrada. No concede autoridad física.

`NO_CHANGES_ALREADY_CERTIFIED` exige release y payload exactos ya persistidos, pointer vigente, transición gobernada, misma incarnation y observación fresca del scope certificado. No crea otro registro. Mismo schema con DATA distinta bloquea. Un DDL/DML arbitrario todavía no ejecutado no se declara no-op por igualdad de schema. El forward sólo lectura continúa bloqueado antes de SQL.

El worktree Workflow aislado parte de `98a85359ee73cfaa4c246fd09145a4d5f7982d84`. Incluye adopción read-only y guards. Qualification/preflight conservan `unset GOVERNANCE_TOKEN`; únicamente el comando dotnet del productor recibe temporalmente el token de lectura para verificar main. Build y discovery hijos no lo reciben. No se adelantó ningún pin al merge humano de Actions.

## Evaluación final de LegacyExecutionFence

Se inspeccionaron `CertificationCommitProtocol.cs` y `CertifiedStateStore.cs`: las implementaciones concretas presentes son in-memory. El CAS corporativo protege pointer/certificaciones; no reserva operaciones físicas SQL. Se reutiliza para certificación sin insertar certificados ficticios como locks ni crear un Registry/State Store productivo paralelo.

| Pregunta | Contrato implementado y límite |
|---|---|
| Qué protege | Orden y exclusión por recurso entre productores que usan el mismo proveedor. No promete exactly-once ni bloquea SQL externo al protocolo. |
| Identidad | Target, recurso canónico, endpoint/base, incarnation, repository, run, release, package, fase y hash predecessor. Lease identifica la reserva; revisión monotónica identifica cada transición. |
| Reserva | Claim atómico antes de Begin; MarkStarted debe persistirse antes de abrir conexión mutante. Fallo de cualquiera impide SQL. |
| Replay/orden | FORWARD1→ROLLBACK→FORWARD2; predecessor igual al resultado confirmado anterior. Sustituciones y release consumida bloquean. Nuevo incarnation nunca libera una reserva anterior. |
| Checkpoints | CLI verifica integridad, origen, orden y scope antes del claim; engine revalida estado y cobertura antes de mutar. Confirm enlaza hash del checkpoint o receipt completo. |
| Runner termina | CLAIMED/STARTED permanecen reservados. El modelo de journal compartido prueba reconexión lógica; no demuestra persistencia física ante reinicio del servidor. |
| Timeout/cancelación/commit incierto | UNKNOWN permanece reservado. MarkUnknown no sobreescribe un CONFIRMED durable aunque se pierda su respuesta. No hay TTL liberador ni retry SQL. |
| Evidencia | Identidad, lease, estados y revisiones previas quedan en historial append-only; journal local/checkpoint complementan al proveedor durable. |
| Reconciliación | LegacyExecutionReconcilerV1 adquiere un reference opaco de ILegacyExecutionReconciliationSourceV1; no existe CLI de aprobación JSON. Reconcile aplica CAS de revisión y conserva hash de evidencia. |
| Autoridad para resolver | Actor/reviewer distintos, prueba fresca del runner imposibilitado de continuar, identidad exacta y evidencia independiente. CONFIRM exige ejecución y cobertura completa. ABORT_BEFORE_START sólo desde CLAIMED; deja tombstone y revoca el token. STARTED/UNKNOWN no admiten abort por declaración de no efecto. |
| Varios repositorios/alias | Reserva común por CanonicalResourceId adquirido por proveedor. Endpoint/base sólo modela tests. Binding real exige identidad canónica y hash de prueba de incarnation. |
| Proveedor ausente | DURABLE_EXECUTION_FENCE_REQUIRED antes de mutación. In-memory no puede registrarse; reconciliación no instala ni habilita proveedor real. |

Interfaz exacta: `ILegacyExecutionFenceV1.Claim`, `MarkStarted`, `Confirm`, `MarkUnknown`, `Read`, `Reconcile`; records `LegacyExecutionIdentityV1`, `LegacyExecutionReservationV1`, `LegacyExecutionReconciliationV1` y `LegacyExecutionFenceBindingV1`. El proveedor autorizado deberá acreditar atomicidad/durabilidad, historial, autenticación, mapeo de alias, revocación de runners y ausencia de liberación por vencimiento. No basta implementar el diccionario en otro proceso.

## Validación reproducible

Comandos desde Actions, usando Git Bash real en PATH cuando el test llama Node/Bash:

```powershell
dotnet build tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-restore
dotnet run --project tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-build --no-restore
bash tests/test-legacy-package-static.sh
bash tests/test-legacy-rehearsal-static.sh
bash tests/test-legacy-closure-static.sh
bash tests/test-ef-release-plan-static.sh
bash tests/test-ef-operational-host-static.sh
bash tests/test-schema-capture-static.sh
git diff --check
```

Desde `Workflow/.github/workflows`:

```powershell
bash tests/test-legacy-package-qualification-v1-static.sh
bash tests/test-legacy-rehearsal-v1-static.sh
bash tests/test-legacy-adoption-v1-static.sh
python tests/validate-legacy-closure-static.py
```

- Compilaciones dirigidas: sin warnings ni errores, salvo un nombre de fault-point del fixture corregido antes del run.
- Primera suite integral durante desarrollo: 586 casos, 1 fallo de entorno al iniciar Bash en sandbox Windows. El diagnóstico directo fue `NtCreateDirectoryObject: Access denied`. Repetición EF dirigida fuera del sandbox: 3/3 PASS sin cambiar código EF.
- Segunda consolidación: **598 casos, 0 fallos**, incluyendo EF. Log local: `%TEMP%/legacy-closure-38e6274745644705b0474e599e0c186f/full-suite.log`.
- Consolidación heredada: **603/603**; log `%TEMP%/legacy-final-870346ece1dd42c497541a5067e48a92/full-suite.log`. No se reutilizó como prueba de cambios posteriores.
- Consolidación actual dirigida: **63/63**, subconjunto de la suite integral, no adicional.
- Primer run integral actual: **609 casos, 1 fallo** en la expectativa del test de concurrencia LEGACY. El protocolo compartido devuelve ConcurrencyConflict al perdedor de CAS; el test exigía dos confirmaciones inmediatas. Se corrigió el fixture con barrera antes de CAS: exige un único ganador, conflicto explícito, historial único y replay idempotente posterior. No se modificó el protocolo ni EF. Log conservado: `%TEMP%/legacy-engineering-final-ab996b6ead8948dfbdf3cb1f64b644c2/full-suite.log`.
- Regresión integral final: **609/609 PASS**, incluidas regresiones EF, después de la corrección del fixture CAS. Compilación Release: 0 warnings / 0 errores. Log `%TEMP%/legacy-engineering-final-ab996b6ead8948dfbdf3cb1f64b644c2/full-suite-final.log`. No se suman runs repetidos ni el subconjunto 63/63.
- Seis guards Actions: LEGACY package/rehearsal/closure, EF plan/operational host y Schema Capture PASS. Tres guards Workflow: qualification/rehearsal/adoption PASS. Parser sin claves duplicadas: cuatro YAML y sintaxis de 19 bloques Bash + runner por fases PASS. Ningún bloque se ejecutó.
- Red/green reproducidos: scope DATA parcial; CLI monolítica; runtime falso REAL_TEST; ordenación de propiedades de módulos al clonar canonical JSON. El último se corrigió sólo en las claves nuevas LEGACY, sin tocar Certified State.

## Archivos y ownership

- Compartidos autorizados: `SqlScriptAnalyzer.cs`, `SqlServerSchemaReader.cs`.
- Análisis/captura LEGACY nuevos: `LegacySqlProfile.cs`, `LegacyModuleAnalysis.cs`, `LegacyModuleSchema.cs`, `LegacySchemaCapture.cs`, `LegacyRecoverySecurityReader.cs`.
- Consumers y contratos nuevos: `LegacyAdoption.cs`, `LegacyCertificationContinuation.cs`, `LegacyExecutionFence.cs`, `schemas/legacy-adoption-request-v1.schema.json`.
- Consumers LEGACY ajustados: `LegacyPackageCli.cs`, `LegacyPackageQualification.cs`, `LegacyProductionCompositionRoot.cs`, `LegacyReadiness.cs`, `LegacyStaticSafety.cs`, `LegacyStructuralEvidence.cs`, `LegacyRehearsalCli.cs`, `LegacyPhasedCli.cs`, `LegacyPhasedRehearsal.cs`, `SqlLegacyRehearsalRuntime.cs`, `SqlLegacyScopeSafetySource.cs`.
- Tests: helpers/casos LEGACY existentes y nuevos `LegacyModuleTests`, `LegacyAdoptionTests`, `LegacyContinuityTests`, `LegacyExecutionFenceTests`; guard `tests/test-legacy-closure-static.sh`. Ningún test EF fue modificado.
- Inventario final Actions: 34 archivos, todos LEGACY salvo los dos compartidos autorizados. Validación explícita de paths: `OUT_OF_SCOPE_MODIFICATIONS = 0`; `git diff --check` PASS.
- Workflow aislado: `.github/workflows/legacy-adoption-v1-test.yml`, `tests/test-legacy-adoption-v1-static.sh`, `tests/validate-legacy-closure-static.py` nuevos; ajustes de alcance del token sólo en `legacy-package-qualification-v1-test.yml` y `legacy-rehearsal-preflight-v1-test.yml`. No cambios EF ni nuevos jobs de SQL mutante.

## Gates, Git y pendientes

| Gate | Estado |
|---|---|
| G0 preflight | Completado para Actions; checkout Workflow preexistente preservado |
| G1 auditoría | Matrices anteriores, con brechas explícitas |
| G2 adopción | Consumer read-only y composición offline; adapter corporativo pendiente |
| G3 análisis/recovery | Subconjunto positivo de módulos/vistas/DATA/SECURITY implementado |
| G4 continuidad | E2E/CAS/fencing offline; bindings reales bloqueados |
| G5 tests | 609/609 final, 0 warnings/errores, seis guards Actions, tres Workflow y parser/sintaxis PASS |
| G6 publicación | PR Actions pendiente; Workflow depende de merge humano y SHA real |
| G7 cierre | No alcanzado: sin merges ni verificación de pins/main integrado |

CI de Actions inspeccionado antes de publicación: 0 workflows registrados y ningún archivo `.github` en el baseline. Ruleset Enterprise `main` exige PR, aprobación y code owner, impide borrado/non-fast-forward; no contiene required workflows. No se altera esa política.

## Dependencias externas y riesgos residuales

| Componente | Existe / falta exactamente | Tipo de dependencia y función bloqueada | Verificación posterior en TEST |
|---|---|---|---|
| Fence durable | Contrato, lease, consumidores, reconciliación y modelo offline completos. Falta binding de proveedor corporativo aprobado, su almacenamiento y fuente autenticada de reconciliación. | Decisión de servicio/infraestructura/autoridad; luego adapter específico y configuración autorizados. Bloquea las tres fases mutantes reales. No se puede elegir almacenamiento ni privilegios silenciosamente. | Crash/partición, pérdida de respuesta, runner pausado que intenta continuar, alias entre repositorios, atomicidad e historial durable; nunca liberar UNKNOWN por TTL. |
| Certificación corporativa | Se reutilizan decision engine, record y commit/CAS. Contrato de evidencia fresca y consumidor offline implementados. Falta fuente auténtica de review, ejecución independiente y store productivo aprobado. | Autoridad/credenciales/configuración y binding específico del proveedor elegido. Bloquea certificación corporativa y transición operativa a MANAGED; no bloquea las pruebas offline. | Persistencia parcial, CAS, replay, scopes reales, identidad del aprobador, continuidad y no duplicación bajo fallos. |
| Incarnation/identidad canónica | Contratos de observación y fence exigen prueba independiente; negativas por cambio/inconsistencia probadas. Candidato read-only declara NOT_VERIFIED. | Fuente corporativa de identidad y política de recreación/mapeo de endpoints aún no aprobadas. Bloquea binding real y reconciliación de base recreada. | Restore/recreate/rebind, alias, identidad efectiva de conexión y correlación con baseline. |
| Integración Git | Actions listo para PR tras validación; cinco archivos Workflow preparados aisladamente. No hay nuevo job físico APPLY/CERTIFY. | Merge humano Actions → verificar SHA real → pin LEGACY y regresiones → PR Workflow → merge humano → verificar ambos main. | No es ejecución SQL; verificar pins, provenance y conservación de superficies EF. |
| Compatibilidad SQL física | Queries read-only, análisis AST y escenarios sintéticos probados. | Ejecución física expresamente no autorizada. No se afirma soporte operacional validado en un servidor real. | Catálogos/versiones, permisos mínimos, propiedades de módulos/vistas, tipos/límites DATA, transacciones y recovery multidimensional. |

El adapter específico de proveedor no es un motor nuevo ni una segunda implementación del CAS: se completará contra el servicio y autoridad que se aprueben. No existe en los repositorios un proveedor durable concreto que pueda conectarse sin esa decisión. Las APIs internas no admiten registrar implementaciones desde un request, environment o assembly path del caller.

Bloqueo anterior de `validate_adoption.py`: el registro de la evaluación terminó por `usage_limit_exceeded`; el archivo no se creó y no hay una denegación de política concluida. En esta sesión el validator equivalente vive como guard LEGACY del worktree autorizado y pasó. La revisión automática sí rechazó quitar `unset GOVERNANCE_TOKEN`; se aplicó su alternativa de alcance por comando, conservando unset y sin cambiar secrets, permisos ni infraestructura.

La preparación Workflow se conserva en `C:/Users/fgalvan/.codex/worktrees/legacy-workflow-integration/workflow`, inicialmente detached en el main correcto; el checkout `C:/Repos/workflow` permanece en la rama EF sin cambios. Los SHAs de integración y PR se registrarán al completar la publicación de Actions. No se ejecutaron workflows ni SQL, ni se escribieron Registry/Certified State reales.

No hay declaración de cierre funcional integrado: `EXISTING_LEGACY_FUNCTIONAL_CODE_CLOSED` requiere los dos merges humanos y la verificación final. La siguiente acción humana es revisar/mergear Actions; sólo después se verificará el SHA integrado y se fijarán los pins LEGACY de Workflow. La habilitación operativa requiere además proveedores autorizados y acreditación TEST física.
