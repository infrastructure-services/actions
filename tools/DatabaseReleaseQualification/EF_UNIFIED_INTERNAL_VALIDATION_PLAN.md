# EF unificado — validación por etapas

Fecha: 2026-10-22. Este documento distingue checks disponibles de casos futuros.
No acredita recuperación física, identidad corporativa ni certificación durable.

## UNIT_CONTRACT_OFFLINE

PowerShell, desde `C:\Repos\actions`. Dependencias ya restauradas; ningún restore
ni red está incluido. No ejecutar la suite completa sin selector en esta etapa.

```powershell
dotnet build tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-restore
dotnet tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll --ef-bootstrap
dotnet tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll --ef-preparation-directed
dotnet tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll --ef-release-plan
dotnet tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll --ef-operational-host
& 'C:\Program Files\Git\bin\bash.exe' tests/test-ef-release-plan-static.sh
& 'C:\Program Files\Git\bin\bash.exe' tests/test-ef-operational-host-static.sh
git diff --check
```

Desde `C:\Repos\workflow\.github\workflows`:

```powershell
& 'C:\Program Files\Git\bin\bash.exe' tests/test-database-discovery-v2-static.sh
python tests/test-ef-plan-dag.py
git diff --check
```

El test del DAG requiere Python/PyYAML y Node. Git Bash/MSYS y lectura de la raíz Git
de Workflow fallaron dentro del sandbox de Windows; los guards pasaron con ejecución
local autorizada fuera del sandbox. No se cambió código para eludir restricciones.

| Check ejecutado | Resultado |
| --- | --- |
| Build Release `--no-restore` | PASS, 0 warnings, 0 errores |
| `--ef-bootstrap` | 12 grupos PASS, incluyendo múltiples negativas por grupo |
| `--ef-preparation-directed` | 12 grupos PASS; excluye `successive release` |
| `--ef-release-plan` | 14 grupos PASS |
| `--ef-operational-host` | 11 grupos PASS |
| Guards estáticos de PLAN y host | PASS |
| Guard estático Discovery | PASS |
| YAML/DAG | 152.064 casos PASS, 22 contextos reales de source-gate |
| Publicación de artifacts | 32 casos PASS |
| `git diff --check` Actions / Workflow | PASS |

Total: 49 grupos dirigidos .NET, sin contar ejecuciones repetidas por iteración.
La matriz DAG/publicación es estática, no una ejecución de workflows ni una
simulación integral multi-release.

## Fixtures y aceptación del incremento

`EfBootstrapTests.cs` usa el provider falso de `EfPreparationTests`, llama el
servicio A2.1 real y conserva A1/análisis/recovery existentes. Los datos de ensayo
son genéricos (Catalog/Delivery/BootstrapApp); no tienen conexión SQL. El fake de
rehearsal sólo alterna snapshots en memoria.

Aceptar únicamente si:

- EMPTY con bootstrap tipado puede preparar un PLAN de revisión sin autoridad.
- ABSENT admite el contrato de bootstrap, pero sin qualification queda bloqueado.
- Se rechazan bytes opacos, creation receipt, versiones desconocidas y campos duplicados.
- Se rechazan scope, sesión, SHA, gobierno, política, source, PRE o proyección sustituidos.
- Se rechazan cambios en incarnation, schema, datos, seguridad, servidor, base y captura.
- Se rechazan vencimiento, vigencia excesiva, datos no vacíos o no medidos y objetos inesperados.
- ABSENT y EMPTY no se intercambian automáticamente ni en bootstrap ni en recuperación.
- Rol denegado, hash alterado, provider ausente, cambio concurrente y cancelación bloquean.
- EXISTING no usa bootstrap como reemplazo de baseline.

Los reason codes `BOOTSTRAP_*`, `INITIAL_HISTORY_STATE_UNVERIFIED`,
`NEW_BOOTSTRAP_EVIDENCE_REQUIRED` y `UNEXPECTED_BOOTSTRAP_EVIDENCE` identifican
fallos de contrato. JSON no interpretable o errores no clasificables conservan
`EVIDENCE_NOT_VERIFIABLE`; no se publica el mensaje crudo de la excepción.

## INTEGRATION_OFFLINE — preparar/completar, NO ejecutar ahora

Existe `--ef-preparation` con una simulación limitada de release sucesiva. No fue
ejecutada en esta tarea; el selector dirigido la excluye. **No equivale a la
matriz integral NEW→EXISTING→NO_CHANGES**, que todavía requiere fixtures ejecutables.

| Caso futuro | Evidencia esperada | Preparación actual |
| --- | --- | --- |
| V1 ausencia confirmada / visibilidad insuficiente | Reason distinto, cero SQL y cero PLAN | Matriz especificada; falta recorrido unificado |
| V2 initial ABSENT con 1/N migraciones | SQL exacto, Expected POST, recovery válida | Admisión unit/contract disponible; recovery integral pendiente |
| V3 EMPTY acreditada / no acreditada | PLAN o INITIAL_HISTORY_STATE_UNVERIFIED según autoridad | Contratos A2.1 disponibles; classifier/consumer integral pendiente |
| V4 una/N pendientes, prefijo válido | Sólo delta pendiente y rollback ligado a hashes | A1/A2.1 unit/contract disponibles |
| V5 historia/source completos y PRE fresco | NO_CHANGES, ningún SQL ni PLAN | A2.1 unit/contract disponible |
| V6 duplicados, drift, baseline vencido, mismatch, timeout/cancelación | BLOCKED con razón estable | Cobertura parcial dirigida; matriz integral pendiente |
| Approval ausente/vencida, host no acreditado, replay y artifact sustituido | Ninguna ejecución autorizada | Host/contratos parciales; APPLY protegido pendiente |
| PRE→UP→POST1→DOWN→PRE2→UP→POST2 | Todas las dimensiones y hashes verificados | Fake estricto existente; equivalencia inicial pendiente |
| Commit incierto y concurrencia de baseline | EXECUTION_UNCERTAIN, sin retry; CAS sin falsa certificación | Motores existentes a integrar; fixture integral pendiente |
| NEW→FIRST_POST→BASELINE→EXISTING_PENDING→SECOND_POST→NO_CHANGES | Transición gobernada y continuidad exacta | No implementada como suite integral |

Antes de autorizar esta etapa: completar implementaciones anteriores, publicar la
matriz runnable con sus comandos reales y revisar sus runners. No inventar comandos
de suites inexistentes ni marcar estas filas como ejecutadas.

## SQL_LAB_VALIDATION — pendiente y no autorizada

No ejecutar SQL Server, Docker SQL, `dotnet ef database update`, dispatch ni ensayos
físicos. Los requisitos externos están en `EF_UNIFIED_TEST_INFRA_REQUIREMENTS.md`.
La etapa posterior debe demostrar datos/seguridad reales, aislamiento, equivalencia
de recuperación y POST independiente antes de cualquier aprobación operativa.
