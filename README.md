Actions para equipo DevOps

Para `Build Android`, configura la variable de repositorio `VERSION_CODE` con un entero no negativo. La action usa `VERSION_CODE + 1` para la build y guarda ese valor en la variable después de que Gradle compile correctamente. El workflow llamador debe conceder `permissions: actions: write` para que `GITHUB_TOKEN` pueda actualizar la variable. Si se ejecutan builds simultáneas, serialízalas con `concurrency` en el workflow llamador para evitar que dos builds usen el mismo código.
