# EF unificado — requisitos externos y pruebas posteriores

Fecha: 2026-10-22. Estado externo: EXTERNAL_DEPENDENCY_NOT_ACCREDITED.

## Fronteras que el código no simula como productivas

| Dependencia | Contrato requerido | Estado actual |
| --- | --- | --- |
| Identidad del host | Identidad verificable del job/run, revisión ejecutada, target, source y sesión; revocación/freshness. | Factory productiva fail-closed; verificar Git no acredita al host. |
| Productores | Autorización por propósito para Discovery, Schema Capture, history, proyecciones, bootstrap, qualification, clasificación y receipts. | Interfaces/validadores y providers offline; ningún verifier corporativo registrado. |
| Seguridad completa | Inventario completo de principals/SID, memberships, grants/denies/grantors, owners, scope y visibility; evidencia independiente por fase. | Contrato de contenido validado; captura SQL/acreditación pendientes. |
| Datos | Proyección versionada, completa, reproducible y vinculada al mismo target/incarnation y sesión. | Normalizador exige contrato/provenance/hash; algoritmo y productor real deben acreditarse. |
| Ejecución | Credenciales separadas, identidad física verificada y PRE vigente bajo la frontera de escritura. | Reutilización del contrato exact-byte existente; host SQL corporativo no implementado por falta de contrato aprobado. |
| Exclusión durable | Claim atómico de OperationId, fence sobre el target, estado de intento, ausencia de retry y reconciliación tras crash/timeout. | Prueba lógica in-memory; no equivale a garantía durable/distribuida. No backend elegido. |
| Certificación | Store corporativo con protocolo CAS/idempotente, evidencia append-only y autoridad independiente. | Protocolo/store existentes reutilizados offline. Ningún store externo registrado. |
| Registry/Onboarding | Transición autorizada NEW→EXISTING y referencia al baseline realmente persistido. | Simulación explícita; no escrituras externas ni auto-certificación. |
| Workflow | SHA integrado de Actions que contenga los nuevos contratos y selección corporativa de rulesVersion 2. | Pin local de PLAN sólo a #41 integrado. Nuevos contratos requieren otro merge/repin revisado. |

## Preparación del entorno posterior

- Base TEST ya provisionada y gobernada; nunca CREATE DATABASE desde este circuito.
- Laboratorio separado del target, con identidad e incarnation verificables y metadata completa.
- Inspección read-only separada de mutación; sin secretos en argumentos, logs, artifacts o manifest.
- Capturas PRE/POST1/PRE2/POST2 independientes y contemporáneas, sin excluir arbitrariamente EF History.
- Política de History revisada para el layout real. La versión actual acepta dbo.__EFMigrationsHistory con MigrationId nvarchar(150), ProductVersion nvarchar(32), NOT NULL y PK(MigrationId).
- Permisos, runner, almacenamiento, retención, fencing y mecanismos de autorización definidos por Infra/DevOps; no se crean durante esta tarea.
- Procedimiento humano para resultado UNKNOWN y commit/pointer inciertos. Reintentar la escritura no es recuperación.

## Orden de habilitación

1. Publicación autorizada del incremento mediante commits/push/PR y revisión humana; sin merge automático.
2. Merge humano de Actions; registrar el SHA real.
3. Completar repin y conexión de los contratos nuevos en el reusable existente, con guards de rutas/DAG/artifacts.
4. Acreditar identidad, productores, proyecciones, journal y store; mantener fail-closed mientras falten.
5. Autorizar y ejecutar validación interna integral, todavía pendiente.
6. Sólo con autorización separada, preparar ensayos SQL aislados. Su ejecución no está autorizada aquí.

Luana, piloto, LEGACY, TRADITIONAL, infraestructura, Key Vault, secrets y permisos permanecen fuera de las modificaciones. No se hizo dispatch, SQL real, Docker SQL, localhost SQL, APPLY real ni escritura externa de Registry.
