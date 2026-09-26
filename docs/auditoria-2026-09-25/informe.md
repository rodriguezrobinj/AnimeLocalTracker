# Auditoría de ciberseguridad y rendimiento — AnimeLocalTracker

**Fecha:** 2026-09-25 · **Versión auditada:** 1.0.5 (rama `main`, 62 commits posteriores a la auditoría del 2026-09-14) ·
**Equipo de pruebas:** Windows 10 Pro 19045, 1366×768, red doméstica, biblioteca real del usuario (205 animes, 3 301 episodios).
**Tipo:** solo lectura. No se corrigió nada en el código de la app; lo único que se añadió al repo es esta carpeta.

Esta auditoría **no repite** la del 2026-09-14 (`auditoria_AnimeLocalTracker_resultados_2026-09-14_23-25-46/`): parte de ella,
verifica qué cambió y añade lo que aquella no pudo medir (perfilado real en Release, memoria en uso, seguridad de la
superficie nueva: torrents, navegador headless, plugins). Los datos crudos están en [`evidencia/`](evidencia/).

---

## 1. Resumen ejecutivo

| Área | Veredicto | En una línea |
|---|---|---|
| **Seguridad** | 🟢 Sólida | Sin hallazgos críticos ni altos. 1 media (plugins sin controles de confianza), 4 bajas y 2 informativas. Dependencias limpias en las 3 cadenas. |
| **Rendimiento** | 🟡 Aceptable, con mejoras claras | Reposo excelente (CPU ~0 %), pero el **arranque tarda ~4 s hasta la ventana y ~6,8 s hasta la galería**, y la memoria en reposo es alta (~410 MB con el daemon). |
| **Calidad / CI** | 🟡 Buena, con un fallo evitable | 973 tests en verde, build sin advertencias; **18 % de los runs de CI fallan**, casi todos por el mismo error de caché. |

**Lo que más conviene hacer primero** (ordenado por relación beneficio/esfuerzo):
1. Publicar con **ReadyToRun** — medido: −16 % al abrir la ventana, −9 % a la galería, +13 MB. Es una línea.
2. Arreglar el **fallo de caché de `cargo-audit` en CI** — elimina la mayoría de los runs rojos.
3. Sacar **`Engine.Start` de Flyleaf del camino de arranque** — cuesta 0,4–0,55 s medidos y la segunda llamada es gratis.
4. **Plugins opt-in con huella (hash) fijada** — cierra el único hallazgo de severidad media.

---

## 2. Cambios desde la auditoría del 14-sep (verificados)

| Hallazgo previo | Estado hoy | Evidencia |
|---|---|---|
| Benchmarks no compilaban (`DummyDatabaseService`) | ✅ **Resuelto** | `dotnet build` del proyecto Benchmarks: 0 errores; ejecuté la categoría de BD |
| Script de benchmarks reportaba éxito falso | ✅ **Resuelto** | `run_benchmarks_and_reports.ps1` ya hace `exit $LASTEXITCODE` |
| "Posible doble arranque del daemon Python" | ✅ **Descartado** | 1 solo proceso hijo en todas las muestras (14+ en reposo y en uso) |
| Instalador y ejecutable sin firma Authenticode | ⚠️ **Sigue igual** | `NotSigned` en instalador, MSI, `.exe`, `.dll` propias y el daemon |
| ViewModels sobredimensionados | 🟡 Mejoró | `ReproductorViewModel` 1 894 → 1 692 líneas tras el refactor; sigue siendo el más grande junto con `DetalleViewModel` (1 421) |
| Accesibilidad (32 % de controles sin nombre) | ➖ No reauditado | Fuera del alcance de esta pasada |

Superficie **nueva** desde entonces: MonoTorrent (BitTorrent), Playwright/Chromium headless para extraer streams, selector de
torrents, estilos de subtítulos, controles de diálogo. Se revisó la parte de seguridad de las tres primeras.

---

## 3. Seguridad

### 3.1 Métodos

Análisis de dependencias (`dotnet list package --vulnerable --include-transitive`, `cargo audit`, `pip-audit`), análisis
estático de Python (`bandit`), búsqueda de secretos (árbol de trabajo **e historial completo**), revisión dirigida de código
(procesos, red, deserialización, SQL, rutas, plugins, OAuth, CI), y comprobaciones **dinámicas** en la app en ejecución
(sockets abiertos, procesos hijo, cifrado real del token). No hay pruebas de intrusión externas ni fuzzing (ver §6).

### 3.2 Hallazgos

