# A2.3 — habilitación verificable de NO_CHANGES en TEST

Estado al 2026-10-14: **L0/L1; identidad EXTERNAL_DEPENDENCY_NOT_ACCREDITED**.
No hay evidencia de L2 (identidad acreditada), L3 (adquisición real vinculada) ni
L4 (NO_CHANGES real). Esta nota define el trabajo y la evidencia necesarios para
continuar sobre el circuito corporativo existente. No registra un verifier, no
aprueba una federación ni autoriza acceso a secretos, SQL o workflow dispatch.

## Baseline y capacidad disponible

| Componente | Revisión / alcance comprobado |
| --- | --- |
| Actions | Base integrada `f51e0e80b668b0cb84810a8b9a302d95b0a2aa2a` (PR #39). Trabajo A2.3 en `codex/a2-3-discovery-identity-errors`. |
| Workflow | Main `b81d71783b216ad77e7df5bad4825139e7555e30`. PR #44 integrado en `42ddb102a1bc50526f19e1664d2827a89ee5be34`; su árbol `2554ca008823b0afdbe04690c8efc5371c3b1ad1` coincide con el revisado `eb958fff45f27e31a0af498f7078ef8abb0f3fc9`. El avance posterior agrega dos workflows AKS y no modifica EF. |
| Luana | Fuente actual inspeccionada `2bd491451664904994e52133bfc954134443146b`; distinta de la revisión histórica de adopción indicada abajo. |
| Piloto | Main `0a01d61f81af8e142c49308c6a6abd67ec6d4936`, limpio e igual al main remoto consultado. |
| Circuito | `ef-test-migration-controlled.yml`, ruta `PLAN` → `ef-release-plan/action.yml` → `scripts/run-ef-release-plan.mjs` → `prepare-ef-release`. PLAN excluye análisis tradicional y APPLY. |

El incremento de software A2.3 preserva timeout/cancelación al adquirir la identidad
física en SQL Discovery V2. Un fallo de lectura de identidad no debe convertirse
en una observación utilizable ni ocultarse como un error genérico. Este cambio
mejora la adquisición existente; no acredita al host ni a sus productores.
El consumidor es el CLI existente, invocado por `sql-discovery-v2/action.yml` y
`run-sql-discovery-v2-public.mjs`. La ruta PLAN llega a él mediante
`database-discovery-v2-test.yml`. Su pin SQL actual sigue en
`af83061c98a26263c8684e044ee81a535dde5896`: esta corrección no llegará a ese
runtime hasta merge humano de Actions y un repin revisado posterior. No se cambia
APPLY, LEGACY, callers ni pins a revisiones sin integrar.

Validación local del incremento: Release sin restore, cero warnings/errores;
regresión nueva RED: dos controles pasan y ocho errores son ocultados como
`Succeeded`; GREEN: diez grupos pasan. Harness SQL Discovery V2 completo **63/63**;
wrapper público **52/52**, contrato productor .NET → Node PASS y terminal **17/17**.
El harness necesitó ejecución fuera del sandbox para lanzar Git Bash; cuatro
casos de clasificación fallaron por ese entorno antes de pasar allí. Matriz del
workflow **152.064** combinaciones, publicación **32**, guards de host y reusable
PASS. Los fallos inyectados son `TimeoutException`, cancelación, autenticación
normalizada e I/O: no prueban errores nativos SqlClient, login/TLS ni SQL real.

| Etapa necesaria | Estado | Evidencia / límite |
| --- | --- | --- |
| Selección PLAN y exclusión APPLY | READY_VERIFIED, alcance local/estático | Ruta integrada; no demuestra una ejecución TEST. |
| Gobierno y resolución de source | IMPLEMENTED_UNVERIFIED en runtime A2.3 | Resolver corporativo y verificación de Git disponibles; no autentican observaciones SQL. |
| Identidad y deployment del host | EXTERNAL_DEPENDENCY_NOT_ACCREDITED | Primera frontera pendiente: política aprobada y prueba de host/workload/runner. |
| Discovery V2, history y Schema Capture | IMPLEMENTED_UNVERIFIED para este caso | Productores existentes; adquisición real, permisos y sesión aún no acreditados. |
| Mapping autenticado hacia A2.1 y provider productivo | MISSING_IMPLEMENTABLE, condicionado al contrato aprobado | La API existe; falta implementar el adapter contra el mecanismo corporativo concreto. |
| Proyección completa EF de datos/seguridad y transporte | MISSING_IMPLEMENTABLE + EXTERNAL_DEPENDENCY | Hay lectores reutilizables; falta política de alcance, binding y transferencia verificada. |
| PRE/baseline independiente del candidato | EXTERNAL_DEPENDENCY_NOT_ACCREDITED | Declaración MANAGED y hashes de artifacts no prueban un baseline certificado ni el estado SQL actual. |
| Validación NO_CHANGES | READY_VERIFIED, alcance offline | A2.1 exige evidencia, freshness, baseline e historia coherentes; no requiere ensayo si no hay cambios. |

La primera frontera está en `EfOperationalCli.cs`: el CLI registra
`EfGitHubSourceHostV1`. Este host verifica Git corporativo y devuelve provider nulo
con `EXECUTED_HOST_ATTESTATION_REQUIRED`; las variables `GITHUB_*` sólo correlacionan
contexto. `EfOperationalHostV1` no entra en A2.1 con esa sesión bloqueada.
`IEfAuthorizedEvidenceProviderV1` es un registro del host, nunca un selector de
implementación aportado por JSON, artifact, flag o variable.

El núcleo existente ya distingue el caso sin cambios: `EfEvidencePreparationV1`
valida governance/source/PRE y proyecciones, compara la historia ordenada, exige
forward/rollback vacíos y verifica freshness antes de devolver `NO_CHANGES`.
La qualification aislada se exige después de esa rama, para cambios pendientes.
`EfReleasePlannerV1.ValidateNoChanges` exige EXISTING_EF, historia PRESENT idéntica,
PRE fresco y baseline certificado coincidente. No corresponde agregar rehearsal
al caso sin cambios ni retirar sus requisitos de autenticidad/proyección.

## Candidato concreto, todavía no habilitado

El Registry inspeccionado declara:

- `targetId=6e43bc5f-0d27-4e5d-9af8-3709ab69d5f5`, aplicación `3602`, ambiente `TEST`.
- Base `CICDV3`, endpoint gobernado `DBCICDV3TEST`, `ALLOW_LIST=[sqlv1testdcsrv1]`.
- Lifecycle `EXISTING`, modo `EF_MIGRATIONS`, onboarding declarado `MANAGED`.
- Source `infrastructure-services/devops-luana-api`, proyecto
  `src/Infrastructure/Infrastructure.csproj`, inicio managed
  `20260616182600_AjusteModeloNet10`.
- Adopción histórica `4b00e39d5427ac88befd4887c51ac6844cec8a40`; no sustituye a la
  revisión actual del caller ni debe igualarse artificialmente a ella.

Es un candidato de selección, no un NO_CHANGES demostrado. Falta observar bajo
autorización el historial real completo, demostrar ausencia de pendientes y drift,
y resolver un baseline independiente aplicable. La exclusión gobernada de un
artifact anterior a la adopción sólo afecta la proyección managed; no permite
borrar filas, duplicados o divergencias del historial raw. No modificar la base
ni fabricar historial para conseguir este resultado.

## Mecanismos encontrados y propuesta mínima de confianza

El workflow selecciona el grupo de runners `Test`. La consulta de runners propios
del repositorio devolvió una lista vacía; no demuestra ausencia de runners de la
organización. La consulta de grupos de runners de la organización devolvió HTTP
403: sus restricciones y administración efectiva siguen sin verificar. El owner
de Plataforma debe aportar esa evidencia; no se solicita elevar este token a
administrador de la organización. La configuración OIDC consultada de Workflow devuelve
`use_default=true` y `use_immutable_subject=true`: es configuración de subject,
no evidencia de una federación aprobada o de un verifier desplegado. Luana tiene
environment `Test` sin protecciones declaradas y existen environments DBA; su
existencia no acredita una aprobación efectiva ni aislamiento del host.

Existe una Action corporativa de Key Vault identificada por la revisión
`b2109d2cbbdd9db64165ec9101787d8fa9b9afcd`, que obtiene secretos por REST con credenciales de service principal.
Esto acredita una implementación de entrega de credenciales, no sus grants
efectivos ni autorización por propósito de evidencia. El pin histórico de APPLY
`aac0078bb5c9a641991b0c18dc3d372079cca69f` apunta a otra Action; su corrección
permanece una incidencia separada y no forma parte de este incremento.

Propuesta para aprobación: usar GitHub OIDC como identidad del workload, el host
corporativo controlado en un runner administrado aislado como ejecutor y una
política gobernada de productores por propósito. La política debe vincular
caller/reusable workflow/revisiones, run/attempt, target, sesión, bytes y vigencia.
El deployment aprobado debe cubrir checkout, build, runtime y DLL ejecutada;
OIDC por sí solo no prueba esos bytes ni el aislamiento. No se propone un servicio
nuevo de identidad ni se implementa un verifier sin el contrato aprobado.

Si la organización mantiene su service principal Entra aprobado para lectura de
Key Vault, puede reutilizarse para acceso de inspección con mapping/grants
acreditados. No reemplaza la verificación de identidad y autoridad de productores.
Las attestations nativas de GitHub pueden aportar evidencia de artifacts si están
disponibles y aprobadas; no se asume su disponibilidad ni que prueben permisos SQL.

Referencias para evaluar esa implementación: [GitHub OIDC](https://docs.github.com/en/actions/reference/security/oidc),
[artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations)
y [GitHub Actions con Azure Key Vault](https://learn.microsoft.com/en-us/azure/developer/github/github-actions-key-vault).
Estas capacidades documentadas no acreditan la configuración corporativa.

Decisión concreta solicitada a Seguridad e Infraestructura (propietarios nominales
por confirmar): aprobar o rechazar que el host corporativo desplegado bajo control
de Plataforma sea la autoridad de adquisición, con OIDC verificado y política de
productores fuera del control del caller. La alternativa es consumir un verificador
corporativo ya aprobado si se identifica. La entrega de Key Vault por SP puede
continuar separada; aprobarla no aprueba evidencia ni obliga a crear un servicio.
Antes de implementar el registro productivo deben quedar resueltos:

1. Issuer `https://token.actions.githubusercontent.com`, firma y claves oficiales,
   audience exacta del consumidor aprobado, vigencia y regla de revocación/replay.
   El subject debe corresponder al **caller** autorizado; el subject observado
   de Workflow no se debe copiar como si identificara a Luana.
2. Binding verificable a repo/id/ref/revisión del caller, reusable
   `infrastructure-services/workflow/.github/workflows/ef-test-migration-controlled.yml`
   y su SHA aprobado, run/attempt, Actions integrado, target y propósito. La sesión
   de adquisición y los hashes se vinculan por el host acreditado: no se presentan
   como claims que GitHub haya emitido por sí mismo.
3. Custodia de los bytes entre productores y host: misma sesión aislada o mecanismo
   corporativo de transferencia/attestation aprobado. Cada objeto debe resolver
   propósito, productor/revisión, target/incarnation, sesión, instante, contenido
   y política de proyección. Artifacts ordinarios y hashes solos no satisfacen esto.
4. Permisos GitHub: mantener lectura de contenidos; solicitar `id-token: write`
   sólo en el job que realmente adquiera identidad si se aprueba OIDC. Cualquier
   permiso adicional de attestations depende del mecanismo elegido. Ningún permiso
   administrativo o de escritura de repositorio se necesita para producir PLAN.
5. Key Vault: lectura del secreto de inspección exacto del target gobernado,
   sin lista/escritura/Owner. DBA debe verificar `CONNECT`, visibilidad de metadata
   (`VIEW DEFINITION`) y `SELECT` del historial completo. `VIEW ANY DATABASE` sólo
   se necesita si se pretende acreditar ausencia; no se solicita para un target
   EXISTING encontrado. Las lecturas de datos/seguridad requieren el alcance
   completo aprobado y sus grants precisos, nunca `db_owner` como sustituto.
6. El éxito de una conexión no vincula todas las observaciones: Discovery usa
   conexiones separadas. Exigir correspondencia de identidad/incarnation al
   principio y final de adquisición, ventana temporal aprobada y revalidación
   concurrente de source/gobierno/baseline. Un cambio debe invalidar el conjunto.

## Solicitudes concretas a ocho responsables

No se envió ninguna solicitud externa. Esta tabla es el paquete de habilitación
para revisión; ninguna fila permite saltar un bloqueo técnico.

| Responsable | Solicitud mínima | Evidencia de aceptación | Negativa obligatoria / nivel que habilita |
| --- | --- | --- | --- |
| 1. Seguridad / identidad de workloads | Identificar y aprobar el mecanismo existente, su contrato de consumo y política versionada de issuer, audience, firmas, tiempos, caller/reusable, revisiones y sesión. | Decisión aprobada y verifier/configuración identificables; receipt sanitizado de una sesión válida ligada a run/attempt/target/propósito. | Firma alterada, issuer/audience incorrectos, revisión no permitida, expiración y replay rechazados. **L2** junto con deployment. |
| 2. Infraestructura de runners / build | Acreditar runner del grupo Test, aislamiento, runtime y build del host fijado; separar procesos de aplicación y adquisición. | Inventario/política del runner, revisiones del toolchain y vínculo comprobable del binario ejecutado con la sesión. | Binario alterado y ejecución fuera del host permitido rechazados; proceso de aplicación sin acceso a credenciales de adquisición. **L2**. |
| 3. Gobierno / responsables de Luana | Confirmar target, endpoint, source actual, regla de adopción y selección PLAN mediante el gobierno existente. | Revisión exacta aprobada de governance/onboarding/source, manteniendo separados SHA histórico, actual, Actions y Workflow. | Otro repositorio/target, PR o merge ref no autorizado, source divergente o cambio concurrente bloquean. Prerrequisito **L3/L4**. |
| 4. DBA / responsable del estado certificado | Entregar o resolver un baseline independiente y aplicable; acreditar historia raw completa y correspondencia managed sin alterar la base. | Referencias verificables del baseline y su autoridad; PRE posterior vinculado y sin drift inexplicado. | Baseline inexistente, obsoleto o cambiado; historia duplicada/divergente/incompleta bloquean. **L3**, necesario para **L4**. |
| 5. Key Vault / permisos SQL | Acreditar mapping del endpoint a credencial de inspección y grants mínimos mediante la entrega corporativa. | Identidad/propósito autorizados y receipts sanitizados de acceso y denegación; separación de token Git, build, inspección y Owner. | Secreto ausente o ambiguo, target fuera de scope y credencial de mutación en PLAN bloquean. **L3**; requiere autorización de acceso. |
| 6. Responsables de productores / proyección | Aprobar propósito, revisión y alcance de Discovery, schema/history/source y proyección completa de datos/seguridad. | Registro productor→propósito→revisión; contrato de proyección, hash de scope, cobertura y mapping exacto a A2.1. | Productor no autorizado, cobertura parcial, endpoint/session incorrectos, evidencia vieja o alterada rechazados. **L3** tras implementar adapters. |
| 7. Mantenedores de Actions / Workflow | Implementar adapter productivo y transferencia protegida contra el contrato aprobado, reutilizando el circuito PLAN y lectores actuales. | Round trip de bytes/referencias con receipts, hash y contexto; tests positivos/adversariales; DAG PLAN/APPLY y artifacts allowlist conservados. | Artifact parcial, sustitución de referencia, replay, error/timeout/cancelación o APPLY desde PLAN fallan sin éxito falso. Integración necesaria para **L3/L4**. |
| 8. Responsable de la prueba TEST / usuario | Autorizar una ejecución acotada después de aprobar identidad, permisos, implementación y candidato. | Run/job/attempt y SHAs exactos, resultado y receipts retenidos; ninguna ejecución APPLY ni escritura SQL. | Falta de cualquier evidencia conserva BLOCKED; no reintentar con permisos mayores ni aceptar resultado sintético. **L4** sólo si resulta NO_CHANGES auténtico. |

Riesgos concretos si faltan: (1–2) un proceso o binario no autorizado podría
atribuirse evidencia; (3–4) source/target/baseline incorrectos podrían ocultar drift;
(5) una credencial excesiva ampliaría exposición; (6–7) evidencia parcial o
reproducida podría parecer actual; (8) se confundiría evidencia offline con L4.
La acción de desbloqueo es entregar los elementos de aceptación de cada fila;
no basta una aprobación genérica de OIDC o un permiso SQL sin scope.

## Software pendiente y secuencia TEST

La aprobación de identidad **no deja E2E inmediatamente listo**. Todavía hay que
implementar y verificar el consumo del mecanismo aprobado, el registro por
propósito, la normalización de adquisición real a A2.1 y el transporte protegido.
También falta la política completa de proyección EF. Hay código reutilizable:
`SqlRecoverySecurityCatalogReader` valida target/scope y doble captura estable;
`SqlLegacyDataEvidenceReaderV1` implementa un multiset de filas acotado. Son
componentes de lectura/recovery, no evidencia de cobertura completa EF ni razón
para modificar los ejecutores LEGACY. Reutilizarlos requiere demostrar el alcance
y preservar su semántica. No reintroducir build de aplicación sin productor real
que consuma su salida y transferencia aislada verificable.

Cuando las filas anteriores estén resueltas:

1. Integrar los adapters revisados, registrar los SHAs efectivos y verificar la
   política caller/PLAN. Conservar excluidos análisis tradicional y APPLY incluso
   si el caller envía `apply=true`.
2. Ejecutar tests offline afectados: autenticación/propósito, tampering/replay,
   cambios concurrentes, target/session, cobertura, history, timeout/cancelación,
   resultados parciales y no concesión de autoridad. Los fakes no acreditan L2.
3. Aprobar el candidato y solicitar autorización explícita para adquisición SQL
   y un único dispatch TEST. Resolver la credencial de inspección sólo en el host
   aprobado; no heredar secretos de aplicación/build/Owner.
4. Capturar evidencia auténtica, normalizar sin alterar identidad/hashes ni
   ocultar errores, verificar baseline y freshness, y consumirla con A2.1.
5. Si no hay pendientes y todas las condiciones son válidas, exigir `NO_CHANGES`,
   exit 0, `evidenceSetHash`, ausencia de plan/SQL y
   `ExecutionAuthorized=false`, `Certified=false`. Ante cualquier fallo, conservar
   su estado/código; no convertir BLOCKED en éxito.
6. Retener solamente artifacts permitidos y receipts sanitizados. Registrar
   run/job/attempt/SHAs y demostrar exclusión de APPLY y ausencia de escrituras.
   Si hay migraciones pendientes, detener este caso y requerir qualification
   independiente; no afirmar recuperación por análisis semántico.

Rollback de configuración: restaurar el pin revisado anterior y retirar sólo los
grants nuevos autorizados, mediante el proceso corporativo. Este recorrido
PLAN-only no requiere ni autoriza rollback de base, Registry u Onboarding.
