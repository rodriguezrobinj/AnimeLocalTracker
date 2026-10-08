# AGENTS.md — Guía mínima para agentes de IA

Aplicación de escritorio **Windows** (.NET 8 WPF, MVVM). Stack: SQLite (WAL, sqlite-net-pcl) · core nativo Rust (FFI `animetracker_core.dll`) · daemon Python (`AnimeTrackerTools.exe`, PyInstaller) · Velopack · OAuth2 AniList (GraphQL) · AniSkip.

## Comandos

- Verificación **proporcional al cambio** (procedimiento y flakes conocidos en `.claude/skills/repo-build-test/SKILL.md`):
  - Sin tocar C#/XAML (documentos, reglas, configuración del repo): no hace falta compilar ni probar.
  - Mientras se trabaja: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug` y las pruebas de lo tocado: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~NombreDeLaClaseTests"`
  - Al cerrar la tarea o antes de subir: la suite completa, una vez (el mismo `dotnet test` sin `--filter`).
- Build completo como en CI/release: `powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests` (compila una vez y reintenta solo si falla; no hace falta para el trabajo diario).
- Compila con `TreatWarningsAsErrors` + analyzers CA: **0 warnings obligatorio** (no editar `Directory.Build.props` ni `.editorconfig` sin entenderlos).
- SCA: `dotnet list AnimeLocalTracker/AnimeLocalTracker.csproj package --vulnerable --include-transitive` (+ el de Tests).
- CI (local, si `gh` está instalado): `gh run list --limit 1` · `gh run view <id> --log-failed`
- Exe de Debug: `AnimeLocalTracker\bin\Debug\net8.0-windows10.0.26100.0\AnimeLocalTracker.exe`. **No lo lances directamente** para probar: usaría los datos reales del usuario (usa el perfil aislado, ver abajo).

## Invariantes críticas (no romper)

- **Datos de usuario SIEMPRE en `%LocalAppData%\AnimeLocalTrackerData`** (AppDataPaths), nunca en el directorio de instalación.
- Backups = snapshot atómico con `VACUUM INTO` (nunca `File.Copy` de la DB abierta).
- Import JSON nunca toca la nube (registros importados → `SincronizadoEnNube = true`).
- UI: textos traducidos en XAML con `{loc:T Clave}` (`Services/TExtension.cs`); no escribir a mano el `{Binding [Clave], Source=...}` (detalles en `.agents/rules/wpf-mvvm.md`). Toda clave nueva debe existir en ES y EN (`LocalizationService`).
- El daemon Python se mata en `ProcessExit` (`PythonBridgeService`) — no eliminar.
- **Probar la app en vivo = perfil aislado** (`python .claude/skills/perfil-aislado/scripts/perfil_aislado.py crear|iniciar|cerrar|borrar`): copia temporal de la biblioteca, sin token de AniList. Nunca lanzar el exe con los datos reales, nunca con `--borrar-datos` (borra los datos reales sin poder redirigirlos), y cerrar solo el proceso lanzado por uno mismo, por PID (nunca `Stop-Process`/`taskkill` por nombre: puede ser la app real con un episodio en marcha).
- **Esquema de la base de datos**: solo con una entrada nueva en `DatabaseService.Migraciones` (nunca `CreateTableAsync` suelto ni `[Indexed]`); las que borran o unen datos hacen copia previa y se aplazan si no pueden hacerla. Procedimiento en `.claude/skills/migracion-db/SKILL.md`.
- Release: tag `v*` → pipeline (SCA bloqueante → vpk → delta → pre-release). La versión la define el tag; `AnimeLocalTracker.csproj` `<Version>` es para builds locales. `global.json` fija SDK 8.x (los runners traen SDK 10 → analizadores CA distintos).
- **Fechas**: guardar SIEMPRE UTC (`DateTime.UtcNow`). sqlite-net devuelve `Kind=Unspecified`: tratarlas como UTC (`Kind != Local ? ToLocalTime()`) antes de mostrar/agrupar.
- **Historial/Actualizaciones**: el "historial" refleja visionado REAL — solo la reproducción registra `UltimaReproduccion`; un marcado manual NO debe fabricar fecha (NULL no se rellena en la capa de datos). Borrar un archivo del disco conserva el registro. Los feeds de episodios usan `ListBox` virtualizado (nunca `ItemsControl` dentro de `ScrollViewer`).
- **Cada commit debe compilar**: no commitear cambios parciales que dependan de archivos nuevos sin versionar (el repo debe poder clonarse y compilar en HEAD).
- Nueva pestaña/vista = una fila en `Pestanas.Todas` (`ViewModels/Pestana.cs`) → ViewModel singleton en DI (`App.xaml.cs`) → `DataTemplate` en `App.xaml`. No se tocan `MainWindow.xaml`, `MainViewModel` ni `NavigationService` (detalles en `.agents/rules/ui-wpf-vistas.md`).