| ID | Severidad | Hallazgo |
|---|---|---|
| **SEC-01** | 🟠 Media | **Plugins sin controles de confianza.** `CSharpPluginLoader` carga cualquier `.dll` de `%LocalAppData%\AnimeLocalTrackerData\Plugins` al construir el resolver de video (arranque), con los mismos permisos que la app, sin opt-in, hash ni firma. El daemon ejecuta además `audio_skip_plugin.py` desde esa misma carpeta en cada episodio (`run-plugin` → `exec_module`). Cualquier proceso del mismo usuario que escriba ahí consigue ejecución de código *persistente dentro de la app*. No escala privilegios (ya tenía los del usuario), pero convierte a la app en punto de persistencia y hace viable la ingeniería social ("copia este plugin"). |
| **SEC-02** | 🟡 Baja | **La política de URLs no bloquea destinos locales.** `UrlSeguridad.EsUrlDescargaHttpSegura` exige HTTPS y sin credenciales, pero acepta `https://127.0.0.1`, `https://192.168.x.x`, `https://169.254.169.254`. La URL final puede venir de scrapers/yt-dlp y `RedirectSeguroHandler` reutiliza la misma función en cada redirección. Impacto acotado (solo GET; el cuerpo se guarda en un archivo) pero es un SSRF hacia la red local. |
| **SEC-03** | 🟡 Baja | **Descarga por torrent: dos defensas en profundidad ausentes.** (a) El `.torrent` se lee con `ReadAsByteArrayAsync` sin límite de tamaño (host acotado a `nyaa.si`, así que solo lo explotaría un Nyaa comprometido). (b) La ruta del archivo elegido sale de `DownloadCompleteFullPath` sin comprobar que quede dentro de la carpeta temporal; hoy depende de que MonoTorrent rechace rutas con `..`. Sí hay una buena práctica: solo se baja el archivo de video elegido (`DoNotDownload` al resto). |
| **SEC-04** | 🟡 Baja | **Dependencias Python con rangos abiertos.** Solo `yt-dlp==2026.8.19` está fijada; `playwright>=1.45`, `opencv-python-headless>=4.9`, `numpy>=1.26`, `pydantic>=2.7`, `rapidfuzz>=3.9`, `anitopy>=2.1.2` se resuelven a "lo último" al empaquetar el daemon: builds no reproducibles y superficie de cadena de suministro. |
| **SEC-05** | 🟡 Baja | **Paquete de CI en producción:** `MaterialDesignThemes 5.3.3-ci1462` (versión de integración continua, no estable). Vale la pena confirmar procedencia o volver a una estable. |
| **SEC-06** | ⚪ Info | **Sin firma Authenticode** (persistente desde el 14-sep). El pipeline ya rehúsa publicar sin certificado; queda pendiente la decisión de producto. Mitigación mientras tanto: publicar el SHA-256 junto a cada release. |
| **SEC-07** | ⚪ Info | **OAuth de AniList por flujo implícito** (token en el fragmento de la URL) con `HttpListener` en el puerto fijo `127.0.0.1:5050`, solo activo durante el login. El flujo está bien endurecido (estado aleatorio de 32 bytes, token de un solo uso, cabecera anti-CSRF, timeout de 2 min). Un proceso local malicioso podría ocupar el puerto antes; requiere malware previo en la sesión. |

### 3.3 Lo que está bien (comprobado, no supuesto)

- **Token cifrado de verdad:** `anilist_token.txt` empieza con la cabecera DPAPI (`01 00 00 00 D0 8C 9D DF`) y no contiene un JWT legible.
- **Cero vulnerabilidades conocidas:** NuGet (app y tests), Cargo (19 crates, 1 271 avisos) y PyPI, el 2026-09-25.
- **Bandit:** 31 hallazgos, **todos Low** (0 Medium, 0 High): `try/except/pass` ×4, `random` en el generador de datos de prueba ×9, uso de `subprocess` con lista de argumentos y sin `shell=True` ×18.
- **Sin secretos** en el árbol ni en el historial. La única coincidencia de `client_secret` es texto de una librería de terceros; el `ClientId` de AniList (48217) es público por diseño.
- **Sin telemetría** (búsqueda en código propio y en sockets): no hay contadores de analítica, y en reposo la app no escucha en ningún puerto; solo tiene 3 conexiones HTTPS salientes a una IP de Cloudflare (destino exacto no atribuido).
- **Sin puertos abiertos por el motor de torrents en reposo** (medido: 0 sockets en escucha, 0 UDP). *No medí qué abre durante una descarga real.*
- **Comunicación con el daemon por stdin/stdout** (no TCP), con reintentos con retroceso, un solo daemon y cierre forzado al salir.
- **Navegador headless** con el Edge/Chrome del sistema (`channel=msedge/chrome`), sin `--no-sandbox` y sin empaquetar un Chromium.
- **FFI de Rust** con `catch_unwind` en todos los bordes; `VACUUM INTO` con comillas escapadas; restauración de copias con `PRAGMA integrity_check`; URL del tráiler con `Uri.EscapeDataString`.
- **CI endurecido:** acciones fijadas por SHA, `permissions: contents: read` (solo el release escribe), Dependabot en 4 ecosistemas.
- **FFmpeg incluido: 8.1** (`avcodec 63.8`): reciente, importante porque la app abre archivos de terceros.

