# EF unificado — validación dirigida y siguiente etapa

Fecha: 2026-10-22. Se ejecutaron pruebas offline dirigidas; **no** se ejecutó la suite interna integral ni SQL físico. Todos los resultados simulan productores/hosts donde no existe autoridad corporativa acreditada.

## Resultados .NET del incremento

Compilación Release, sin restore: cero errores y cero advertencias.

| Selector del runner | Casos/grupos | Resultado |
| --- | ---: | --- |
| --ef-bootstrap | 13 | PASS |
| --ef-release-plan | 14 | PASS |
| --ef-preparation | 13 | PASS |
| --ef-operational-host | 11 | PASS |
| --ef-initial-recovery | 5 | PASS |
| --ef-normalization | 2 | PASS |
| --ef-unified-classification | 2 | PASS |
| --ef-protected-execution | 5 | PASS |
| --ef-baseline-commit | 3 | PASS |
| --recovery-coverage | 42 | PASS |
| --legacy-rehearsal | 45 | PASS |
| --ef-test-boundary | 55 | PASS |
| Total sin duplicar reruns | 210 | 0 fallos |

Cada grupo nuevo puede contener varias mutaciones adversariales. El total cuenta las entradas reportadas por el runner, no cada mutación interna. El número previo de 228 fue un error de suma: las ocho suites de aquel corte sumaban 188. No se suman nuevamente los reruns.

## Node, Bash y Workflow

- test-ef-unified-classification.mjs: 12 casos dirigidos V1–V6, errores técnicos y duplicados.
- test-classification-adapter-v2.mjs: 90 casos del comportamiento previo del adapter.
- test-ef-release-plan-adapter.mjs: 13 casos de artifacts/CLI, PASS. El primer intento tuvo EPERM al crear un hard link temporal; el rerun fuera del sandbox pasó completo.
- test-classification-v2.sh: 62 casos de regresión del clasificador anterior, PASS.
- Workflow test-ef-plan-route.mjs: 33 tests de rutas, autoridad, CLI y artifacts.
- Workflow test-ef-plan-dag.py: 152.064 combinaciones DAG, 22 contextos reales del guard de source y 32 casos de publicación.
- Workflow test-ef-test-migration-controlled-static.sh: PASS; ejecutores históricos y separación PLAN/TRADITIONAL preservados.
- git diff --check: verificación en ambos repositorios. Advertencias de conversión LF/CRLF no son errores de whitespace.

Git Bash requirió ejecución fuera del sandbox porque MSYS no pudo crear su objeto de proceso; los comandos siguieron siendo exclusivamente locales, sin red, dispatch ni SQL.

## Comandos reproducibles

Desde C:\Repos\actions, PowerShell:

```powershell
dotnet build tests/DatabaseReleaseQualification.Tests/DatabaseReleaseQualification.Tests.csproj -c Release --no-restore
$runner = 'tests/DatabaseReleaseQualification.Tests/bin/Release/net10.0/DatabaseReleaseQualification.Tests.dll'
$groups = '--ef-bootstrap','--ef-release-plan','--ef-preparation','--ef-operational-host','--ef-initial-recovery','--ef-normalization','--ef-unified-classification','--ef-protected-execution','--ef-baseline-commit','--recovery-coverage','--legacy-rehearsal','--ef-test-boundary'
foreach ($group in $groups) { dotnet $runner $group; if ($LASTEXITCODE -ne 0) { throw $group } }
$env:PATH = 'C:\Program Files\Git\bin;' + $env:PATH
node tests/test-ef-unified-classification.mjs
node tests/test-classification-adapter-v2.mjs
node tests/test-ef-release-plan-adapter.mjs
& 'C:\Program Files\Git\bin\bash.exe' tests/test-classification-v2.sh
git diff --check
```

Desde C:\Repos\workflow\.github\workflows:

```powershell
$env:PATH = 'C:\Program Files\Git\bin;' + $env:PATH
node tests/test-ef-plan-route.mjs C:\Repos\actions
python tests/test-ef-plan-dag.py
& 'C:\Program Files\Git\bin\bash.exe' tests/test-ef-test-migration-controlled-static.sh
git -C C:\Repos\workflow diff --check
```

El argumento del test de Workflow apunta al checkout local bajo prueba; no demuestra que el SHA publicado contenga los contratos locales nuevos. El workflow real continúa fijado al merge #41. No se ejecuta dotnet ef database update ni se obtiene conexión SQL.

## Suite interna integral preparada, pendiente de autorización

Las nuevas clases de casos se agregaron al agregador general del runner, pero no se ejecutó esa suite completa. Después de revisión e integración:

1. Ejecutar la matriz completa del circuito con productores de prueba independientes: V1–V6, ABSENT/EMPTY, una/múltiples iniciales y pendientes.
2. Recorrer FIRST RELEASE→POSTCHECK→commit de baseline→transición gobernada→SECOND RELEASE→NO_CHANGES. Mantener explícita la diferencia entre un store de prueba y uno corporativo.
3. Inyectar capturas parciales/malformadas, timestamps, hashes, source revision, target/incarnation, replay y contexto de producer distintos.
4. Inyectar GRANT, DENY, schema/database owner, principal SID, role membership, objeto inesperado y POST2 divergente. Comprobar ausencia de relajación en LEGACY/TRADITIONAL/EXISTING_EF.
5. Simular carreras, claim duplicado, cancelación antes/después de claim, timeout del write, recibo incierto y fallo de persistencia; comprobar que ningún camino reintenta UP ni aplica DOWN automático.
6. Validar CAS/idempotencia/reconciliación del backend elegido cuando exista contrato aprobado; no inferir durabilidad a partir del fake in-memory.
7. Validar artifacts reales de Actions contra Workflow tras repin al SHA integrado, incluyendo BLOCKED, NO_CHANGES, PLAN y salidas fallidas sin conversión a éxito.
8. Confirmar que APPLY/CERTIFY siguen inaccesibles por CLI/workflow y que el caller no puede registrar providers ni elevar autoridad con flags.

## Gates antes de SQL

Se necesita aprobación específica, target TEST aislado/preprovisionado, políticas de identidad y productores, evidencia de metadata completa, proyecciones acreditadas y journal/store corporativos. El ensayo UP/DOWN/UP físico, pruebas de autenticación, concurrencia distribuida y fallos reales siguen pendientes. No usar fixtures como certificación ni ejecutar la suite interna/SQL por el solo hecho de existir estos comandos.
