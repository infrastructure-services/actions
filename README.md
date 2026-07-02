# Detect .NET Startup Project

Composite Action para detectar automáticamente el proyecto startup de una aplicación .NET leyendo el `Dockerfile`.

El action busca la línea `dotnet publish` dentro del Dockerfile, extrae el `.csproj` utilizado para publicar la aplicación y devuelve la ruta real del proyecto dentro del repositorio.

---

## Descripción

Este action permite resolver dinámicamente el `startup-project` de una aplicación .NET sin tener que configurarlo como variable del repositorio.

Es útil para workflows que necesitan ejecutar comandos de Entity Framework Core, validaciones de migraciones o procesos de build que requieren conocer el `.csproj` principal de la aplicación.

El flujo principal es:

1. Recibe la ruta del `Dockerfile`.
2. Busca una línea que contenga:

```bash
dotnet publish <archivo>.csproj
```

3. Extrae el nombre del `.csproj`.
4. Intenta resolver la ruta real del proyecto dentro del repositorio.
5. Si no encuentra una ruta directa, usa el `WORKDIR` previo al `dotnet publish`.
6. Si aún no puede resolverlo, busca el `.csproj` por nombre en todo el repositorio.
7. Si hay múltiples coincidencias, prioriza proyectos ubicados en rutas tipo `Api`.
8. Devuelve el `startup-project` y el nombre del `.csproj` como outputs.

---

## Uso básico

```yaml
- name: Detectar startup project
  id: detect_startup_project
  uses: infrastructure-services/actions@detect-startup-project
```

Luego se puede usar el output:

```yaml
startup-project: ${{ steps.detect_startup_project.outputs.startup-project }}
```

---

## Uso con Dockerfile custom

Si el Dockerfile no está en la raíz del repositorio, se puede informar la ruta:

```yaml
- name: Detectar startup project
  id: detect_startup_project
  uses: infrastructure-services/actions@detect-startup-project
  with:
    dockerfile: ./src/Api/Dockerfile
```

---

## Inputs

| Input        | Requerido | Default      | Descripción                                                 |
| ------------ | --------: | ------------ | ----------------------------------------------------------- |
| `dockerfile` |        No | `Dockerfile` | Ruta del Dockerfile que contiene la línea `dotnet publish`. |

---

## Outputs

| Output            | Descripción                                                  |
| ----------------- | ------------------------------------------------------------ |
| `startup-project` | Ruta del `.csproj` startup detectado dentro del repositorio. |
| `csproj-name`     | Nombre del archivo `.csproj` detectado desde el Dockerfile.  |

Ejemplo:

```yaml
- name: Mostrar startup project detectado
  run: |
    echo "Startup project: ${{ steps.detect_startup_project.outputs.startup-project }}"
    echo "CSProj name: ${{ steps.detect_startup_project.outputs.csproj-name }}"
```

---

## Formato esperado en el Dockerfile

El action espera encontrar una línea `dotnet publish` que incluya un archivo `.csproj`.

Ejemplo:

```dockerfile
RUN dotnet publish "LuanaApi.csproj" -c Release -o /app/publish
```

También soporta referencias con ruta relativa:

```dockerfile
RUN dotnet publish "./src/Api/LuanaApi.csproj" -c Release -o /app/publish
```

---

## Cómo resuelve el startup project

El action intenta detectar la ruta real del proyecto usando distintos criterios.

### 1. Ruta directa desde el Dockerfile

Si el `dotnet publish` ya tiene una ruta válida, la usa directamente.

Ejemplo:

```dockerfile
RUN dotnet publish "./src/Api/LuanaApi.csproj" -c Release -o /app/publish
```

Resultado:

```text
./src/Api/LuanaApi.csproj
```

---

### 2. Ruta relativa desde el repositorio

Si el Dockerfile contiene una referencia sin `./`, el action también prueba agregando el prefijo.

Ejemplo:

```dockerfile
RUN dotnet publish "src/Api/LuanaApi.csproj" -c Release -o /app/publish
```

Resultado:

```text
./src/Api/LuanaApi.csproj
```

---

### 3. Resolución usando `WORKDIR`

Si el `dotnet publish` solo contiene el nombre del `.csproj`, el action busca el último `WORKDIR` definido antes del publish.