---

## 4. Rendimiento (medido)

Todas las cifras son de **Release** en esta máquina; los datos y scripts de medición están en `evidencia/`. n = 3–4 corridas
por escenario salvo que se indique; tratar las diferencias pequeñas con cautela.

### 4.1 Arranque

| Métrica | Valor |
|---|---|
| Ventana principal visible | **3,7–4,5 s** en caliente (media 3,99 s con la publicación actual, n = 4; 4,0–4,4 s con el build de `bin\Release`, n = 3) · **12,5 s** la primera vez tras compilar (disco/antivirus en frío) |
| Galería lista (portadas) | **6,4–7,3 s** en caliente · 15,4 s en frío |
| 205 portadas cargadas / primeras 24 tarjetas | ~2,05 s / ~205–221 ms (log `[Perf]` de la app) |
| Con **ReadyToRun** (n = 4 alternadas) | ventana **3,34 s** (−16 %) · galería **6,14 s** (−9 %) · +13 MB de tamaño |

**Dónde se va el tiempo** (perfil de CPU con `dotnet-trace`, hilo principal, tiempos inclusivos que se solapan):

| Concepto | Tiempo |
|---|---|
| `Window.Show()` → primera medición/colocación del árbol visual | ~1,9 s |
| Resolución del grafo de dependencias (22 `GetRequiredService`) | ~1,2 s (de ellos `MainWindow` ≈ 0,70 s de XAML) |
| Carga de diccionarios de recursos (`ResourceDictionary.Source` ×221) | ~1,06 s |
| `OnStartup` síncrono (incluye **`Engine.Start` de Flyleaf**) | 0,47 s (`Engine.Start` aislado: **0,38–0,55 s**; una 2.ª llamada cuesta 0 ms) |
| Cargar biblioteca en `GaleriaViewModel` | ~0,52 s |

La publicación actual (`build_velopack_release.ps1`) usa `--self-contained false` **sin ReadyToRun ni TieredPGO**.

### 4.2 Recursos en reposo y en uso

| Escenario | Memoria (WS) | CPU medio (pico) | Hilos | Handles |
|---|---|---|---|---|
| Reposo tras arrancar | 334–355 MB (privada 216–239) | **0–1 %** (17–22 % los ~5 s iniciales) | 31–38 | ~980–1 010 |
| Desplazar la galería | 418 MB (máx. 429) | 5 % (19 %) | 36 | 981 |
| **Reproduciendo video** (GPU) | **542 MB** (máx. 581; privada 441) | 10 % (24 %) | 52 | 1 253 |
| Tras cerrar el reproductor | 500 → 466 MB | 6 % → 0,1 % | 35 | ~1 125 |
| Daemon Python (proceso hijo) | +68 MB | — | — | — |

Total en reposo ≈ **410 MB** (app + daemon). CPU en reposo es excelente; el consumo lo domina la memoria.

### 4.3 Memoria: ¿hay fuga?

Ciclo "abrir → reproducir 16 s → cerrar" repetido (3 corridas, hasta 4 ciclos):

| Medida | Resultado |
|---|---|
| Memoria **privada** tras cerrar, ciclo a ciclo | 300 → 318 → 334 → 355 MB (≈ **+16–18 MB por ciclo**); las otras corridas tienen la misma pendiente |
| **Heap administrado vivo** (`dotnet-gcdump`, GC forzado) | 75,4 → 77,8 MB tras 4 ciclos (**+3 %**) |
| Instancias vivas de `ReproductorViewModel` / `ReproductorView` / `FlyleafHost` | +2 respecto a la base, **no** +4: no se acumulan por ciclo |
| Handles tras cerrar | 1 142 → 1 176 → 1 135 → 1 168: sin tendencia clara |

**Conclusión:** no hay fuga administrada demostrable. El crecimiento residual es **nativo** (Flyleaf/FFmpeg/Direct3D) o memoria
retenida sin devolver al sistema; con 3–4 ciclos no puedo distinguirlas. Gravedad baja; se resuelve con una prueba de desgaste
larga (§5). Contadores de .NET durante el uso: el heap de Gen2 crece porque casi no hay recolecciones de Gen2 (0–1 cada 2 s), no
porque se retenga.

### 4.4 Base de datos

| Métrica | Valor |
|---|---|
| Tamaño | 0,8 MB (+ 4,1 MB de WAL, que es el punto normal de auto-checkpoint de 1 000 páginas) · integridad `ok` |
| Consultas típicas (205 animes / 3 301 episodios) | 0,45–8 ms; el feed del historial usa el índice (`OR` incluido) |
| Escalado sintético ×10 (32 950 episodios) | feed 1,5 ms · episodios de un anime 0,13 ms · conteo por anime 11 ms |
| Escalado sintético ×50 (164 750 episodios) | feed 1,2–1,9 ms · 0,09 ms · conteo por anime **35 ms** (lineal) |
| BenchmarkDotNet: guardar 500 episodios en 1 transacción | **15,0 ms** (30 µs/registro, 2,6 MB asignados) |
| BenchmarkDotNet: leer todos los registros | **113 µs** |