## Convenciones

- Commits en español, conventional commits (`feat(área): ...`, `fix(área): ...`), con el ID del hallazgo cuando aplique.
- Tests en `AnimeLocalTracker.Tests` (xUnit + FluentAssertions + Moq). No romper la suite (al añadir features, añadir tests — el conteo aparece en README).
- Logs de la app: `%LocalAppData%\AnimeLocalTrackerData\Logs\` (`sesiones\` con un archivo por arranque y `errores.log`) — consultar antes de diagnosticar bugs.

## Documentación

- `docs/auditoria-*/` — auditorías con hallazgos `file:line` y su evidencia.
- `docs/investigacion-*.md` y `docs/plan-*.md` — investigaciones y planes por tema.

Para tareas estructurales grandes, leer el documento correspondiente antes de tocar código.

## Eficiencia de Tokens y Comunicación

- **Concisión estricta**: ir directo a la solución técnica, comandos y código. Omitir cortesías, explicaciones redundantes y resúmenes obvios.
- **Ediciones quirúrgicas**: reemplazos puntuales localizados; no reescribir archivos enteros salvo imprescindible.
- **Búsqueda focalizada**: leer fragmentos/símbolos específicos, no volcados completos de archivos grandes.
- **Explicaciones con ejemplos prácticos**: traducir lo técnico a situaciones reales de la app (reproducción, galería, descargas, sync AniList) en lenguaje claro.
- **Flake conocido del build WPF**: si `obj` queda con BAMLs bloqueados (MC1000/BG1002/MC3072 espurios) o el exe no se regenera tras "OK": matar procesos `dotnet`/`MSBuild`/`testhost` colgados y borrar `obj` del proyecto antes de reconstruir.
- **Verificación del ejecutable**: Siempre debes asegurarte de que el ejecutable se haya generado/actualizado correctamente y exista en `AnimeLocalTracker\bin\Debug\net8.0-windows10.0.26100.0\AnimeLocalTracker.exe` tras compilar.

## Reglas Modulares y Skills (.agents/)

- Reglas activas: `.agents/rules/` (`wpf-mvvm.md`, `polyglot-ffi.md`, `persistence.md`, `comunicacion-explicaciones.md`, `ui-wpf-vistas.md`, `skills-orquestacion.md`).
- Skills del repo en `.claude/skills/` (cada `SKILL.md` es un procedimiento legible por cualquier agente): `repo-build-test` (build y pruebas), `wpf-add-view`, `wpf-visual-verification`, `perfil-aislado` (probar la app sin tocar datos reales), `migracion-db` (cambios de esquema SQLite), `commit-es` (commit y subida: español con tildes, solo a `main`, sin firma de Claude).
- Guardarraíles para Claude Code en `.claude/hooks/` (bloquean `--borrar-datos`, cerrar la app por nombre, `git push` fuera de `main` y escribir en la carpeta de datos real); prueba: `python .claude/hooks/test_hooks.py`.
