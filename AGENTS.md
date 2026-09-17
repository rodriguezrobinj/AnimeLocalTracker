# AGENTS.md — Guía mínima para agentes de IA

Aplicación de escritorio **Windows** (.NET 8 WPF, MVVM). Stack: SQLite (WAL, sqlite-net-pcl) · core nativo Rust (FFI `animetracker_core.dll`) · daemon Python (`AnimeTrackerTools.exe`, PyInstaller) · Velopack · OAuth2 AniList (GraphQL) · AniSkip.

## Comandos

- Build (doble pasada, obligatorio): `powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests`
- **Verificación final recomendada tras tocar código** (el doble pase puede reportar "OK" con errores ocultos del proyecto WPF): `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug --no-incremental` y `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-incremental`
- Solo tests: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-build`
- Compila con `TreatWarningsAsErrors` + analyzers CA: **0 warnings obligatorio** (no editar `Directory.Build.props` ni `.editorconfig` sin entenderlos).
- SCA: `dotnet list AnimeLocalTracker/AnimeLocalTracker.csproj package --vulnerable --include-transitive` (+ el de Tests).
- CI (local, si `gh` está instalado): `gh run list --limit 1` · `gh run view <id> --log-failed`
- Exe de Debug para probar en el Explorador: `AnimeLocalTracker\bin\Debug\net8.0-windows\AnimeLocalTracker.exe`

## Invariantes críticas (no romper)

- **Datos de usuario SIEMPRE en `%LocalAppData%\AnimeLocalTrackerData`** (AppDataPaths), nunca en el directorio de instalación.
- Backups = snapshot atómico con `VACUUM INTO` (nunca `File.Copy` de la DB abierta).
- Import JSON nunca toca la nube (registros importados → `SincronizadoEnNube = true`).
- UI: bindings localizados `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}}` — **solo en propiedades OneWay** (`Run.Text` es TwoWay: añadir `Mode=OneWay`). Toda clave nueva debe existir en ES y EN (`LocalizationService`).
- El daemon Python se mata en `ProcessExit` (`PythonBridgeService`) — no eliminar.
- Release: tag `v*` → pipeline (SCA bloqueante → vpk → delta → pre-release). La versión la define el tag; `AnimeLocalTracker.csproj` `<Version>` es para builds locales. `global.json` fija SDK 8.x (los runners traen SDK 10 → analizadores CA distintos).
- **Fechas**: guardar SIEMPRE UTC (`DateTime.UtcNow`). sqlite-net devuelve `Kind=Unspecified`: tratarlas como UTC (`Kind != Local ? ToLocalTime()`) antes de mostrar/agrupar.
- **Historial/Actualizaciones**: el "historial" refleja visionado REAL — solo la reproducción registra `UltimaReproduccion`; un marcado manual NO debe fabricar fecha (NULL no se rellena en la capa de datos). Borrar un archivo del disco conserva el registro. Los feeds de episodios usan `ListBox` virtualizado (nunca `ItemsControl` dentro de `ScrollViewer`).
- **Cada commit debe compilar**: no commitear cambios parciales que dependan de archivos nuevos sin versionar (el repo debe poder clonarse y compilar en HEAD).
- Nueva pestaña/vista = seguir la cadena completa: `NavegarMensaje_*` → `INavigationService` → DI singleton → `DataTemplate` en `App.xaml` → flag `Es*Activo` + comando en `MainViewModel` → botón en `MainWindow.xaml` con `ToolTip` localizado.

## Convenciones

- Commits en español, conventional commits (`feat(área): ...`, `fix(área): ...`), con el ID del hallazgo cuando aplique.
- Tests en `AnimeLocalTracker.Tests` (xUnit + FluentAssertions + Moq). No romper la suite (**362 tests**; al añadir features, añadir tests — el conteo aparece en README).
- Logs de la app: `%LocalAppData%\AnimeLocalTrackerData\Logs\app.log` — consultar antes de diagnosticar bugs.

## Documentación local (NO versionada, vive solo en disco)

- `AUDITORIA_BLOQUE1_SEGURIDAD_ARQUITECTURA.md` … `AUDITORIA_BLOQUE4_UX_LEGAL_MARKETING.md` — auditorías por bloque con hallazgos `file:line`.
- `NOTA_CAMBIOS_REMEDIACION_BLOQUE1.md` … `_BLOQUE4.md` — antes/después con ejemplos de uso.
- `RESUMEN_EJECUTIVO_REMEDIACION.md` — matriz de estado de los 4 bloques.

Para tareas estructurales grandes, leer el bloque correspondiente antes de tocar código.

## Eficiencia de Tokens y Comunicación

- **Concisión estricta**: ir directo a la solución técnica, comandos y código. Omitir cortesías, explicaciones redundantes y resúmenes obvios.
- **Ediciones quirúrgicas**: reemplazos puntuales localizados; no reescribir archivos enteros salvo imprescindible.
- **Búsqueda focalizada**: leer fragmentos/símbolos específicos, no volcados completos de archivos grandes.
- **Explicaciones con ejemplos prácticos**: traducir lo técnico a situaciones reales de la app (reproducción, galería, descargas, sync AniList) en lenguaje claro.
- **Flake conocido del build WPF**: si `obj` queda con BAMLs bloqueados (MC1000/BG1002/MC3072 espurios) o el exe no se regenera tras "OK": matar procesos `dotnet`/`MSBuild`/`testhost` colgados y borrar `obj` del proyecto antes de reconstruir.
- **Verificación del ejecutable**: Siempre debes asegurarte de que el ejecutable se haya generado/actualizado correctamente y exista en `AnimeLocalTracker\bin\Debug\net8.0-windows\AnimeLocalTracker.exe` tras compilar.

## Reglas Modulares y Skills (.agents/)

- Reglas activas: `.agents/rules/` (`wpf-mvvm.md`, `polyglot-ffi.md`, `persistence.md`, `comunicacion-explicaciones.md`, `ui-wpf-vistas.md`).
- Skill de verificación de build y pruebas: `.agents/skills/repo-build-test/SKILL.md`.