La base **no es un cuello de botella** ni a 50× la biblioteca actual. Menor: hay índices redundantes
(`DescargaHistorial` tiene dos sobre `FechaUtc`; `RegistroEpisodio_AniListId` es prefijo de `IX_RegistroEpisodio_AnimeEp`).

### 4.5 Tamaño, build y CI

| Métrica | Valor |
|---|---|
| Paquete publicado | **516 MB** (428 archivos): `Tools/` 307 MB (**OpenCV 112**, **driver de Playwright 105**, NumPy 21…), `FFmpeg/` 162 MB, resto ≈ 47 MB |
| Build Release (frío) | 97 s, 0 advertencias, 0 errores |
| Tests | 973 en verde, 31–38 s |
| CI (últimos 40 runs) | 31 ok, **7 fallos (18 %)**; duración mediana 5,8 min, p90 7,8 min, máx. 10,2 min |

### 4.6 Hallazgos de rendimiento

| ID | Severidad | Hallazgo |
|---|---|---|
| **PERF-01** | 🟠 Media | **Arranque lento** (~4 s a la ventana, ~6,8 s a la galería). Palancas medidas o identificadas: ReadyToRun (−0,65 s medido), sacar `Engine.Start` del arranque (−0,4–0,55 s), reducir los 221 diccionarios de recursos (~1 s inclusivo) y difuminar la carga de `MainWindow`. |
| **PERF-02** | 🟡 Baja-Media | **Memoria en reposo alta (~410 MB) y sin retorno a la base tras reproducir** (+16–18 MB privados por ciclo, nativo). Pide una prueba de desgaste de 20+ ciclos y revisar el `Dispose` del `Player`. |
| **PERF-03** | 🟡 Baja | **Excepciones de flujo:** ~8 excepciones por cada 2 s mientras se reproduce (contadores de .NET), y contención de locks de 47–326 por 2 s. No se atribuyó el origen (los `catch {}` vacíos del bucle de seguimiento son candidatos). |
| **PERF-04** | 🟡 Baja | **Espera síncrona en el arranque:** `CSharpPluginLoader` hace `Task.Wait(5 s)` por plugin colgado mientras se construye el grafo de dependencias, y `TorrentDownloadService` llama a `GetAwaiter().GetResult()` en `ProcessExit`, que podría retrasar el cierre. |
| **PERF-05** | 🟡 Baja | **Paquete de 516 MB:** OpenCV (112 MB) y el driver de Playwright (105 MB) pesan más que la propia app. Candidatos a "paquete opcional" bajo demanda. |
| **CI-01** | 🟠 Media | **18 % de runs rojos, ≥ 5 de 7 por el mismo error:** `binary cargo-audit.exe already exists in destination` (la caché restaura el binario y `cargo install` lo rechaza). Es un fallo determinista de configuración, no de código. |
| **DB-01** | ⚪ Info | Índices redundantes (ver §4.4); solo cuestan en escritura. |

---

## 5. Plan recomendado

**Rápido (≤ 1 día cada uno)**

| # | Acción | Efecto esperado | Cierra |
|---|---|---|---|
| 1 | `-p:PublishReadyToRun=true` en la publicación | −0,65 s de arranque (medido) | PERF-01 |
| 2 | Arreglar el paso de `cargo-audit` en `ci.yml` (`--force` o comprobar antes de instalar) | Elimina la mayoría de runs rojos | CI-01 |
| 3 | Mover `Flyleaf Engine.Start` a segundo plano o hacerlo bajo demanda | −0,4–0,55 s | PERF-01 |
| 4 | Plugins **opt-in** (apagados por defecto), listar con SHA-256 y pedir confirmación la primera vez que cambia | Reduce el riesgo de persistencia a "el usuario lo aceptó" | SEC-01 |
| 5 | Tope de 5 MB al `.torrent` + comprobar que `rutaFinal` queda dentro de `carpetaTemporal` | Defensa en profundidad | SEC-03 |
| 6 | Rechazar IPs privadas/loopback/link-local en `UrlSeguridad` (resolviendo DNS, también en cada redirección) | Cierra el SSRF local | SEC-02 |
| 7 | `pip-compile --generate-hashes` para el daemon | Builds reproducibles | SEC-04 |
| 8 | Migración que elimine los índices redundantes | Menos coste de escritura | DB-01 |

**Estructural**

