# Resolve DB Migration Risk

Composite Action para resolver el resultado final de una validación de migraciones EF Core contra SQL Server.

Este action centraliza la decisión final del flujo de migraciones: determina si hay migraciones pendientes, cuál es el riesgo final, si requiere aprobación DBA y si corresponde ejecutar el action de aplicación de migraciones.

---

## Descripción

Este action se usa después de ejecutar los pasos de:

1. Obtención del secret desde Azure Key Vault.
2. Validación de conexión SQL Server.
3. Validación de migraciones EF Core.

Con esos resultados, el action decide:

* Si el secret existe.
* Si la conexión SQL fue válida.
* Si la validación EF Core fue correcta.
* Si existen migraciones pendientes.
* Qué riesgo final tienen las migraciones.
* Si requieren aprobación DBA.
* Si se debe ejecutar `apply-db-migrations`.

---

## Uso básico

```yaml
- name: Resolver riesgo de migración TEST
  id: resolve_migration_risk_test
  uses: infrastructure-services/actions@resolve-db-migration-risk
  with:
    environment-name: ${{ vars.ENVIRONMENT_NAME }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}
    keyvault-outcome: ${{ steps.get_connection_string_test.outcome }}
    secret-found: ${{ steps.get_connection_string_test.outputs.secret-found }}
    keyvault-error-code: ${{ steps.get_connection_string_test.outputs.error-code }}
    sql-outcome: ${{ steps.validate_connection_test.outcome }}
    ef-outcome: ${{ steps.validate_migrations_test.outcome }}
    has-pending-db-migrations: ${{ steps.validate_migrations_test.outputs.has-pending-db-migrations }}
    detected-migration-risk: ${{ steps.validate_migrations_test.outputs.migration-risk }}
```

---

## Inputs

| Input                       | Requerido | Default   | Descripción                                                                                             |
| --------------------------- | --------: | --------- | ------------------------------------------------------------------------------------------------------- |
| `environment-name`          |        Sí | -         | Nombre del ambiente validado. Ejemplo: `TEST`, `QA` o `PROD`.                                           |
| `secret-name`               |        Sí | -         | Nombre del secret consultado en Azure Key Vault.                                                        |
| `keyvault-outcome`          |        Sí | -         | Resultado del step que obtiene el secret desde Key Vault. Normalmente se pasa con `steps.<id>.outcome`. |
| `secret-found`              |        No | `false`   | Indica si el secret existe en Key Vault.                                                                |
| `keyvault-error-code`       |        No | `""`      | Código de error devuelto por el action que consulta Key Vault.                                          |
| `sql-outcome`               |        No | `skipped` | Resultado del step de validación de conexión SQL Server.                                                |
| `ef-outcome`                |        No | `skipped` | Resultado del step de validación de migraciones EF Core.                                                |
| `has-pending-db-migrations` |        No | `false`   | Output del action de validación EF Core que indica si hay migraciones pendientes.                       |
| `detected-migration-risk`   |        No | `NONE`    | Riesgo detectado por el action de validación EF Core. Valores válidos: `NONE`, `LOW`, `MEDIUM`, `HIGH`. |

---

## Outputs

| Output                    | Descripción                                                                   |
| ------------------------- | ----------------------------------------------------------------------------- |
| `has-migrations`          | Indica si existen migraciones pendientes.                                     |
| `migration-risk`          | Riesgo final de migración. Valores posibles: `NONE`, `LOW`, `MEDIUM`, `HIGH`. |
| `requires-dba`            | Indica si se requiere aprobación DBA.                                         |
| `should-apply-migrations` | Indica si corresponde ejecutar el action `apply-db-migrations`.               |

Ejemplo de uso:

```yaml
- name: Mostrar resultado final
  run: |
    echo "Tiene migraciones: ${{ steps.resolve_migration_risk_test.outputs.has-migrations }}"
    echo "Riesgo: ${{ steps.resolve_migration_risk_test.outputs.migration-risk }}"
    echo "Requiere DBA: ${{ steps.resolve_migration_risk_test.outputs.requires-dba }}"
    echo "Debe aplicar migraciones: ${{ steps.resolve_migration_risk_test.outputs.should-apply-migrations }}"
```

---

## Lógica de decisión

### 1. Fallo real en Key Vault

Si `keyvault-outcome` es distinto de `success`, el action falla.

```text
keyvault-outcome != success
```

Resultado:

```text
El action corta el workflow.
```

Motivo:

```text
No se pudo determinar correctamente el estado del secret.
```

---

### 2. Secret no encontrado

Si Key Vault respondió correctamente, pero el secret no existe:

```text
keyvault-outcome = success
secret-found != true
```

El action no falla.

Devuelve:

```text
has-migrations=false
migration-risk=NONE
requires-dba=false
should-apply-migrations=false
```

Resultado informado en el summary:

```text
SKIPPED_SECRET_NOT_FOUND
```

Este caso permite continuar el flujo sin aplicar migraciones cuando el ambiente no tiene secret configurado.

---

### 3. Fallo en validación SQL

Si el secret existe, pero la validación SQL no fue exitosa:

```text
secret-found=true
sql-outcome != success
```

Resultado:

```text
El action falla.
```

Motivo:

```text
El secret existe, pero no se pudo validar la conexión SQL.
```

Esto evita continuar con un despliegue sin haber validado correctamente la base de datos.

---

### 4. Fallo en validación EF Core

Si el secret existe, SQL conecta, pero la validación EF Core falla:

```text
secret-found=true
sql-outcome=success
ef-outcome != success
```

Resultado:

```text
El action falla.
```

Motivo:

```text
No se pudo validar correctamente el estado de migraciones EF Core.
```

---

### 5. Sin migraciones pendientes

