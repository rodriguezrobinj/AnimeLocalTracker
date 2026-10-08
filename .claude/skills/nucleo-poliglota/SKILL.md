---
name: nucleo-poliglota
description: Añadir o cambiar una función del núcleo Rust (animetracker_core.dll, FFI con LibraryImport) o un comando del daemon Python (AnimeTrackerTools.exe, PythonBridgeService) en AnimeLocalTracker, incluidas sus dependencias, su empaquetado con PyInstaller y cómo comprobarlo. Usar al tocar native/, tools/python/, Services/Native/NativeMethods.cs o Services/Python/, o ante "FFI", "daemon", "cargo", "PyInstaller" o "requirements".
---

# Núcleo políglota — Rust FFI y daemon Python

Reglas de fondo en `polyglot-ffi.md`. Dónde está cada cosa:

| Pieza | Código | Para qué |
|---|---|---|
| Núcleo Rust | `native/animetracker_core/src/` (`lib.rs`, `parser.rs`, `hasher.rs`, `spritesheet.rs`) → `animetracker_core.dll` | Parseo de nombres de archivo, huella perceptual, fotogramas/spritesheets |
| Puente C# | `Services/Native/NativeMethods.cs` | `LibraryImport` + comprobación `IsAvailable` (si falta la DLL, la app degrada) |
| Daemon Python | `tools/python/` (`cli.py`, `parsers/`, `resolvers/`, `media/`) → `AnimeTrackerTools.exe` | Scraping/resolvers de video, metadatos, plugins |
| Puente C# | `Services/Python/PythonBridgeService.cs` | Proceso persistente, JSON por líneas, reintentos |

## A. Función nueva en Rust

1. En `lib.rs`: `#[no_mangle] pub extern "C" fn nombre(...)` con tipos simples (`*const c_char`, `f64`, `i32`, `bool`). **Todo** el cuerpo va dentro de `ffi_catch(|| …, fallback)`: un pánico que cruce el borde `extern "C"` es comportamiento indefinido y derrumba el proceso .NET.
2. Si devuelve texto (lo habitual: JSON), `CString::into_raw()`; la memoria **solo** la libera `anitomy_free_string`. Nunca liberarla con el asignador de C#.
3. Prueba con `#[cfg(test)]` en el módulo. CI exige `cargo clippy --release -- -D warnings` y `cargo audit` sin avisos.
4. En `NativeMethods.cs`: `[LibraryImport(DllName, EntryPoint = "nombre")] private static partial …` (nunca `DllImport`; el hook lo avisa), tipos blittable (`IntPtr`, `double`, `int`; `bool` con `[return: MarshalAs(UnmanagedType.I1)]`) y un método público que siga el patrón de `ParseFilename`: comprueba `IsAvailable`, pasa la entrada con `StringToUtf8Ptr` (se libera con `Marshal.FreeHGlobal` en el `finally`), recoge el resultado con `MarshalStringAndFree` (copia el texto y llama a `NativeAnitomyFreeString`), y ante cualquier excepción registra en `Debug` y devuelve `null` (la app degrada, no se cae).
5. Compila con `cargo build --release --manifest-path native/animetracker_core/Cargo.toml`. `build.ps1` recompila si algún `.rs` es más nuevo y copia la DLL a la raíz del proyecto, a `bin` de la app y a `bin` de las pruebas: una DLL vieja en `bin` hace que la función nueva "no exista" sin error claro.

## B. Comando nuevo en el daemon Python

1. En `cli.py`, `process_command`: un `elif command == "nombre":` que llama a un módulo de `parsers/`, `resolvers/` o `media/` y **devuelve un `dict` con `"success"`** (y `"error"` si falla). El canal es stdout: nada de `print` de depuración (va a stderr o al log).
2. Protocolo: una línea JSON por petición `{"command","payload","id"}`; la respuesta devuelve el mismo `id`. `PythonBridgeService` descarta una respuesta que no sea de la petición en curso: así, una petición cancelada no entrega su respuesta tardía a la siguiente (de ahí la migración v16 que borró análisis de OP/ED mal asignados). Un cambio que rompa el formato sube `protocolVersion`.
3. En C#: `ExecuteCommandAsync<TRequest, TResponse>("nombre", payload, ct)` con DTOs `[JsonPropertyName]`; existe `ExecuteCommandOneShotAsync` como respaldo mientras el daemon espera su reintento (backoff de 10 min). Todo proceso Python se lanza **solo** desde el puente, que lo mata en `ProcessExit`.
4. Prueba en `tools/python/tests/test_*.py`: `python -m pytest tools/python/tests -q` (bloquea el CI). En C#, el puente se mockea en las pruebas del servicio que lo usa.
5. **Dependencias**: se editan `pyproject.toml` / `requirements.in` y se regenera `requirements.txt` con hashes (comando exacto en la cabecera de `requirements.in`, con `py -3.11` y `pip-tools`); el CI instala con `--require-hashes`. `anitopy` es la única que queda en pre-lanzamiento. Cualquier módulo pesado que solo se use en desarrollo va a `MODULOS_EXCLUIDOS` de `build_binary.py` (Playwright y OpenCV ya están fuera).
6. **Empaquetado**: `python tools\python\build_binary.py` (≈ 75 s) regenera `AnimeTrackerTools.exe` en `AnimeLocalTracker/Tools/` (ignorado por git); luego `dotnet build` lo copia a `bin\…\Tools`. En Debug la app ejecuta los scripts de `tools/python`; fuera de Debug solo usa la copia empaquetada, así que **un comando nuevo no existe en una build de release hasta regenerar el `.exe`**. Comprueba cada comando contra el `.exe`, no solo contra el script.

## C. Verificación

- Rust: `cargo test` + `cargo clippy --release -- -D warnings` en `native/animetracker_core`. Python: `pytest`. C#: build con 0 advertencias y `--filter` de la clase tocada; suite completa una vez al cerrar (`repo-build-test`).
- Si el cambio afecta a lo que ve el usuario (miniaturas, detección de OP/ED, resolvers), pruébalo en vivo con `perfil-aislado`: hay detección por audio y descargas reales que no cubre ninguna prueba unitaria.
- No cambies la lista de plugins confiables ni su huella SHA-256 desde aquí (seguridad: `/security-review`).