- Firma Authenticode (decisión de producto) y, mientras tanto, publicar el SHA-256 de cada release.
- Prueba de desgaste (20+ ciclos, VMMap/`dotnet-gcdump`) y revisión del `Dispose` de Flyleaf.
- Perfilar las excepciones con `dotnet-trace` (eventos de excepción) para atribuir PERF-03.
- Reducir los diccionarios de recursos y cargar diferido lo que no se ve al abrir.
- Sacar OpenCV/Playwright a un paquete descargable bajo demanda.
- Medir en un equipo modesto (esta máquina no es representativa del piso de hardware).

---

## 6. Limitaciones (lo que **no** se hizo)

- **Sin pruebas de intrusión externas ni fuzzing** de los parsers (FFmpeg, `.torrent`, JSON de importación).
- **No se descargó ningún torrent:** los puertos y el tráfico de red durante una descarga real no están medidos.
- **No se probó un plugin malicioso:** SEC-01 está establecido por lectura de código, no por explotación.
- **Una sola máquina, n = 3–4** por escenario; el escenario de uso se conduce con clics simulados, así que las variaciones
  pequeñas (< 5 %) no son significativas. No se midió GPU ni energía.
- **Medición en frío distorsionada** por el antivirus la primera vez tras compilar (12,5 s frente a ~4 s).
- **Accesibilidad y diseño** no se reauditaron.
- **Los números de memoria son del sistema operativo** (working set/privada), no de una prueba de desgaste larga.

## 7. Efectos secundarios en tu equipo

- Usé la app real con tu biblioteca: **se reprodujo el episodio 24 de Slime varias veces**, así que su progreso guardado cambió
  (antes de mis pruebas de hoy estaba en ~07:14). No se marcó nada como visto ni se sincronizó con AniList.
- Tu token de AniList y tu `settings.json` no se tocaron durante la auditoría.
- Instalé `dotnet-gcdump` y un entorno de Python **solo en la carpeta temporal** de la sesión; no modifiqué tus herramientas globales.
- Publiqué dos copias de la app (~1 GB) en esa misma carpeta temporal.

---

## 8. Estado de los arreglos (aplicados el 2026-09-25, sin commit)

Suite completa tras los cambios: **1 110 tests en verde** (137 nuevos) y build sin advertencias; los 28 tests de Python también
pasan con las versiones fijadas. Los cambios están en el árbol de trabajo, sin commitear ni publicar.

| ID | Arreglo | Verificación |
|---|---|---|
| **CI-01** | `ci.yml` y `release.yml`: `cargo-audit` solo se instala si falta la versión fijada (`--force` si no coincide). | YAML válido. **No verificable en local:** se confirmará en el primer run de CI. |
| **PERF-01** | `PublishReadyToRun` en el `.csproj` (solo con `-r win-x64`); `Engine.Start` de Flyleaf pasa a ejecutarse tras mostrar la ventana. | Publicado y medido (n = 5): ventana **3,28 s** (antes 3,99), galería **6,08 s** (antes 6,77). **Casi todo el beneficio es de ReadyToRun; el aplazamiento de Flyleaf no se distingue del ruido (≤ 0,1 s).** Reproducción comprobada (video + decodificación por GPU). |
| **PERF-04** | El cierre espera como máximo 3 s a detener el sembrado de torrents (antes esperaba sin límite). | Cubierto por revisión; sin prueba automática (requiere un motor de torrents real). |
| **SEC-01** | Plugins **apagados por defecto**; cada archivo (.dll/.py) requiere confianza expresa con su huella SHA-256 fijada; si el archivo cambia deja de ser confiable. Nueva pestaña en Configuración → Plugins con diálogo de confirmación. El daemon rechaza nombres con rutas. | 75 tests en las clases de plugins (casi todos nuevos): huella, estados, ejecución bloqueada, archivo reemplazado, nombres con `..`, cargador, ViewModel y traducciones ES/EN. Comprobado en la app: interruptor, lista con huella, diálogo ámbar; al cancelar no se guarda nada. |
| **SEC-02** | `UrlSeguridad` rechaza IPs privadas/loopback/enlace local/CGNAT/reservadas, `localhost`, nombres de una sola etiqueta y sufijos locales; `RedirectSeguroHandler` resuelve el DNS antes de cada petición y de cada redirección y bloquea si alguna IP no es pública. | 95 tests entre `UrlSeguridad` y `RedirectSeguroHandler` (incluye los previos): IPv4, IPv6, IPv4 mapeada, límites de rango, DNS que falla, redirección a la red local. **Limitación conocida:** entre la comprobación y la conexión el DNS puede cambiar (*DNS rebinding*). |
| **SEC-03** | El `.torrent` se lee con tope de 5 MB; la ruta del archivo descargado debe quedar dentro de la carpeta temporal. | 17 tests de `EntradaSegura` (rutas normalizadas, `..`, prefijos parecidos, cuerpos sin `Content-Length`). |
| **SEC-04** | `tools/python/requirements.txt` con **hashes SHA-256** (compilado con `pip-compile` desde `pyproject.toml` + `requirements.in`); CI, release y el script local lo instalan con `--require-hashes`. Ningún pre-lanzamiento salvo `anitopy` (que solo existe como 2.2.0rc2). | `pip install --dry-run --require-hashes` correcto en Python 3.11 y 3.14; instalación real y 28 tests de Python en verde en un entorno limpio 3.11. Efecto colateral bueno: el script local ahora también instala `playwright`, que antes no incluía. |
| **DB-01** | Migración v10 borra los índices duplicados y se quitan los `[Indexed]` redundantes de los modelos. | 3 tests: base nueva sin duplicados, base v9 migrada, y las consultas siguen usando índice (`EXPLAIN QUERY PLAN`). |

