# Get Azure Key Vault Secret

Composite Action para obtener el valor de un secret desde Azure Key Vault usando la API REST de Azure.

Este action no requiere Azure CLI. Se autentica con un Service Principal, obtiene un access token desde Azure AD y consulta directamente el secret en Key Vault mediante `curl`.

---

## Descripción

El action permite recuperar un secret desde Azure Key Vault y exponerlo como output para usarlo en otros steps del workflow.

Está pensado para flujos donde se necesita obtener una connection string, una credencial o cualquier valor sensible almacenado en Key Vault.

El flujo principal es:

1. Valida los inputs obligatorios.
2. Valida que el runner tenga instalados `curl` y `jq`.
3. Solicita un token OAuth2 a Azure AD usando `client_credentials`.
4. Consulta el secret en Azure Key Vault usando REST API.
5. Enmascara el `client-secret`, el access token y el valor del secret en logs.
6. Devuelve:

   * El valor del secret.
   * Si el secret fue encontrado.
   * Un código de error controlado, si aplica.
7. Escribe un resumen seguro en `GITHUB_STEP_SUMMARY`.

---

## Uso básico

```yaml
- name: Obtener secret desde Key Vault
  id: get_keyvault_secret
  uses: infrastructure-services/actions@get-keyvault-secret
  with:
    keyvault-name: ${{ secrets.KV_NAME_DBA }}
    client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
    client-secret: ${{ secrets.KV_SECRET_DBA }}
    tenant-id: ${{ secrets.KV_TENANT_DBA }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}
```

---

## Uso recomendado para connection string

Ejemplo para obtener una connection string y luego validarla:

```yaml
- name: Obtener connection string TEST
  id: get_connection_string_test
  uses: infrastructure-services/actions@get-keyvault-secret
  with:
    keyvault-name: ${{ secrets.KV_NAME_DBA }}
    client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
    client-secret: ${{ secrets.KV_SECRET_DBA }}
    tenant-id: ${{ secrets.KV_TENANT_DBA }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}

- name: Validar conexión SQL Server TEST
  id: validate_connection_test
  if: ${{ steps.get_connection_string_test.outputs.secret-found == 'true' }}
  uses: infrastructure-services/actions@validate-connection
  with:
    connection-string: ${{ steps.get_connection_string_test.outputs.secret-value }}
    dotnet-version: "10.0.x"
    test-framework: "net10.0"
    connection-timeout: "15"
```

---

## Inputs

| Input                  | Requerido | Default      | Descripción                                                               |
| ---------------------- | --------: | ------------ | ------------------------------------------------------------------------- |
| `keyvault-name`        |        Sí | -            | Nombre del Azure Key Vault. No debe incluir la URL completa.              |
| `secret-name`          |        Sí | -            | Nombre del secret dentro del Key Vault.                                   |
| `client-id`            |        Sí | -            | Client ID del Service Principal de Azure.                                 |
| `client-secret`        |        Sí | -            | Client Secret del Service Principal de Azure.                             |
| `tenant-id`            |        Sí | -            | Tenant ID de Azure.                                                       |
| `keyvault-api-version` |        No | `2025-07-01` | Versión de la API REST de Azure Key Vault usada para consultar el secret. |

---

## Outputs

| Output         | Descripción                                                         |
| -------------- | ------------------------------------------------------------------- |
| `secret-value` | Valor del secret obtenido desde Azure Key Vault.                    |
| `secret-found` | Indica si el secret existe en Key Vault. Valores: `true` o `false`. |
| `error-code`   | Código de error controlado. Vacío cuando la consulta fue exitosa.   |

Ejemplo de uso de outputs:

```yaml
- name: Mostrar resultado
  run: |
    echo "Secret encontrado: ${{ steps.get_connection_string_test.outputs.secret-found }}"
    echo "Error code: ${{ steps.get_connection_string_test.outputs.error-code }}"
```

> No se recomienda imprimir `secret-value`, ya que contiene información sensible. El action lo enmascara en logs, pero debe tratarse como secreto.

---

## Autenticación contra Azure

El action obtiene un token usando el flujo `client_credentials`.

Endpoint utilizado:

```text
https://login.microsoftonline.com/<TENANT_ID>/oauth2/v2.0/token
```

Scope utilizado:

```text
https://vault.azure.net/.default
```

Para que funcione, el Service Principal debe tener permisos para leer secrets en el Key Vault.

Permiso requerido:

```text
secrets/get
```

---

## Consulta al Key Vault

Una vez obtenido el token, el action consulta el secret usando la API REST de Key Vault.