Ejemplo:

```dockerfile
WORKDIR /src/src/Api
RUN dotnet publish "LuanaApi.csproj" -c Release -o /app/publish
```

El action intenta mapear ese `WORKDIR` a una ruta real del repositorio.

Resultado esperado:

```text
./src/Api/LuanaApi.csproj
```

Soporta mapeos habituales desde rutas Docker como:

```text
/src
/src/*
/source/*
/app/*
```

---

### 4. Búsqueda por nombre en todo el repositorio

Si no logra resolver la ruta con los pasos anteriores, busca el `.csproj` por nombre dentro del repositorio.

Excluye carpetas:

```text
bin/
obj/
```

Ejemplo:

```text
LuanaApi.csproj
```

Si encuentra una sola coincidencia, la usa como `startup-project`.

---

### 5. Múltiples coincidencias

Si encuentra varios proyectos con el mismo nombre, prioriza rutas que contengan:

```text
/src/Api/
/Api/
```

Si no puede elegir automáticamente, el action falla para evitar usar un proyecto incorrecto.

---

## Ejemplo de integración con Preparativos

Este action puede usarse en un job de `Preparativos` para exponer el `startupProject` al resto del workflow.

```yaml
jobs:
  Preparativos:
    name: Preparativos
    runs-on: ubuntu-latest

    outputs:
      startupProject: ${{ steps.detect_startup_project.outputs.startup-project }}
      csprojName: ${{ steps.detect_startup_project.outputs.csproj-name }}

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Detectar startup project
        id: detect_startup_project
        uses: infrastructure-services/actions@detect-startup-project
        with:
          dockerfile: Dockerfile
```

Luego, desde otro job:

```yaml
startup-project: ${{ needs.Preparativos.outputs.startupProject }}
```

---

## Casos en los que falla

El action falla en cualquiera de estos casos:

* No existe el Dockerfile informado.
* No encuentra una línea `dotnet publish` con `.csproj`.
* No puede extraer el `.csproj` desde la línea `dotnet publish`.
* No encuentra el `.csproj` dentro del repositorio.
* Encuentra múltiples proyectos con el mismo nombre y no puede elegir automáticamente.
* La ruta detectada no existe.

---

## Mensajes de diagnóstico

Si no existe el Dockerfile, muestra los Dockerfiles encontrados:

```text
Dockerfiles encontrados:
./Dockerfile
./src/Api/Dockerfile
```

Si no encuentra la línea `dotnet publish`, muestra el formato esperado:

```dockerfile
RUN dotnet publish "LuanaApi.csproj" -c Release -o /app/publish
```

Si no encuentra el `.csproj`, muestra los proyectos encontrados:

```text
Proyectos encontrados:
./src/Api/LuanaApi.csproj
./src/Infrastructure/Infrastructure.csproj
```

---

## Requisitos del repositorio

El repositorio debe tener:

* Un `Dockerfile` válido.
* Una línea `dotnet publish` con referencia a un `.csproj`.
* El archivo `.csproj` referenciado debe existir en el repositorio.
* El workflow debe ejecutar `actions/checkout` antes de usar este action.

Ejemplo:

```yaml
- name: Checkout
  uses: actions/checkout@v4

- name: Detectar startup project
  id: detect_startup_project
  uses: infrastructure-services/actions@detect-startup-project
```

---

## Consideraciones importantes

* Este action no compila el proyecto.
* No ejecuta migraciones.
* No valida si el `.csproj` es ejecutable.
* Solo detecta la ruta del proyecto startup a partir del Dockerfile.
* Es útil para evitar hardcodear rutas como `./src/Api/LuanaApi.csproj`.
* Si hay más de un proyecto con el mismo nombre, intenta priorizar rutas de API.
* Si no puede elegir de forma segura, falla para evitar falsos positivos.

---

## Resultado esperado

Para un Dockerfile como este:

```dockerfile
WORKDIR /src/src/Api
RUN dotnet publish "LuanaApi.csproj" -c Release -o /app/publish
```

y un repositorio con:

```text
./src/Api/LuanaApi.csproj
```

el action devuelve:

```text
startup-project=./src/Api/LuanaApi.csproj
csproj-name=LuanaApi.csproj
```