**Pendiente / no cambiado:**

- **SEC-05** (`MaterialDesignThemes 5.3.3-ci1462`): **no se tocó.** La última estable es 5.3.2 y el commit que lo fijó (`d91a35c`) no explica el motivo; bajar de versión sin conocerlo podría reintroducir un fallo. Conviene confirmar por qué se eligió esa compilación.
- **SEC-06** (firma Authenticode) y **SEC-07** (puerto fijo 5050 en el login): decisiones de producto / sin cambio.
- **PERF-02** (memoria tras reproducir), **PERF-03** (excepciones de flujo) y **PERF-05** (paquete de 516 MB): requieren más medición o decisiones de diseño; no se abordaron.
- **Dependabot** no actualiza `requirements.txt` con hashes de forma automática si solo cambia `pyproject.toml`: tras subir una dependencia hay que regenerar el archivo con el comando de `requirements.in`.

---

## 9. Segunda ronda de arreglos: lo que quedaba pendiente (2026-09-25)

Suite tras esta ronda: **1 112 tests en verde**, build sin advertencias.

| ID | Resultado | Evidencia |
|---|---|---|
| **PERF-03** — excepciones de flujo | ✅ **Resuelto en lo principal.** El 78 % venía de un solo sitio: bindings a `Image.Source` con valor `null` (WPF intenta convertirlo, `ImageSourceConverter` lanza `NotSupportedException` y WPF la captura). Se añadió `TargetNullValue={x:Null}` a los 3 bindings que faltaban (`DetalleView`, `HistorialView`, `AnimeWrappedCardView`; el resto de la app ya lo tenía). Nueva prueba que falla si aparece otro binding de imagen sin él. | Rastro de eventos de excepción con `dotnet-trace` + TraceEvent sobre el mismo recorrido (galería → ficha → 2 reproducciones → Configuración → Estadísticas): **133 → 30 excepciones en ~60 s (−77 %)**; 0,5/s. Las 30 restantes son esperadas: 12 cancelaciones de peticiones HTTP al navegar, 9 cancelaciones del seguimiento al cerrar el reproductor, 3 del logger, 3 *timeouts* de AnimeThemes (caída), 2 de un convertidor de fuentes interno de WPF y 1 `NullReferenceException` dentro de Flyleaf (no es nuestra). |
| **PERF-02** — memoria tras reproducir | ⚠️ **Causa acotada, sin arreglo posible desde la app.** | El crecimiento es de **~17 MB de memoria privada por cada episodio abierto**, lineal (8 ciclos, sin meseta). Se descartó: (1) fuga administrada — el heap vivo casi no crece (75,4 → 77,8 MB); (2) basura sin recolectar — un GC forzado acotó el heap administrado pero **no** cambió la pendiente y causó pausas de hasta 290 ms (vs 72 ms), así que se revirtió; (3) el búfer del demuxer — bajar `BufferDuration` de 30 s a 10 s dio la misma pendiente (~18 MB/ciclo), revertido. Un arnés aislado con solo FlyleafLib también crece (~+11 MB por ciclo con la **3.11.11**, la última), así que el origen está dentro de Flyleaf/FFmpeg/Direct3D y actualizar no lo arregla. Mitigación real: ninguna barata; quedaría reutilizar un único `Player` entre episodios (cambio de diseño con riesgo) o cambiar de biblioteca. En la práctica: reproducir 10 episodios seguidos suma ~170 MB hasta reiniciar la app. |
| **PERF-05** — paquete de 516 MB | ✅ **Opciones (b) y (a) aplicadas; ver secciones 10 y 12.** | No hay recortes gratuitos: `cv2.pyd` (82 MB) y `opencv_videoio_ffmpeg` (29 MB) son monolíticos, y `node.exe` de Playwright (89 MB) es el motor que este necesita. Opciones: **(a)** reemplazar OpenCV por lectura de fotogramas con FFmpeg + NumPy (−112 MB; hoy solo se usa para leer fotogramas, redimensionar y restar en `scene_detector.py` y `episode_fingerprint.py`), pero los valores de huella cambiarían respecto a los ya calculados, hay que validar con videos reales; **(b)** convertir Playwright en un paquete opcional que se descarga bajo demanda (−105 MB; solo lo usa la extracción del servidor Byse); **(c)** ambas: ~−217 MB (42 %). |
| **SEC-05** — `MaterialDesignThemes 5.3.3-ci1462` | ➖ **Sin cambio.** La estable más reciente es 5.3.2 y el commit que fijó esa compilación no dice por qué. | Se necesita saber qué corregía esa compilación antes de bajar de versión. |
| **SEC-06 / SEC-07** | ➖ **Sin cambio.** Firma Authenticode (requiere un certificado) y el puerto fijo 5050 (está registrado en la app de AniList: cambiarlo exige volver a registrarla). | — |

