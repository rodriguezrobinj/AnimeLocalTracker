# AGENTS.md — Guía mínima para agentes de IA

Aplicación de escritorio **Windows** (.NET 8 WPF, MVVM). Stack: SQLite (WAL, sqlite-net-pcl) · core nativo Rust (FFI `animetracker_core.dll`) · daemon Python (`AnimeTrackerTools.exe`, PyInstaller) · Velopack · OAuth2 AniList (GraphQL) · AniSkip.

## Comandos

- Build (doble pasada, obligatorio): `powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests`
- Solo tests: `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj`
- Compila con `TreatWarningsAsErrors` + analyzers CA: **0 warnings obligatorio** (no editar `Directory.Build.props` ni `.editorconfig` sin entenderlos).
- SCA: `dotnet list AnimeLocalTracker/AnimeLocalTracker.csproj package --vulnerable --include-transitive`

## Invariantes críticas (no romper)

- **Datos de usuario SIEMPRE en `%LocalAppData%\AnimeLocalTrackerData`** (AppDataPaths), nunca en el directorio de instalación.
- Backups = snapshot atómico con `VACUUM INTO` (nunca `File.Copy` de la DB abierta).
- Import JSON nunca toca la nube (registros importados → `SincronizadoEnNube = true`).
- UI: bindings localizados `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}}` — **solo en propiedades OneWay** (`Run.Text` es TwoWay: añadir `Mode=OneWay`).
- El daemon Python se mata en `ProcessExit` (`PythonBridgeService`) — no eliminar.
- Release: tag `v*` → pipeline (SCA bloqueante → vpk → delta → pre-release). La versión la define el tag; `AnimeLocalTracker.csproj` `<Version>` es para builds locales.

## Convenciones

- Commits en español, conventional commits (`feat(área): ...`, `fix(área): ...`).
- Tests en `AnimeLocalTracker.Tests` (xUnit + FluentAssertions + Moq). No romper la suite (265 tests).
- Logs de la app: `%LocalAppData%\AnimeLocalTrackerData\Logs\app.log` — consultar antes de diagnosticar bugs.

## Documentación local (NO versionada, vive solo en disco)

- `AUDITORIA_INTEGRAL.md` — auditorías v1→v4 con trazabilidad de hallazgos.
- `PLAN_ACCION_V4.md` — plan de acción completo con seguimiento por tarea.
- `INFORME_ANTES_DESPUES.md` — antes/después del plan v4.

Para tareas estructurales grandes, leer `AUDITORIA_INTEGRAL.md` y `PLAN_ACCION_V4.md` antes de tocar código.
