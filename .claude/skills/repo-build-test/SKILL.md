---
name: repo-build-test
description: Compila AnimeLocalTracker y corre las pruebas de forma confiable y proporcional al cambio (pruebas afectadas mientras se trabaja, suite completa una vez al cerrar la tarea), evitando el falso "OK" del flake conocido del SDK WPF (_wpftmp). Usar tras cualquier cambio en C#/XAML antes de darlo por terminado.
---

# Build y verificación de pruebas — AnimeLocalTracker

Procedimiento para verificar la compilación y los tests tras cualquier cambio, sin caer en el falso positivo del flake de build conocido en este repo.

## Cuánto verificar (proporcional al cambio)

| Situación | Qué se hace |
|---|---|
| No se tocó C#/XAML (investigar, responder preguntas, documentos, reglas, `.gitignore`, workflows) | Nada: ni compilar ni probar. Si la investigación necesita ver la app en marcha, solo el paso 1; si es sobre una prueba que falla, solo esa prueba. |
| Mientras se trabaja en un cambio de C#/XAML | Paso 1 (compilar la app, 0 advertencias) y las pruebas de lo tocado (paso 4 con `--filter`). |
| Al cerrar la tarea, o antes de un commit/push | Paso 1 y la suite completa (paso 4 sin filtro), **una vez**. |

- La suite completa no se repite en cada paso intermedio: se corre una vez al final. Si no está claro qué pruebas cubren el cambio (servicio compartido, modelo, migración de base de datos), se corre completa.
- No hay doble pasada: se compila una vez y solo se reintenta si falla (pasos 2 y 3). En 30 de 30 ejecuciones de CI con `obj` limpio (27-09 a 06-10 de 2026) la primera compilación funcionó.
- Referencia de tiempos en la máquina de desarrollo: compilar la app, 4 s sin cambios y unos 40 s con cambios; compilar las pruebas, unos 40 s; una clase de pruebas, unos 12 s; la suite completa en CI, 1 min 40 s.

## El flake conocido (por qué este skill existe)

El SDK de WPF compila cada proyecto en dos fases: primero genera los `.g.cs` desde el XAML (pasada de *markup compile*) en un proyecto temporal `*_wpftmp`, y luego compila el C# normal contra esos archivos. Cuando `obj/` está "frío" o hay más de una invocación de `dotnet build`/`dotnet test` solapándose (dos proyectos que referencian el mismo `AnimeLocalTracker.csproj`, o un build lanzado mientras otro seguía corriendo), la primera pasada puede terminar en un estado a medias: el build reporta `Compilación correcta` pero faltan `.g.cs`, y la siguiente pasada explota con `CS2001` ("no se encontró el archivo de origen ...View.g.cs") o, más raro, con una excepción en runtime tipo `IOException: No se encuentra el recurso 'app.xaml'` (BAML embebido a medias).

**Esto no es un error en tu código.** Si ves errores `CS2001` mencionando archivos `*.g.cs`, o `AnimeLocalTracker_xxxxx_wpftmp.csproj`, es este flake.

## Procedimiento robusto

1. Build del proyecto principal, solo (nunca junto con Tests/Benchmarks en la misma invocación si puedes evitarlo — reduce las invocaciones solapadas de MSBuild sobre el mismo proyecto):
   ```
   dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
   ```
2. Si falla con `CS2001` (archivos `.g.cs` faltantes) o con errores tipo `InitializeComponent`/`MenuOpcionesPopup` no encontrados: **reintentar el mismo comando sin cambiar nada** — casi siempre basta un segundo intento.
3. Si persiste tras 2-3 reintentos:
   ```
   dotnet build-server shutdown
   rm -rf AnimeLocalTracker/obj AnimeLocalTracker/bin
   dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
   ```
