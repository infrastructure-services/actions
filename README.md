# Validate SQL Server Connection

Composite Action para validar una connection string de SQL Server desde GitHub Actions.

Este action valida que el runner tenga instalado el SDK de .NET esperado, normaliza la connection string, prueba conectividad TCP contra el servidor SQL y finalmente intenta abrir una conexión real a la base ejecutando consultas simples.

---

## Descripción

El action permite validar de forma temprana si un runner puede conectarse correctamente a una base SQL Server.

El flujo principal es:

1. Valida que `dotnet` esté instalado en el runner.
2. Valida que exista el major version esperado del SDK de .NET.
3. Recibe una connection string de SQL Server.
4. Enmascara la connection string en los logs.
5. Normaliza la connection string agregando valores necesarios si no existen:

   * `Encrypt=True`
   * `TrustServerCertificate=True`
   * `Connection Timeout=<valor>`
6. Crea un proyecto temporal de consola en .NET.
7. Agrega el paquete `Microsoft.Data.SqlClient`.
8. Parsea la connection string.
9. Detecta:

   * `DataSource`
   * `Database`
   * `Encrypt`
   * `TrustServerCertificate`
   * `ConnectionTimeout`
10. Valida conectividad TCP contra el host y puerto SQL.
11. Abre una conexión real a SQL Server.
12. Ejecuta:

* `SELECT DB_NAME()`
* `SELECT 1`

13. Publica outputs con la connection string normalizada, el datasource y la base detectada.
14. Agrega un resumen seguro en `GITHUB_STEP_SUMMARY`.

---

## Uso básico

```yaml
- name: Validar conexión SQL Server
  id: validate_connection
  uses: infrastructure-services/actions@validate-connection
  with:
    connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
```

---

## Uso recomendado

Ejemplo usando una connection string recuperada previamente desde Azure Key Vault:

```yaml
- name: Obtener connection string
  id: get_connection_string
  uses: infrastructure-services/actions@get-keyvault-secret
  with:
    keyvault-name: ${{ secrets.KV_NAME_DBA }}
    client-id: ${{ secrets.KV_CLIENT_ID_DBA }}
    client-secret: ${{ secrets.KV_SECRET_DBA }}
    tenant-id: ${{ secrets.KV_TENANT_DBA }}
    secret-name: ${{ needs.Preparativos.outputs.testOwnerSecret }}

- name: Validar conexión SQL Server TEST
  id: validate_connection_test
  uses: infrastructure-services/actions@validate-connection
  with:
    connection-string: ${{ steps.get_connection_string.outputs.secret-value }}
    dotnet-version: "10.0.x"
    test-framework: "net10.0"
    connection-timeout: "15"
```

---

## Inputs

| Input                | Requerido | Default   | Descripción                                                                                 |
| -------------------- | --------: | --------- | ------------------------------------------------------------------------------------------- |
| `connection-string`  |        Sí | -         | Connection string de SQL Server a validar.                                                  |
| `dotnet-version`     |        No | `10.0.x`  | Versión esperada del SDK de .NET instalado en el runner. El action valida el major version. |
| `test-framework`     |        No | `net10.0` | Framework usado por el proyecto temporal creado para validar la conexión.                   |
| `connection-timeout` |        No | `15`      | Timeout de conexión SQL en segundos. Se agrega a la connection string si no está definido.  |

---

## Outputs

| Output                         | Descripción                                                                        |
| ------------------------------ | ---------------------------------------------------------------------------------- |
| `normalized-connection-string` | Connection string normalizada con los valores requeridos agregados si no existían. |
| `data-source`                  | Valor de `DataSource` detectado desde la connection string.                        |
| `database`                     | Nombre de la base detectada desde la connection string.                            |

Ejemplo de uso:

```yaml
- name: Mostrar datos detectados
  run: |
    echo "DataSource: ${{ steps.validate_connection_test.outputs.data-source }}"
    echo "Database: ${{ steps.validate_connection_test.outputs.database }}"
```

> No se recomienda imprimir `normalized-connection-string`, ya que contiene credenciales de conexión. El action la enmascara en logs, pero debe tratarse como dato sensible.

---

## Validación de .NET SDK

Antes de validar la conexión, el action verifica que `dotnet` esté instalado en el runner.

Ejecuta:

```bash
dotnet --info
dotnet --list-sdks
```

Luego toma el major version configurado en `dotnet-version`.

Por ejemplo, si se configura:

```yaml
dotnet-version: "10.0.x"
```

el action valida que exista algún SDK instalado que empiece con:

```text
10.
```

Si no encuentra una versión compatible, falla con error.

---

## Normalización de connection string

El action toma la connection string original y agrega configuraciones necesarias si no existen.

Si no encuentra `Encrypt`, agrega:

```text
Encrypt=True
```

Si no encuentra `TrustServerCertificate`, agrega:

```text
TrustServerCertificate=True
```

Si no encuentra `Connection Timeout`, agrega:

```text
Connection Timeout=<connection-timeout>
```

Ejemplo:

```text
Server=tcp:mi-servidor,1433;Database=MiBase;User Id=user;Password=pass
```