Formato de URL:

```text
https://<KEYVAULT_NAME>.vault.azure.net/secrets/<SECRET_NAME>?api-version=<KEYVAULT_API_VERSION>
```

Ejemplo:

```text
https://mi-keyvault.vault.azure.net/secrets/sql-owner-test?api-version=2025-07-01
```

---

## Manejo de secrets y logs

El action enmascara automáticamente:

* `client-secret`
* `access_token`
* `secret-value`

Esto evita que esos valores aparezcan expuestos en los logs de GitHub Actions.

Además, escribe el valor del secret como output multilínea, por lo que soporta valores largos o connection strings complejas.

---

## Manejo de secret inexistente

Si Key Vault responde `404 Not Found`, el action no falla.

En ese caso devuelve:

```text
secret-found=false
error-code=SecretNotFound
secret-value=
```

Esto permite que el workflow continúe y que los steps consumidores decidan saltear validaciones o migraciones.

Ejemplo:

```yaml
- name: Validar conexión SQL Server TEST
  if: ${{ steps.get_connection_string_test.outputs.secret-found == 'true' }}
  uses: infrastructure-services/actions@validate-connection
  with:
    connection-string: ${{ steps.get_connection_string_test.outputs.secret-value }}
```

---

## Códigos de error controlados

| Código             | Descripción                            | Falla el action |
| ------------------ | -------------------------------------- | --------------: |
| `SecretNotFound`   | El secret no existe en el Key Vault.   |              No |
| `EmptySecretValue` | El secret existe, pero no tiene valor. |              Sí |

Cuando la consulta es exitosa:

```text
error-code=
```

---

## Errores que hacen fallar el action

El action falla en cualquiera de estos casos:

* `keyvault-name` vacío.
* `secret-name` vacío.
* `client-id` vacío.
* `client-secret` vacío.
* `tenant-id` vacío.
* `keyvault-api-version` vacío.
* `curl` no está instalado en el runner.
* `jq` no está instalado en el runner.
* Error de red al obtener el token.
* Azure no devuelve un token válido.
* Key Vault responde `401 Unauthorized`.
* Key Vault responde `403 Forbidden`.
* Key Vault responde un HTTP distinto de `2xx` o `404`.
* Key Vault devuelve una respuesta que no es JSON válido.
* El secret existe, pero el campo `value` está vacío.

---

## Diagnóstico de errores comunes

### `401 Unauthorized`

Indica que Azure rechazó la autenticación.

Posibles causas:

* `tenant-id` incorrecto.
* `client-id` incorrecto.
* `client-secret` incorrecto.
* El Service Principal no existe en ese tenant.

---

### `403 Forbidden`

Indica que el Service Principal autenticó correctamente, pero no tiene permisos para leer el secret.

Posibles causas:

* Falta permiso `secrets/get`.
* El Key Vault usa RBAC y el Service Principal no tiene rol adecuado.
* El Key Vault usa Access Policies y el Service Principal no está agregado.
* Restricciones de red del Key Vault impiden el acceso.

---

### `404 Not Found`

Indica que el secret no existe en el Key Vault informado.

En este caso el action no falla y devuelve:

```text
secret-found=false
error-code=SecretNotFound
```

---

### `EmptySecretValue`

Indica que el secret existe, pero no tiene valor.

En este caso el action falla porque no hay un valor válido para entregar a los steps siguientes.

---

## Summary en GitHub Actions

El action escribe un resumen seguro en `GITHUB_STEP_SUMMARY`.

Incluye:

| Campo        | Descripción                          |
| ------------ | ------------------------------------ |
| Key Vault    | Nombre del Key Vault consultado.     |
| Secret name  | Nombre del secret consultado.        |
| Secret found | Indica si el secret fue encontrado.  |
| Error code   | Código de error controlado o `none`. |

El summary no muestra el valor del secret.

---

## Requisitos del runner

El runner debe tener instalado:

* `bash`
* `curl`
* `jq`

No requiere:

* Azure CLI
* PowerShell
* Login previo con `azure/login`

---

## Consideraciones importantes

* Este action solo obtiene secrets desde Azure Key Vault.
* No valida que el valor del secret sea una connection string válida.
* No prueba conectividad contra la base de datos.
* No aplica migraciones.
* Si el secret no existe, no falla; devuelve `secret-found=false`.
* Si el secret existe pero está vacío, falla.
* El valor del secret se enmascara en logs.
* El Service Principal debe tener permisos `secrets/get` sobre el Key Vault.
