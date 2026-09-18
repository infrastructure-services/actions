# SQL Discovery V2 — núcleo local read-only

Este incremento agrega un núcleo aislado; no está conectado a `action.yml`, workflows ni callers.

## Frontera y etapas

`SqlDiscoveryOrchestratorV2` depende sólo de `ISqlDiscoveryTransport`. Ejecuta, con prerrequisitos explícitos: conexión servidor, lookup de base, conexión target, suficiencia de metadata, observación física y observación de EF history. Las etapas dependientes quedan `NotAttempted`; física e history pueden completarse independientemente una vez acreditada la metadata.

`SqlClientDiscoveryTransportV2` es la implementación opcional. Construirla no abre conexiones. Al ejecutarla usa `ApplicationIntent=ReadOnly`, TLS estricto, certificados no confiados rechazados, `ConnectRetryCount=0`, timeouts finitos, cancelación y únicamente consultas `SELECT`. El nombre de base del lookup se envía como parámetro; los únicos identificadores SQL no parametrizables son las constantes internas `dbo.__EFMigrationsHistory` y sus columnas conocidas.

## Mapeo raw V2

`ProjectSources` produce exclusivamente `connectionSource`, `databaseLookupSource`, `metadataSource`, `physicalSource` e `historySource`. No crea declaraciones, repositorio, Registry, onboarding ni Schema Capture.

- La ausencia de base se publica como `NOT_FOUND` sólo desde `NotFoundConfirmed`; visibilidad insuficiente se publica como `UNKNOWN`.
- La conexión target permanece separada. Como el contrato raw no tiene un campo target, cualquier fallo target bloquea la proyección.
- Cancelación y timeouts de lookup, metadata, física o history quedan en el resultado interno y bloquean la proyección porque esos enums raw no los representan.
- Una observación física parcial no publica counts. La consulta completa cuenta objetos de catálogo no enviados por Microsoft y excluye sólo la tabla EF conocida y sus objetos hijos; no clasifica schemas por nombre.
- `technicalObjectCount` permanece ausente: la taxonomía técnica gobernada no está cerrada. Por ello un resultado físico cero no acredita elegibilidad `NEW_EF` a través del adapter vigente.
- EF history distingue ausencia, presencia vacía, presencia con filas, ilegibilidad, estructura inválida, error técnico y no intentada. La estructura exige las columnas EF conocidas y una PK sobre `MigrationId`; los IDs se conservan, incluidos duplicados recibidos desde un transporte doble. `MigrationId ASC` es sólo orden canónico de lectura.

## Pruebas

`tests/SqlDiscovery.V2.Tests` es un ejecutable sin paquetes de test externos. Usa un doble registrador y no construye el transporte real durante recorridos de orquestación. El harness entrega las fuentes SQL a productor raw V2, adapter V2 y classifier V2 reales mediante procesos locales. Las fuentes no SQL son fixtures explícitos del propio test.

No hay evidencia de conexión SQL real, onboarding, Registry, Schema Capture ni validación runtime en este incremento.