Se normaliza como:

```text
Server=tcp:mi-servidor,1433;Database=MiBase;User Id=user;Password=pass;Encrypt=True;TrustServerCertificate=True;Connection Timeout=15
```

---

## Validación de formato

El action usa `SqlConnectionStringBuilder` para validar que la connection string tenga un formato correcto.

Además, valida que existan estos datos mínimos:

| Campo                          | Requerido |
| ------------------------------ | --------- |
| `DataSource`                   | Sí        |
| `Database` / `Initial Catalog` | Sí        |

Si falta alguno, el action falla.

---

## Validación TCP

Antes de abrir la conexión SQL, el action valida conectividad TCP hacia el servidor.

Detecta el host y puerto a partir de `DataSource`.

Soporta formato con puerto:

```text
Server=tcp:mi-servidor.database.windows.net,1433
```

En ese caso detecta:

```text
TCP Host: mi-servidor.database.windows.net
TCP Port: 1433
```

Si no se informa puerto, usa por defecto:

```text
1433
```

La validación TCP usa un timeout interno de 10 segundos.

Si no puede conectarse por TCP, el action falla indicando posibles causas:

* Problema de red.
* Firewall.
* DNS.
* Ruta no disponible.
* Puerto cerrado desde el runner.

---

## Consideración para instancias SQL con backslash

Si el `DataSource` usa una instancia SQL con backslash, por ejemplo:

```text
Server=mi-servidor\SQLEXPRESS
```

el action muestra una advertencia:

```text
En runners Linux es mejor usar host,puerto.
Ejemplo: Server=tcp:mi-servidor,1433;Database=...
```

Para validar TCP, toma solamente el host antes del backslash.

---

## Validación SQL Server

Una vez validada la conectividad TCP, el action intenta abrir una conexión real a SQL Server usando `Microsoft.Data.SqlClient`.

Luego ejecuta:

```sql
SELECT DB_NAME()
```

y:

```sql
SELECT 1
```

Si ambas consultas responden correctamente, la conexión se considera válida.

---

## Errores diagnosticados

### Error SQL 258

Si SQL Server devuelve error `258`, el action muestra un diagnóstico probable:

```text
- El runner no llega al SQL Server.
- El DNS no resuelve desde el runner.
- El firewall no permite el puerto SQL.
- La base está en una red privada no accesible desde este grupo de runners.
```

Este error suele estar relacionado con conectividad, red privada, firewall o timeout.

---

### Error SQL 18456

Si SQL Server devuelve error `18456`, el action muestra un diagnóstico probable:

```text
- Usuario o password incorrectos.
- El login no tiene permisos sobre la base.
```

Este error suele estar relacionado con autenticación o permisos.

---

## Resumen en GitHub Actions

El action escribe un resumen en `GITHUB_STEP_SUMMARY` con información segura:

| Campo        | Valor              |
| ------------ | ------------------ |
| `DataSource` | Servidor detectado |
| `Database`   | Base detectada     |

No escribe la connection string completa en el resumen.

---

## Ejemplo dentro de un job de validación

```yaml
jobs:
  Validar_conexion_test:
    name: Validar conexión SQL Server TEST
    runs-on: ubuntu-latest
    needs:
      - Preparativos

    outputs:
      data_source: ${{ steps.validate_connection_test.outputs.data-source }}
      database: ${{ steps.validate_connection_test.outputs.database }}

    steps:
      - name: Checkout
        uses: actions/checkout@v4

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
        uses: infrastructure-services/actions@validate-connection
        with:
          connection-string: ${{ steps.get_connection_string_test.outputs.secret-value }}
          dotnet-version: "10.0.x"
          test-framework: "net10.0"
          connection-timeout: "15"
```

---

## Casos en los que falla

El action falla en cualquiera de estos casos:

* `connection-string` vacía.
* `dotnet` no está instalado en el runner.
* No existe el SDK de .NET esperado.
* La connection string tiene formato inválido.
* La connection string no tiene `Server` / `Data Source`.
* La connection string no tiene `Database` / `Initial Catalog`.
* No se puede abrir conexión TCP al host y puerto detectados.
* No se puede abrir conexión SQL Server.
* Falla `SELECT DB_NAME()`.
* Falla `SELECT 1`.
* Error de autenticación SQL.
* Timeout de conexión.
* Base inaccesible desde el runner.

---

## Requisitos del runner

El runner debe tener:

* `bash`
* `.NET SDK` instalado
* Acceso a internet o al feed necesario para restaurar `Microsoft.Data.SqlClient`
* Acceso de red al SQL Server
* Resolución DNS hacia el servidor
* Puerto SQL habilitado, usualmente `1433`

---

## Consideraciones importantes

* Este action no obtiene secrets desde Key Vault; espera recibir la connection string por input.
* La connection string se enmascara en los logs.
* El output `normalized-connection-string` contiene credenciales y debe tratarse como dato sensible.
* El action crea un proyecto temporal y lo elimina al finalizar.
* No modifica la base de datos.
* Solo ejecuta consultas de lectura simples para validar la conexión.
* Es útil como paso previo antes de validar o aplicar migraciones EF Core.
