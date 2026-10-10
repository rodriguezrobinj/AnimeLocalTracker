# Investigación: qué lenguajes, frameworks y sistemas sumar al proyecto

Fecha: 2026-10-08. Solo lectura del repo y consulta de documentación pública; no se cambió código ni se lanzó la app.

## 1. Veredicto

El proyecto **ya es políglota** (C#, Rust, Python, PowerShell, SQL). El problema que se ve en el código no es que falten lenguajes: es que **los tres principales hacen el mismo trabajo por triplicado y se hablan por canales frágiles**. Varias de las "marañas" nacen justo en esas fronteras (la respuesta del daemon que se guardó en otro episodio, el `ffmpeg` que hay que meter en el `PATH`, el `.exe` de Python que hay que acordarse de regenerar).

Por eso el orden recomendado es:

1. **Un dueño por tarea** (sin instalar nada): quitar los caminos duplicados.
2. **Arreglar el canal con Python** (StreamJsonRpc): peticiones a la vez, cancelar sin reiniciar, progreso.
3. **Darle a Rust trabajo de verdad**: el análisis de audio de openings/endings, que hoy hace numpy.
4. **Un lenguaje realmente nuevo que sí aporta: JavaScript embebido (Jint)**, junto con AngleSharp, para leer las páginas de los sitios sin depender de 44 expresiones regulares.
5. Apuestas grandes que conviene prototipar antes de decidir: **libmpv** como motor de video y un **mando web** desde el móvil.

Lo demás que se evaluó (Go, gRPC, aria2, HLSL sobre el video, FTS5, generadores GraphQL, F#, IA local) queda descartado o aplazado, con el motivo en la sección 6.

## 2. Qué hay hoy (medido el 2026-10-08)

| Lenguaje | Tamaño | Qué hace |
|---|---|---|
| C# / XAML | 54.445 líneas (app) | Todo: interfaz, descargas, base de datos, AniList, reproductor |
| Python | 3.559 líneas (con pruebas) | Daemon `AnimeTrackerTools.exe`: yt-dlp, resolvers de servidores, audio OP/ED, y además parseo, similitud, ffprobe, miniaturas |
| Rust | 446 líneas | `animetracker_core.dll`: envoltorio de `anitomy-pure`, un hash por bloques y lanzar `ffmpeg.exe` |
| PowerShell | 649 líneas | Build, release, benchmarks |

Paquete: el daemon pesa **72 MB**, de los que **27 MB son numpy** y **5,6 MB rapidfuzz** (medido en `Tools/AnimeTrackerTools/_internal`). La carpeta `FFmpeg/` ronda los 165 MB.

Un dato que cambia varias recomendaciones: el `ffmpeg` que ya viaja con la app es una compilación completa y **ya trae** `chromaprint` (huellas de audio), `libass`, `scdet` (cambios de escena), `blackdetect`, `silencedetect`, `libplacebo` y `whisper`. Comprobado con `ffmpeg -muxers` y `-filters`. Hay capacidades que no hace falta añadir: ya están pagadas.

## 3. Las marañas, con su evidencia

**M1. Tres motores para lo mismo, encadenados como respaldo.**

| Tarea | Camino 1 | Camino 2 | Camino 3 |
|---|---|---|---|
| Número de episodio de un archivo | Regex en C# | Rust `anitomy_parse_batch` | Python `parse-batch` (anitopy) |
| Miniatura de un episodio | C# → Rust → lanza `ffmpeg.exe` | C# → daemon Python → lanza `ffmpeg` | — |
| ¿Estos dos títulos son el mismo anime? | Python `match-media` (rapidfuzz) | C# `TituloSimilaridad` (otro criterio) | — |
| Huella de un archivo | Rust: FNV de 5 bloques de 64 KB | Python: dHash con ffmpeg | — |
| Datos técnicos (duración, códec) | C# → daemon → `ffprobe` | C# → `ffprobe` directo (`FotogramasClaveService`, `VideoIntegrityService`) | — |

Consecuencias reales: el comentario de `App.xaml.cs:335` cuenta que cuando Python rechazaba un título "la app decidía con otro criterio más permisivo"; `CLAUDE.md` describe la huella de Rust como "perceptual" y en `hasher.rs` es un hash de bytes (dos copias del mismo episodio con distinto contenedor no coinciden).

**M2. La frontera con Rust se escribe a mano y casi no transporta nada.** `NativeMethods.cs` son 314 líneas para 6 funciones; los datos cruzan como JSON dentro de cadenas C. `anitomy_extract_frame` cruza la frontera solo para lanzar un proceso (`spritesheet.rs:52`), cosa que C# ya hace con `ProcesoExterno`. Los comentarios prometen "<20 ms"; no está medido y arrancar un proceso en Windows suele costar más que eso.

**M3. El canal con Python es de un solo carril.** `PythonBridgeService` serializa todo con un semáforo: mientras se analiza el audio de un episodio (~1,7 s; 30 s en el peor caso medido), una miniatura o una resolución de enlace esperan. Cancelar obliga a **matar y reiniciar el daemon** (0,8–1,6 s). Los comandos largos van por un proceso aparte sin progreso, y la respuesta se busca como "la última línea que parezca JSON", probando dos formatos de nombres.

**M4. Las páginas de los sitios se leen con expresiones regulares.** 17 en `AnimeAv1VideoSourceResolver`, 4 en `JkAnime`, 23 en `NyaaSourceService`. Las de AnimeAV1 leen objetos JavaScript (`destination:\{id:\d+,slug:"…",title:"…"`): si el sitio cambia el orden de dos campos, dejan de encontrar nada. En Python, `packed_extractor.py` y `voe_extractor.py` **reimplementan a mano** lo que hace el JavaScript del sitio (desempaquetar p.a.c.k.e.r, ROT13 + base64).

**M5. El reproductor pelea contra su motor.** Parche en caliente con Harmony para que un subtítulo no cierre la app, lectura de contadores privados por reflexión, un renderizador propio de ASS, un resolvedor propio de líneas solapadas, reintento manual de AV1, una versión de integración continua de MaterialDesign que Flyleaf obliga a usar, y ~17 MB por episodio que no se recuperan.

**M6. numpy vive en el daemon para una sola función** (`audio_skip_plugin.py`) y es más de un tercio de su peso.

## 4. Recomendaciones, por orden

### 4.1 Un dueño por tarea (nada nuevo que instalar)

- **Datos técnicos del video:** llamar a `ffprobe` desde C# con `ProcesoExterno` (ya se hace en dos servicios) y quitar el salto por el daemon (`inspect-episode`). **Hecho el 2026-10-08** (`PythonEpisodeEnricher.EnriquecerEpisodioAsync`).
- **Similitud de títulos:** un único criterio. `token_sort_ratio` de rapidfuzz es ordenar las palabras y calcular una proporción; cabe en unas 30 líneas de C# y se valida contra las puntuaciones que hoy da Python. Se elimina el viaje al daemon y la diferencia de criterio entre el camino normal y el de respaldo. **Hecho el 2026-10-08** (`TituloSimilaridad.SimilitudPorLetras`, validado contra rapidfuzz 3.14.5; `rapidfuzz` fuera del daemon). Ese criterio daba 0,80 a "Dragon Ball Z" / "Dragon Ball Super" (umbral 0,75) cuando no hay MAL ID comparable (ya pasaba con el daemon). **Resuelto el 2026-10-09:** el parecido por letras solo cuenta si los nombres cambian en la escritura y no en una palabra entera (`TituloSimilaridad.SoloCambiaLaEscritura`).
- **Parseo de nombres:** Rust como único motor y fuera `anitopy` del daemon, **después** de comprobar con los nombres reales de la biblioteca que cubre lo que hoy rescata Python (usa el nombre de la carpeta como contexto). **Hecho el 2026-10-08.** Medido antes de quitarlo: en los 407 archivos de la biblioteca Python solo intervenía en 1 (un especial «06.5», que pasaba a ser un segundo episodio 6); en 182 nombres reales de fansub con el episodio anotado (datos de prueba de anitomy) la cadena completa acertaba 152 y Rust solo 178, y en los 26 donde Python cambiaba el resultado fallaba siempre. El contexto de carpeta solo afectaba al título, que la app no usa. `NativeMethods.ParseBatch` y `rayon` se retiraron el 2026-10-09 (nadie los usaba). El mismo día se añadió al núcleo la única regla propia sobre anitomy: el número seguido del idioma ("Anime 12 Latino.mp4"), que antes solo rescataba Python.
- **Miniaturas:** dejar un solo camino. El de Rust no aporta nada sobre lanzar `ffmpeg` desde C#. **Hecho el 2026-10-08** (`PythonEpisodeEnricher.ExtraerMiniaturaAsync`, estático; fuera `anitomy_extract_frame`/`spritesheet.rs` y el comando `generate-thumbnail`). Se conservó el ancho real de 512 px y la prioridad baja del proceso. Comprobado en vivo con un perfil aislado junto con el escáner y los datos técnicos.

*En la app:* al abrir la ficha de una serie con 200 episodios, las miniaturas y los datos técnicos dejan de competir con el análisis de openings por el único carril del daemon.

Coste: bajo; es sobre todo borrar. Riesgo: que el camino que se quita estuviera cubriendo un caso raro; por eso cada retirada va con su comparación previa.

### 4.2 StreamJsonRpc para el canal con Python

**Qué es:** la biblioteca de Microsoft que usa Visual Studio para hablar JSON-RPC 2.0 por una tubería. Trae `NewLineDelimitedMessageHandler` (una línea por mensaje, igual que hoy), identificadores de petición, varias peticiones a la vez, cancelación (`$/cancelRequest`) y progreso (`IProgress<T>`).

**Qué maraña quita (M3):** el semáforo, el reinicio al cancelar, `EsRespuestaDeOtraPeticion`, la heurística de la última línea JSON y el proceso aparte para comandos largos. En Python no hace falta biblioteca: un bucle que reparte cada petición a un hilo y atiende el aviso de cancelación (unas 60–80 líneas).

*En la app:* estás viendo One Piece y pulsas "Siguiente" a mitad del análisis del opening: hoy se mata el daemon y el siguiente episodio espera a que vuelva a arrancar; con esto se cancela esa petición y la del nuevo episodio empieza al momento. Las descargas HLS (Vidhide, Streamwish), que hoy no muestran avance, podrían mostrar porcentaje.

**Límites honestos:** un hilo de Python no se puede matar desde fuera; la cancelación solo corta donde el trabajo coopera (matar el `ffmpeg` hijo, comprobar una bandera entre referencias). Cambia el protocolo, así que hay que subir `protocolVersion` y regenerar el `.exe`. Cumple la regla de `polyglot-ffi.md` sobre peticiones canceladas mejor que el reinicio actual.

### 4.3 Rust para el audio de openings y endings

**Qué es:** portar `detect_themes` a Rust con `rustfft` (o `realfft`) y `rayon`, que ya está en el `Cargo.toml`. `ffmpeg` sigue decodificando el audio a PCM como hoy.

**Qué maraña quita (M6 y parte de M3):** numpy sale del daemon (27 MB menos), el análisis deja de ocupar al daemon y se puede cancelar con una bandera. Es el primer trabajo de cálculo real para el núcleo Rust, que hoy apenas envuelve una biblioteca.

*En la app:* mismo resultado en la barra de progreso (marcas de opening y ending), pero la app instalada pesa menos y el botón "Saltar opening" no depende de que el daemon esté libre.

**Cómo hacerlo sin romper lo que funciona:** portar el **mismo** algoritmo (RMS + 7 bandas, mismos umbrales) y validarlo con los 25 episodios ya medidos (0,73–1,0 el tema correcto, 0,3–0,6 otro corte). La caché de huellas `.npy` cambia de formato y se regenera.

**Hecho el 2026-10-09** (`native/animetracker_core/src/audio.rs` + `Services/DetectorTemasAudio.cs`; diseño y plan en `docs/investigacion-audio-openings-rust.md` y `docs/plan-audio-openings-rust.md`). Validado contra el plugin Python antes de retirarlo: 40 episodios reales de 10 animes, 65 tramos (63 enteros y 2 por trozos, confianzas de 0,749 a 0,999) con diferencia de inicio, fin y confianza de 0,000; los 7 tramos comparables ya guardados en la base coinciden; 120,0 s en Python frente a 98,7 s en la implementación nueva. En vivo (perfil aislado): mismo opening y ending que Python. Se retiraron el plugin, numpy y la comparación entre dos episodios (con su botón de la ficha), que en los datos reales nunca había dado un resultado. El experimento con Chromaprint queda sin hacer: su único destino era esa comparación.

**Experimento aparte, barato:** el `ffmpeg` embebido ya genera huellas Chromaprint (`-f chromaprint`), la misma técnica que usa Intro Skipper de Jellyfin comparando episodios entre sí. Sirve para probar si mejora el camino débil actual: la comparación de dos episodios cuando no hay referencias de AnimeThemes (30 s y un opening falso con 0,47).

**Descartado para esto:** `symphonia` (decodificador en Rust puro): su soporte de Opus sigue sin terminar y buena parte del anime viene con Opus o E-AC3.

### 4.4 JavaScript embebido (Jint) + AngleSharp para leer los sitios

**Qué son:** Jint es un intérprete de JavaScript escrito en .NET (versión 4.x, sin proceso externo ni navegador) con límites de tiempo, memoria y recursión. AngleSharp es un analizador de HTML para .NET con selectores CSS.

**Qué maraña quitan (M4):**

- Los datos de AnimeAV1 **son** objetos JavaScript. En vez de regex que dependen del orden de los campos, se evalúa el objeto y se navega: `datos.media.title`, `datos.episodes[i].number` (nombres ilustrativos).
- El p.a.c.k.e.r de Vidhide/Streamwish y la ofuscación de Voe se **ejecutan** en vez de reimplementarse: cuando el sitio cambie el empaquetado, el código del propio sitio lo sigue resolviendo.
- AngleSharp cubre las partes que sí son HTML (el botón de Mediafire, los enlaces del catálogo).

*En la app:* pulsas "Descargar" en un episodio de AnimeAV1 y hoy, si el sitio reordenó sus datos, aparece "no se encontraron servidores" hasta que salga una versión nueva con las regex corregidas. Con el objeto evaluado, ese cambio no rompe nada.

**Riesgos y límites:** es ejecutar código de un tercero. Jint no da acceso al sistema si no se le pasa nada, pero hay que fijar límite de tiempo y memoria, evaluar solo el fragmento de datos y nunca exponerle objetos de la app. **No** se usa para saltarse protecciones anti-bot (decisión ya tomada con Byse/Filemoon). Los resolvers que necesitan huella TLS de navegador (`curl_cffi`) se quedan en Python. **Sin verificar:** que el bloque de datos de AnimeAV1 se pueda aislar y evaluar tal cual; hace falta un prototipo con una página real guardada. Las 23 regex de Nyaa son otra cosa (nombres de releases): ahí lo que procede es revisar cuántas puede sustituir el `anitomy` que ya hay en Rust.

### 4.5 csbindgen, solo si Rust crece

**Qué es:** una herramienta de Cysharp que, al compilar Rust, genera el archivo C# con las declaraciones de las funciones exportadas.

**Qué maraña quita (M2):** escribir y mantener `NativeMethods.cs` a mano, y permite pasar estructuras en vez de JSON dentro de cadenas.

**Pega:** genera `[DllImport]` con tipos directos (sin conversión, así que el rendimiento es equivalente), y la regla del proyecto exige `[LibraryImport]`. No comprobé si tiene opción para generarlo; si no, habría que exceptuar el archivo generado en la regla y en el hook. Con 6 funciones no compensa; con 15, sí. `uniffi-bindgen-cs` se descarta: es de terceros y su estabilidad entre versiones no está clara.

## 5. Apuestas grandes: prototipo antes de decidir

### 5.1 libmpv como motor de video (C, con scripts en Lua/JS y shaders GLSL)

**Qué resolvería (M5):** subtítulos ASS nativos con sus fuentes, líneas solapadas, saltos exactos y AV1 sin reintentos, y abriría Anime4K (reescalado para anime) y scripts de usuario. Dejaría de hacer falta Harmony, la reflexión sobre contadores privados y, probablemente, la versión de integración continua de MaterialDesign.

**Por qué no ahora:** es reescribir el reproductor (`ReproductorViewModel` 2.397 líneas, la vista 1.157, siete coordinadores, el mini reproductor). Los envoltorios para .NET son proyectos pequeños o en beta (LibMPVSharp 0.0.1-beta; Mpv.NET incrusta un control de WinForms). El problema de las ventanas superpuestas no desaparece, solo cambia de forma. Y no está comprobado que el consumo de memoria por episodio mejore.

**Si se quiere explorar:** un arnés aparte que abra los tres archivos que más guerra dieron (Re:Zero T4 ep. 19 por el ASS, Tokyo Revengers T4 ep. 1 por el AV1, Youjo Senki por los estilos) y mida memoria tras 10 episodios. Con ese resultado se decide.

**HLSL sobre el video, descartado con Flyleaf:** la documentación de FlyleafLib 3.11.3 solo expone elegir procesador de video, superresolución y filtros; no hay punto de entrada para shaders propios. Los `ShaderEffect` de WPF no llegan a la superficie de video. Anime4K pasa por cambiar de motor o por mantener una copia modificada de Flyleaf.

### 5.2 Mando web desde el móvil (servidor HTTP embebido + página HTML/TypeScript)

No arregla ninguna maraña; abre una puerta. Un servidor mínimo dentro de la app (Kestrel) que sirve una página a la red local: pausar, saltar el opening o pasar de episodio desde el sofá, o ver la cola de descargas. Coste real: es abrir un puerto en la máquina del usuario, así que necesita su propio diseño de seguridad (emparejamiento, solo red local, apagado por defecto). Es una función nueva: iría por `brainstorming` antes de tocar nada.

## 6. Evaluado y descartado

| Herramienta | Motivo |
|---|---|
| Go, Kotlin, C++ propio | Ninguna tarea del proyecto los pide. Sumarían un cuarto compilador a la integración continua sin quitar ninguna maraña. |
| gRPC para el daemon | Resuelve lo mismo que 4.2 con mucho más peso en el `.exe` de Python y HTTP/2 por medio. |
| aria2 como motor de descargas | La lógica que hace valioso a `DownloadService` (tope aprendido por servidor, descifrado de Mega, volver a resolver el enlace ante un 403) no existe en aria2; habría que mantener ambas. |
| WebView2 / Playwright | Solo servirían para pasar desafíos anti-bot, y esa decisión ya está tomada. Playwright ya se sacó del daemon. |
| SQLite FTS5 | Está disponible en el SQLite que usa la app, pero la búsqueda de la galería filtra en memoria por título y nombres alternativos y a ese tamaño no hay nada que acelerar. Reabrir si se quiere buscar en sinopsis o en registros. |
| Generador GraphQL (StrawberryShake, ZeroQL) | AniList se usa con unas pocas consultas, casi todas en un solo servicio. El generador añade un paso de compilación y un esquema que mantener para muy poco. |
| F# | Mismo runtime y cero coste de interoperar, pero no quita ninguna maraña concreta. |
| IA local (Whisper, ONNX) | El `ffmpeg` embebido ya trae `whisper`, pero ~92 % de las descargas son con subtítulos pegados a la imagen y el equipo de referencia (Intel HD 620) iría muy lento. Reabrir si entra una función de aprendizaje de japonés. |
| `tantivy`, `ffmpeg-next` en Rust | El primero duplica a FTS5. El segundo metería la decodificación dentro del proceso de la app: un video corrupto pasaría de "miniatura fallida" a "app cerrada". |

## 7. Hallazgos de paso

- El `AnimeTrackerTools.exe` **local** lleva `curl_cffi 0.10.0`, mientras `requirements.txt` fija `0.16.3` (subido por PYSEC-2026-2431). Se construyó con el Python del sistema, no con el entorno bloqueado. Los builds de release instalan con `--require-hashes`, así que el paquete publicado no debería estar afectado; no lo comprobé sobre un instalador.
- `pydantic` está en las dependencias y ningún archivo de `tools/python` lo importa. `playwright` sigue en `pyproject.toml` y solo lo usa `browser_stream_extractor.py`.
- `CLAUDE.md` y los comentarios de `spritesheet.rs` describen el núcleo Rust con capacidades que no tiene ("fingerprint perceptual", "<20 ms").

## 8. Qué no se verificó

- Ningún tiempo de esta investigación es nuevo: los de audio y descargas vienen de mediciones anteriores del proyecto.
- No se probó Jint contra una página real de AnimeAV1, ni csbindgen con `LibraryImport`, ni libmpv en WPF.
- El ahorro de tamaño del daemon es el peso en disco de numpy y rapidfuzz; el `.exe` final puede variar algo.

## Fuentes

- [csbindgen (Cysharp), NuGet](https://www.nuget.org/packages/csbindgen)
- [csbindgen: artículo del autor](https://neuecc.medium.com/csbindgen-generate-c-native-code-bridge-automatically-or-modern-approaches-to-native-code-78d9f9a616fb)
- [uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs)
- [Jint, NuGet](https://www.nuget.org/packages/jint)
- [StreamJsonRpc, referencia de API](https://learn.microsoft.com/en-us/dotnet/api/streamjsonrpc)
- [Intro Skipper (Jellyfin)](https://cdn.jsdelivr.net/gh/intro-skipper/intro-skipper@10.11/README.md)
- [Symphonia: códecs soportados](https://www.mintlify.com/pdeljanov/Symphonia/resources/supported-codecs)
- [SQLitePCLRaw.lib.e_sqlite3 (FTS5 incluido)](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.12)
- [LibMPVSharp, NuGet](https://feed.nuget.org/packages/LibMPVSharp)
- [Mpv.NET](https://github.com/hudec117/Mpv.NET-lib-)
- [Magpie (Anime4K en HLSL)](https://backiee.wasmer.app/https_github_com/Blinue/Magpie)
- [StrawberryShake](https://chillicream.com/products/strawberryshake)
- [ZeroQL](https://github.com/byme8/ZeroQL/)
