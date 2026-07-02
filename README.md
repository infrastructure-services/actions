# Get DB Secrets

Composite Action para generar los nombres de secrets Owner de base de datos a partir del `APP_ID` de una aplicación.

Este action no consulta Azure Key Vault ni obtiene valores sensibles. Solo construye los nombres esperados de los secrets para los ambientes `TEST`, `QA` y `PROD`.

---

## Descripción

El action recibe un `APP_ID` y genera automáticamente los nombres de los secrets Owner usados para obtener la connection string de cada ambiente.

El formato generado es:

```text
<APP_ID>-<AMBIENTE><OWNER_SECRET_SUFFIX>
```

Por defecto, el sufijo utilizado es:

```text
--DataAccessRegistry--Owner
```

Por ejemplo, si el `APP_ID` es:

```text
3170
```

el action genera:

```text
3170-TEST--DataAccessRegistry--Owner
3170-QA--DataAccessRegistry--Owner
3170-PROD--DataAccessRegistry--Owner
```

---

## Uso básico

```yaml
- name: Generar nombres de secrets DB
  id: get_db_secrets
  uses: infrastructure-services/actions@get-db-secrets
  with:
    app-id: ${{ vars.APP_ID }}
```

---

## Uso con sufijo custom

```yaml
- name: Generar nombres de secrets DB
  id: get_db_secrets
  uses: infrastructure-services/actions@get-db-secrets
  with:
    app-id: ${{ vars.APP_ID }}
    owner-secret-suffix: "--DataAccessRegistry--Owner"
```

---

## Inputs

| Input                 | Requerido | Default                       | Descripción                                                                       |
| --------------------- | --------: | ----------------------------- | --------------------------------------------------------------------------------- |
| `app-id`              |        Sí | -                             | APP_ID de la aplicación. Se usa como prefijo para generar los nombres de secrets. |
| `owner-secret-suffix` |        No | `--DataAccessRegistry--Owner` | Sufijo usado para identificar el secret Owner.                                    |

---

## Outputs

| Output               | Descripción                                         |
| -------------------- | --------------------------------------------------- |
| `test-owner-secret`  | Nombre del secret Owner para el ambiente `TEST`.    |
| `qa-owner-secret`    | Nombre del secret Owner para el ambiente `QA`.      |
| `prod-owner-secret`  | Nombre del secret Owner para el ambiente `PROD`.    |
| `owner-secrets-json` | JSON con los nombres de secrets Owner por ambiente. |

---

## Ejemplo de outputs generados

Con este input:

```yaml
with:
  app-id: 3170
```

El action devuelve:

```text
test-owner-secret=3170-TEST--DataAccessRegistry--Owner
qa-owner-secret=3170-QA--DataAccessRegistry--Owner
prod-owner-secret=3170-PROD--DataAccessRegistry--Owner
owner-secrets-json={"TEST":"3170-TEST--DataAccessRegistry--Owner","QA":"3170-QA--DataAccessRegistry--Owner","PROD":"3170-PROD--DataAccessRegistry--Owner"}
```

---

## Ejemplo de uso de outputs

```yaml
- name: Mostrar secrets generados
  run: |
    echo "TEST: ${{ steps.get_db_secrets.outputs.test-owner-secret }}"
    echo "QA: ${{ steps.get_db_secrets.outputs.qa-owner-secret }}"
    echo "PROD: ${{ steps.get_db_secrets.outputs.prod-owner-secret }}"
    echo "JSON: ${{ steps.get_db_secrets.outputs.owner-secrets-json }}"
```
---

## Formato de nombres generados

El action genera los nombres con este patrón:

```text
<APP_ID>-TEST<OWNER_SECRET_SUFFIX>
<APP_ID>-QA<OWNER_SECRET_SUFFIX>
<APP_ID>-PROD<OWNER_SECRET_SUFFIX>
```

Con el sufijo default:

```text
--DataAccessRegistry--Owner
```

el resultado queda:

```text
<APP_ID>-TEST--DataAccessRegistry--Owner
<APP_ID>-QA--DataAccessRegistry--Owner
<APP_ID>-PROD--DataAccessRegistry--Owner
```

---

## JSON generado

Además de los outputs individuales, el action genera un JSON con los tres ambientes:

```json
{
  "TEST": "3170-TEST--DataAccessRegistry--Owner",
  "QA": "3170-QA--DataAccessRegistry--Owner",
  "PROD": "3170-PROD--DataAccessRegistry--Owner"
}
```

Este output puede servir para debug, trazabilidad o para consumir los nombres de secrets de forma dinámica.

---

## Validaciones

El action valida que:

* `app-id` no esté vacío.
* `owner-secret-suffix` no esté vacío.
* `app-id` contenga solo letras, números y guiones.
* `owner-secret-suffix` contenga solo letras, números y guiones.

Valores válidos:

```text
3170
infraops-api
my-app-123
--DataAccessRegistry--Owner
```

Valores inválidos:

```text
app_id
app/id
app.id
app id
```

---

## Casos en los que falla

El action falla si:

* No se informa `app-id`.
* No se informa `owner-secret-suffix`.
* `app-id` tiene caracteres inválidos.
* `owner-secret-suffix` tiene caracteres inválidos.

Ejemplo de error:

```text
❌ APP_ID inválido: app_id
Solo se permiten letras, números y guiones.
```

---

## Summary en GitHub Actions

El action escribe un resumen en `GITHUB_STEP_SUMMARY` con los secrets generados:

| Ambiente | Secret Owner                               |
| -------- | ------------------------------------------ |
| TEST     | `<APP_ID>-TEST--DataAccessRegistry--Owner` |
| QA       | `<APP_ID>-QA--DataAccessRegistry--Owner`   |
| PROD     | `<APP_ID>-PROD--DataAccessRegistry--Owner` |

---

## Requisitos del runner

El runner solo necesita:

* `bash`

No requiere:

* Azure CLI
* `curl`
* `jq`
* `.NET SDK`
* Acceso a Azure Key Vault

---

## Consideraciones importantes

* Este action no obtiene el valor de los secrets.
* Este action no valida si los secrets existen en Azure Key Vault.
* Este action no valida conexión a la base de datos.
* Este action no aplica migraciones.
* Solo genera nombres de secrets siguiendo una convención.
* Para obtener el valor real del secret, se debe usar `get-keyvault-secret`.
* Para validar la connection string, se debe usar `validate-connection`.
* Para validar migraciones, se debe usar `validate-migrations`.
* Para aplicar migraciones, se debe usar `apply-db-migrations`.