4. Tests: una vez que el build principal compiló limpio, correr `dotnet test` reutilizando ese build en vez de dejar que reconstruya el proyecto referenciado (evita el solapamiento que dispara el flake):
   ```
   dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
   ```
   Para correr solo las pruebas de lo tocado, añadir `--filter "FullyQualifiedName~NombreDeLaClaseTests"` (varias clases: `--filter "FullyQualifiedName~UnaTests|FullyQualifiedName~OtraTests"`).
   Si ese flag no aplica (p. ej. tras cambiar el `.csproj` de Tests), usar `dotnet test ... -c Debug` normal y aplicar el mismo procedimiento de reintento/limpieza de `obj` si falla con `CS2001`.
5. **Nunca mates procesos `dotnet`/`MSBuild`/`testhost` a mitad de una corrida de tests que sigue produciendo output.** Si necesitas cancelar, espera a que la corrida termine sola o a que quede sin producir output por varios minutos — matarla a mitad de ejecución puede dejar un `System.Windows.Application.Current` compartido en mal estado y producir fallos cruzados (`InvalidOperationException: El subproceso que realiza la llamada no puede obtener acceso a este objeto...`) en tests que no tienen nada que ver con tu cambio. Si eso pasa, es contaminación del entorno, no una regresión — limpia procesos colgados (paso 6) y vuelve a correr todo limpio antes de concluir que algo se rompió.
6. Si un build/test se queda sin producir NINGÚN output por varios minutos (ni siquiera la línea inicial "Determinando los proyectos..."), probablemente hay una invocación de `dotnet` anterior colgada compitiendo por el mismo lock. Verificar y limpiar:
   ```
   Get-Process dotnet,MSBuild,VBCSCompiler,testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
   (Sin `AnimeLocalTracker` en la lista: ese proceso puede ser la app real del usuario. Si una instancia lanzada por ti bloquea el build, ciérrala por su PID.)
   y volver a intentar desde el paso 1.
7. **Variante con mensaje explícito de archivo bloqueado** (distinta del flake silencioso de arriba): si el build SÍ produce output pero falla repetidamente (3+ veces seguidas, sin converger) con
   `error MC1000: ... 'The process cannot access the file '...View.g.cs' because it is being used by another process'`,
   no es el flake normal — son **nodos de MSBuild reciclados** (`dotnet.exe` de *node reuse*, `-nodeReuse` está activo por defecto) que quedan vivos entre invocaciones separadas de `dotnet build`/`dotnet test` y retienen el lock del `.g.cs` de una build anterior. Reintentar sin más no lo arregla (a diferencia del flake del paso 2). Solución:
   ```
   Get-Process dotnet,MSBuild,VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force
   dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
   ```
   `-nodeReuse:false` evita que esa build deje nodos vivos para la siguiente invocación; conviene pasarlo en toda la sesión si vas a encadenar varios `dotnet build`/`dotnet test` seguidos.

## Exe de prueba (para lanzar la app manualmente)

`AnimeLocalTracker\bin\Debug\net8.0-windows10.0.26100.0\AnimeLocalTracker.exe`

(El TFM incluye la versión del SDK de Windows desde SMT-01 — controles multimedia del sistema — así que la carpeta ya no es `net8.0-windows` a secas.)

## Criterios de éxito

1. **0 errores, 0 advertencias** en el build directo del proyecto principal.
2. **Pasan las pruebas que tocan** según la tabla de arriba: las afectadas mientras se trabaja, toda la suite al cerrar la tarea (xUnit + FluentAssertions + Moq). El total de tests solo debe subir o mantenerse — si añadiste tests y el total no sube, el binario de test está stale: limpiar `obj`/`bin` de `AnimeLocalTracker.Tests` también y recompilar.
3. Si el build o los tests fallan por una razón que SÍ es tu código: corregir la causa antes de reportar éxito. No "aprobar" con un build en verde si una corrida no-incremental o los tests siguen fallando por el mismo motivo.

## Alternativa: `build.ps1`

El repo tiene `.\build.ps1 -RunTests`, que compila app y pruebas de forma no incremental (reintenta una vez si falla) y además gestiona la descarga de `ffmpeg.exe`/`ffprobe.exe` embebidos si faltan. Es más lento pero más completo (útil antes de empaquetar una release). Para iteración rápida durante desarrollo, el procedimiento de arriba con `dotnet build`/`dotnet test` directos es más rápido y suficiente.
