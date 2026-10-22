# EF unificado — dependencias para una etapa TEST posterior

Fecha: 2026-10-22. Estado: **EXTERNAL_DEPENDENCY_NOT_ACCREDITED**.
Este documento no autoriza ni configura infraestructura. No se contactó a equipos
externos ni se leyeron secretos durante el incremento.

## Contrato externo imprescindible

El owner corporativo de identidad debe definir y aprobar cómo el host acredita:

1. Identidad de workload/runner, repositorio caller y reusable, SHA de ambos,
   run/attempt y deployment exacto del host, incluyendo build/runtime ejecutado.
2. Autorización de cada productor por propósito, target, source y sesión; denegar
   que el caller registre un provider o convierta variables `GITHUB_*` en autoridad.
3. Verificación criptográfica o mecanismo corporativo equivalente, su raíz de
   confianza, emisor/audiencia/sujeto admitidos, vigencia y revocación. Ningún
   mecanismo concreto se da por desplegado o aprobado.
4. Transporte de bytes y política de integridad/freshness/replay para governance,
   repository/source, PRE, bootstrap, baseline, proyecciones y qualification.
5. Procedencia y política autorizada de historia EMPTY: quién puede atestiguarla,
   con qué evidencia y bajo qué revisión. El nuevo documento no la autocertifica.

Sin estas decisiones no puede completarse un verifier productivo compatible con
la autoridad real. `EfGitHubSourceHostV1` permanece bloqueado; las pruebas con
provider falso sólo demuestran validaciones independientes de esa decisión.

## Requisitos por responsable funcional

| Área | Evidencia requerida antes de SQL TEST |
| --- | --- |
| Runner TEST | Grupo y aislamiento aprobados; despliegue y revisiones atestiguados; separación de inspección/escritura |
| Identidad y Key Vault | Identidad corporativa autorizada, mapping/grants mínimos por target/propósito; sin secretos en artifacts |
| Target preprovisionado | Registro gobernado, servidor/endpoint/base/incarnation inequívocos; ningún permiso CREATE/DROP DATABASE para el circuito |
| Productor de bootstrap | Documento V1 completo y autorizado, taxonomía y estado vacío, cobertura de datos/seguridad, historia ABSENT/EMPTY y provenance |
| Proyecciones | Algoritmos versionados y alcance completo para schema, datos, principals, grants y ownership; sin exclusiones permanentes |
| Source EF | Aplicación candidata gobernada, repositorio y SHA inmutables, proyectos/migraciones válidos; adopción histórica separada |
| Baseline | Certified State independiente, lineage y actualidad/incarnation verificables; storage locator no redefine identidad |
| Laboratorio aislado | Base previamente provisionada, target/incarnation distintos, equivalencia de contenido PRE, permisos acotados y eliminación por proceso externo |
| Aprobación | Evidencia independiente ligada a operación/target/PLAN/payload/vigencia; `apply=true` no autoriza |
| Ejecución futura | Bytes exactos aprobados, sin regeneración ni retry automático; incertidumbre detiene y exige reconciliación |
| POST/certificación | Captura independiente, comparación de todas las dimensiones y commit/CAS acreditados; candidate no equivale a durable |

Luana y piloto siguen siendo fuentes de consulta. Cualquier cambio futuro en sus
callers, environments o permisos requiere autorización específica. No hay una base
real declarada apta por estas pruebas y no se cambió Registry ni Onboarding.

## Criterios de éxito y recuperación de la etapa futura

- Adquirir evidencia auténtica y fresca para cada variante y conservar su identidad.
- Confirmar ausencia sólo con visibilidad suficiente; cualquier error técnico bloquea.
- Demostrar UP/DOWN/UP físico en aislamiento y comparar historia, schema, datos,
  seguridad, identidad e incarnation; aprobar explícitamente la equivalencia inicial.
- Demostrar preservación del payload entre PLAN, aprobación y ejecución.
- Ante timeout/cancelación/commit incierto, detener sin retry de writes y reconciliar
  mediante un procedimiento autorizado. No restaurar automáticamente como sustituto
  de rollback ni crear otra base para ocultar un fallo.
- Persistir certificación sólo mediante el protocolo y backend aprobados. El store
  in-memory y las simulaciones no demuestran durabilidad ni habilitan QA/PROD.

Primero cerrar el código pendiente enumerado en `EF_UNIFIED_IMPLEMENTATION.md`,
después autorizar validación interna integral y finalmente SQL TEST. No invertir
estas etapas para compensar huecos de implementación.