**Incidente durante las pruebas (ya corregido):** dos de mis scripts de captura hicieron clic en el icono de la barra de tareas
aunque la app ya estaba al frente (lo que la minimiza) y los clics siguientes cayeron sobre otras pantallas, **guardando cambios en
el `settings.json` real** (`MinimizarABandejaAlCerrar` y `NotificarConBandejaSiempre` a `true`, estilo de subtítulos a Arial/40,
plugins activados con `audio_skip_plugin.py` marcado como confiable, grupo de fansub «Erai-raws»). **Se restauraron los valores
que había al inicio de la sesión** y los scripts ahora comprueban que la app esté al frente antes de hacer clic (y abortan si
no), respaldan y comparan `settings.json`; la última comprobación tras los tests y las pruebas de memoria fue "idéntico".

---

## 10. PERF-05 aplicado: Playwright fuera del paquete (opción b)

**Hallazgo que cambió el plan:** la extracción con navegador (`BrowserStreamExtractor.extract_byse`) ya está **deshabilitada dentro del
`.exe` empaquetado** (`sys.frozen`): el desafío anti-bot de Byse falla el 100 % de las veces en el binario de PyInstaller y la función
devuelve un error al empezar, **sin llegar a importar Playwright**. Es decir, los ~105 MB de Playwright (driver Node incluido) viajaban
en cada release sin ejecutarse jamás. Por eso la opción (b) se resolvió **no empaquetándolo**, en lugar de construir una descarga bajo
demanda para una función que en el paquete no existe (el mismo ahorro, sin infraestructura nueva ni superficie de red nueva). Si algún
día se arregla Byse dentro del binario, esa función podría descargar Playwright entonces.

| Qué | Detalle |
|---|---|
| Cambio | `tools/python/build_binary.py`: se quita `--collect-all playwright` y se añaden `--exclude-module playwright/greenlet/pyee` (las dos últimas solo las usa Playwright). La construcción de argumentos pasó a una función testeable. Sigue instalado y funcionando en desarrollo (`python cli.py`) y en los tests de CI. |
| Tamaño del daemon | **289 MB → 183 MB (−106 MB)**; 346 → 153 archivos. El paquete completo pasaría de ~516 a ~410 MB (estimación: no se volvió a publicar). |
| Tests | 3 tests nuevos (`test_build_binary.py`); **31 tests de Python en verde**. Ya existía uno que garantiza que en modo empaquetado no se toca Playwright. |
| Equivalencia | Se ejecutó el mismo comando en el daemon anterior (respaldado) y en el nuevo: `ping`, `parse-filename`, `inspect-episode` (ffprobe) y el mensaje de Byse dan **salida idéntica**; `fingerprint` da el mismo hash (`c122e9c4c4d0dc86`) y `detect-scenes` los mismos tiempos y confianza (0,92). |
| Extremo a extremo | App real reproduciendo el ep. 24: el daemon persistente arranca, un solo proceso hijo, y `PLUGIN AUDIO: Opening detectado [39 - 124] (Conf: 0,97)` igual que antes. |

**Lo que sigue abierto de PERF-05:** OpenCV (112 MB) — la opción (a) — sigue pendiente de decisión. El daemon respaldado quedó en la
carpeta temporal de la sesión por si hiciera falta volver atrás.

---

## 11. Análisis de la opción (a): reemplazar OpenCV por FFmpeg (medido, no aplicado)

Se midió antes de decidir; no se cambió código.

**Qué usa realmente la app de OpenCV (revisión de código):**
- `detect-scenes` (detector de opening/ending por fotogramas): **ninguna parte de la app en C# lo llama** (solo existe en la línea de comandos del daemon).
- `fingerprint` / `find-duplicates`: la app usa **primero la huella nativa en Rust** (`compute_file_fingerprint`); la de Python/OpenCV solo entra como respaldo si Rust falla. Se usa al abrir la ficha de un anime (`DetalleViewModel`).
- Es decir: en el flujo normal de la app, OpenCV no se ejecuta.