Si la validación EF Core fue exitosa, pero no hay migraciones pendientes:

```text
has-pending-db-migrations=false
```

El action fuerza el riesgo final a:

```text
migration-risk=NONE
```

Y devuelve:

```text
has-migrations=false
requires-dba=false
should-apply-migrations=false
```

---

### 6. Con migraciones pendientes

Si existen migraciones pendientes:

```text
has-pending-db-migrations=true
```

El action toma el riesgo recibido desde:

```text
detected-migration-risk
```

El valor debe ser uno de:

```text
NONE
LOW
MEDIUM
HIGH
```

Si recibe un riesgo inválido, el action falla.

---

## Relación entre riesgo, DBA y aplicación

| Riesgo final | Requiere DBA | Aplica migraciones |
| ------------ | -----------: | -----------------: |
| `NONE`       |      `false` |            `false` |
| `LOW`        |      `false` |             `true` |
| `MEDIUM`     |       `true` |             `true` |
| `HIGH`       |       `true` |             `true` |

---

## Casos de resultado

### Secret no encontrado

```text
has-migrations=false
migration-risk=NONE
requires-dba=false
should-apply-migrations=false
```

---

### Sin migraciones pendientes

```text
has-migrations=false
migration-risk=NONE
requires-dba=false
should-apply-migrations=false
```

---

### Migraciones de bajo riesgo

```text
has-migrations=true
migration-risk=LOW
requires-dba=false
should-apply-migrations=true
```

---

### Migraciones de riesgo medio

```text
has-migrations=true
migration-risk=MEDIUM
requires-dba=true
should-apply-migrations=true
```

---

### Migraciones de alto riesgo

```text
has-migrations=true
migration-risk=HIGH
requires-dba=true
should-apply-migrations=true
```

---

## Ejemplo de uso con aprobación DBA

El output `requires-dba` puede usarse para condicionar un job de aprobación.

```yaml
Aprobacion_DBA_TEST:
  name: Aprobación DBA TEST
  runs-on: ubuntu-latest
  needs:
    - Validacion_migracion_test
  if: ${{ needs.Validacion_migracion_test.outputs.requires_dba == 'true' }}
  environment: DBA-Approval

  steps:
    - name: Esperando aprobación DBA
      run: echo "Migración requiere aprobación DBA"
```

---

## Ejemplo de uso con apply-db-migrations

El output `should-apply-migrations` puede usarse para ejecutar migraciones solamente cuando corresponde.

```yaml
- name: Ejecutar Migraciones SQL Server TEST
  id: apply_migrations_test
  if: ${{ needs.Validacion_migracion_test.outputs.should_apply_migrations == 'true' }}
  uses: infrastructure-services/actions@apply-db-migrations
  with:
    migration-risk: ${{ needs.Validacion_migracion_test.outputs.migration_risk }}
    keyvault-name: ${{ secrets.KV_NAME_DBA }}
    client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
    client-secret: ${{ secrets.KV_SECRET_DBA }}
    tenant-id: ${{ secrets.KV_TENANT_DBA }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}
    startup-project: ${{ needs.Preparativos.outputs.startupProject }}
    environment-name: ${{ vars.ENVIRONMENT_NAME }}
    nuget-username: ${{ secrets.ARQUITECTURA_USER }}
    nuget-token: ${{ secrets.ARQUITECTURA_DEPLOY }}
```

---

## Summary en GitHub Actions

El action escribe un resumen en `GITHUB_STEP_SUMMARY` con esta información:

| Campo              | Descripción                                       |
| ------------------ | ------------------------------------------------- |
| Ambiente           | Ambiente evaluado.                                |
| Secret             | Nombre del secret consultado.                     |
| Key Vault outcome  | Resultado del step de Key Vault.                  |
| Secret encontrado  | Indica si el secret existe.                       |
| Error Key Vault    | Código de error de Key Vault, si existe.          |
| SQL connection     | Resultado del step de validación SQL.             |
| EF validation      | Resultado del step de validación EF Core.         |
| Tiene migraciones  | Resultado final sobre migraciones pendientes.     |
| Riesgo             | Riesgo final resuelto.                            |
| Requiere DBA       | Indica si debe aprobar DBA.                       |
| Aplica migraciones | Indica si se debe ejecutar `apply-db-migrations`. |
| Resultado          | `OK` o `SKIPPED_SECRET_NOT_FOUND`.                |

---

## Casos en los que falla

El action falla en estos escenarios:

* `keyvault-outcome` distinto de `success`.
* El secret existe, pero `sql-outcome` es distinto de `success`.
* El secret existe, SQL conecta, pero `ef-outcome` es distinto de `success`.
* El riesgo recibido no es válido.

Riesgos válidos:

```text
NONE
LOW
MEDIUM
HIGH
```

---

## Casos en los que no falla

El action no falla cuando el secret no existe en Key Vault.

En ese caso devuelve:

```text
has-migrations=false
migration-risk=NONE
requires-dba=false
should-apply-migrations=false
```

Esto permite que el pipeline continúe sin validar ni aplicar migraciones para ese ambiente.

---

## Consideraciones importantes

* Este action no consulta Key Vault.
* Este action no valida la conexión SQL.
* Este action no valida migraciones EF Core.
* Este action no aplica migraciones.
* Su función es resolver la decisión final usando los resultados de otros actions.
* Para que funcione correctamente, debe recibir los `outcome` y outputs de los steps previos.
* `requires-dba=true` solamente para riesgos `MEDIUM` o `HIGH`.
* `should-apply-migrations=true` solamente cuando el riesgo final es `LOW`, `MEDIUM` o `HIGH`.
* Si no hay migraciones pendientes, el riesgo final se fuerza a `NONE`.