| Medida | OpenCV (hoy) | Alternativa con FFmpeg | Lectura |
|---|---|---|---|
| Tamaño en disco | 112 MB (`cv2.pyd` 82 + `opencv_videoio_ffmpeg500` 29) | 0 (ya se distribuye `ffmpeg.exe`) | **−112 MB**: el paquete pasaría de ~410 a ~300 MB (de 516 al inicio de la auditoría, −42 %) |
| Arranque del daemon | +65–80 ms de ~1,6 s | — | despreciable |
| Memoria del daemon | +4 MB | — | despreciable |
| Detección de escenas, ep. 24 (H.264 720p) | 31 s | 18 s | −41 % |
| Detección de escenas, ep. 23 (**AV1** 1080p) | **750 s (12,5 min)** | 28 s | **27× más rápido** |
| Coincidencia de resultados (opening) | OP 103,5–120,0 (ep. 24) | OP 105,0–121,5 | dentro de una muestra (1,5 s) |
| Coincidencia de resultados (ending) | ep. 24: 1381,7–1398,2 · ep. 23: 1361,5–1378,0 | ep. 24: 1380,2–1396,7 · ep. 23: **1405,0–1421,5** | ep. 24 coincide; **ep. 23 difiere 44 s** (no se sabe cuál es correcto: falta una referencia) |

**Hallazgos laterales:**
1. OpenCV trae su **propia copia de FFmpeg (rama 5.0**, `opencv_videoio_ffmpeg500`), mucho más vieja que la 8.1 que usa el reproductor. Es un decodificador de archivos de terceros que no se actualiza con el resto y que `pip-audit` no vigila. Quitarlo elimina esa superficie.
2. Trampa latente: si algún día se conecta `detect-scenes` en la app, un capítulo AV1 (la fuente principal de AnimeAV1) tardaría ~12 minutos con OpenCV.
3. Prototipo funcional en la sesión (un proceso FFmpeg por ventana + la misma lógica de detección): sirve de base si se decide portarlo.

**Conclusión:** la mejora de (a) es sobre todo **tamaño (−112 MB) y mantenimiento/seguridad**, no rendimiento en uso normal. Riesgos: la huella de respaldo cambiaría de valores (solo se compara dentro de una misma consulta, así que no invalida datos guardados) y habría que decidir qué hacer con `detect-scenes` (borrarlo por estar sin uso, o portarlo y validar contra una referencia).

---

## 12. Opción (a) aplicada: OpenCV fuera del daemon (2026-09-26, sin commit)

**Qué se hizo**
- `media/episode_fingerprint.py`: el fotograma clave lo extrae ahora **ffmpeg** (una sola llamada: `-ss t -frames:v 1`, escala a 9×8 en gris) y el dHash se calcula en Python puro. Con las mismas protecciones que el resto de llamadas (`es_ruta_media_segura`, `-nostdin`, `-max_alloc`, timeout de 60 s). Antes no validaba la ruta.
- `detect-scenes` / `media/scene_detector.py` **eliminados**: la app en C# nunca los llamaba. También el filtro `scdet` de `ffmpeg_guard.py` que solo usaba ese módulo.
- `pyproject.toml`: fuera `opencv-python-headless`; `requirements.txt` recompilado (único cambio en las versiones fijadas: desaparece `opencv-python-headless==5.0.0.93`). `build_binary.py` excluye además `cv2` para que un entorno de desarrollo que aún lo tenga instalado no lo arrastre de vuelta.
- Pruebas: 32 en Python (antes 31): se quitaron las del detector de escenas y se añadieron las de dHash (valores conocidos, rechazo de URLs, comando con protecciones, fallos de ffmpeg y una integración con ffmpeg real que agrupa copias y separa un video distinto).

**Medido** (daemon compilado en un entorno limpio instalado desde el lock con `--require-hashes`)

| | Antes | Ahora |
|---|---|---|
| Carpeta del daemon | 183 MB | **62 MB** (−121 MB; 289 MB al inicio de PERF-05) |
| Arranque del daemon (saludo `ready`) | 1,26 s | 0,88 s |
| `find-duplicates` (3 videos de 24 min) | 2,48 s | 1,14 s |
| `fingerprint` del mismo episodio | `c122e9c4c4d0dc86` | `c123e9c4c4d0dca6` (**4 bits** de diferencia, umbral de duplicado = 8) |

Comparación comando a comando contra el daemon anterior (ping, parse-filename, match-media, inspect-episode, generate-thumbnail, run-plugin con numpy, find-duplicates, resolve-stream): mismos resultados; el único cambio esperado es `detect-scenes`, que ahora responde «Comando desconocido».

**Para tener en cuenta**
- Los hashes de la huella de respaldo cambian un poco (4 de 64 bits en la muestra) al usar otro reescalado; solo se comparan entre sí dentro de una misma consulta y la huella principal sigue siendo la de Rust, así que no hay datos guardados que invalidar.
- Si algún día se quiere detección automática de intro/ending, hay un prototipo que usa un solo proceso de ffmpeg por ventana (28 s en AV1 frente a 750 s con OpenCV); no se integró porque no hay consumidor.
