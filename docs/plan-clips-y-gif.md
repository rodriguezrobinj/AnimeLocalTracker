# Clips MP4 y GIF desde el reproductor: diseño y plan

Fecha: 2026-10-17. Segunda función de `investigacion-integraciones-y-clips.md` (#91 de `propuestas-nuevas-funciones.md`).

## Objetivo

Desde el reproductor, marcar un tramo de un episodio y crear con un clic un MP4 (que siempre quepa en Discord) o un GIF, con los subtítulos que se están viendo incrustados. El archivo queda en `Vídeos\AnimeLocalTracker` y copiado en el portapapeles para pegarlo con Ctrl+V.

Ejemplo: en el episodio 14 de Frieren, `I` cuando empieza el hechizo, `O` cuando acaba; panel "12:05 → 12:31 · 26 s"; clic en MP4; unos segundos después "Clip listo y copiado".

## Decisiones tomadas

- **Rango:** marcar inicio (`I`) y fin (`O`), configurables en Atajos. No `[`/`]`: en teclado español necesitan AltGr. Sin modo "últimos 30 s".
- **Calidad MP4:** siempre < 10 MB (límite de Discord sin Nitro): 720p como máximo, tasa de bits calculada por duración. Sin selector de calidad.
- **Límites:** MP4 hasta 30 s (si el fin queda más lejos se recorta a inicio + 30 s y el panel lo dice). GIF hasta 10 s (con un tramo más largo el botón GIF sale desactivado con su explicación). Fin antes del inicio → se intercambian. Tramo de menos de 1 s → no se ofrece.
- **Solo en el reproductor** (la ficha no tiene línea de tiempo).
- **Enfoque:** ffmpeg embebido sobre el archivo original. Descartados: codificar con una segunda instancia de mpv (más riesgo, sin garantía de que el libmpv embebido codifique) y grabar la pantalla (controles y avisos encima).
- **Simplificación:** el tramo no se pinta sobre la barra de progreso; el panel muestra los tiempos.

## Qué genera

**MP4**
- `-ss <inicio> -t <duración> -i <video>` (las dos delante de `-i`: limitan la lectura del episodio; con `-t` de salida el GIF, cuya paleta se entrega al acabar la entrada, leía el resto del episodio y llegaba al límite de 2 min; visto en la prueba en vivo); vídeo H.264 (`libx264 -preset veryfast`), `-b:v`/`-maxrate` = tasa calculada, `-bufsize` = 2 × tasa; audio AAC 128 kbps; `-movflags +faststart`; sin pistas de subtítulos (`-sn`).
- Tasa de vídeo (kbps) = mín(5000, presupuesto − 128) con presupuesto = 9,5 MB × 8 / duración. 30 s → ≈ 2528 kbps.
- Escala `scale=-2:'min(720,ih)'` DESPUÉS de los subtítulos (se dibujan a la resolución original y se reducen con el vídeo).
- Audio: la pista que se está oyendo (`-map 0:<IndicePistaAudioActual>`; si es -1, `-map 0:a:0?`). Vídeo: `-map 0:v:0`.

**GIF**
- 480 px de ancho, 15 fps, sin audio, `-loop 0`; paleta en dos pasadas dentro de un solo `-filter_complex`:
  `…,fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle`.

**Subtítulos (los dos formatos)**
- Solo si están activos en el reproductor y la pista es de texto (ASS/SRT…). Pista de imagen (PGS, VobSub): el clip sale sin subtítulos y el aviso final lo indica.
- Pista interna: `subtitles=<video>:si=<n>`, donde n = posición de la pista entre las pistas de subtítulos internas ordenadas por índice de flujo (el motor da el índice absoluto `ff-index`; `si` cuenta solo subtítulos). Las fuentes adjuntas del MKV las carga libass.
- Pista externa (`RutaExterna`): `subtitles=<archivo externo>`.
- Con `-ss` antes de `-i` los tiempos empiezan en 0: `setpts=PTS+<inicio>/TB,subtitles=…,setpts=PTS-STARTPTS` para que cada línea salga en su momento.
- La ruta dentro del filtro se escapa en dos niveles (valor de opción: `\ ' :`; grafo de filtros: `[ ] , ; ' \`), con `\` → `/`. Probado contra ffmpeg real con un nombre de fansub difícil.
- El estilo propio de Configuración ("Usar mi estilo") no se aplica al clip: sale el estilo original de la pista.

**Archivo**
- Carpeta: `Vídeos\AnimeLocalTracker`; si Windows no da la carpeta Vídeos (devuelve "", p. ej. con el perfil redirigido), `<perfil>\Videos\AnimeLocalTracker`; sin perfil, `<datos de la app>\Clips`. Nunca una ruta relativa (escribiría junto al programa; pasó en la prueba en vivo).
- Los atajos `I`/`O` van en `AppSettings.Atajos` como `CapturarFrame`, sin fila en la pantalla de Atajos (que solo lista seis).
- `Vídeos\AnimeLocalTracker\<Título> - Ep <N> - <mm-ss>.mp4|.gif` (inicio del tramo; con horas, `h-mm-ss`). Caracteres no válidos de Windows → `_`; si existe, ` (2)`, ` (3)`…
- Tras crearse: `Clipboard.SetFileDropList` (hilo de interfaz) y aviso con la ruta.

## Piezas

- `Services/Clips/SolicitudClip.cs`: `enum FormatoClip { Mp4, Gif }`; `record SubtitulosClip(string Archivo, int? IndiceRelativo)`; `record SolicitudClip(string RutaVideo, double InicioSeg, double DuracionSeg, FormatoClip Formato, int IndiceAudio, SubtitulosClip? Subtitulos, string RutaSalida)`.
- `Services/Clips/ArgumentosClip.cs` (estático, puro): `Construir(SolicitudClip)`, `TasaVideoKbps(double duracion)`, `EscaparRutaFiltro(string ruta)`, `RangoValido(double a, double b, FormatoClip? formato)`, `IndiceRelativoSubtitulos(IReadOnlyList<PistaSubtitulosMotor>, PistaSubtitulosMotor)`, `NombreArchivo(string titulo, int episodio, double inicioSeg, FormatoClip formato)`.
- `Services/Clips/IClipService.cs` + `ClipService.cs`: `Task<string?> CrearAsync(SolicitudClip, CancellationToken)`; ejecuta `ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, …, límite 2 min, prioridad `BelowNormal`)`; ruta del archivo si el código es 0 y existe; null y registro si no. Crea la carpeta. No lanza.
- `ReproductorViewModel` (parcial nuevo `ReproductorViewModel.Clips.cs`): marcas `ClipInicioSeg`/`ClipFinSeg`, `MostrarPanelClip`, `TextoRangoClip`, `ClipRecortado`, `GifDisponible`, `CreandoClip`; comandos `MarcarInicioClip`, `MarcarFinClip`, `CrearClipMp4`, `CrearClipGif`, `DescartarClip`. Arma la `SolicitudClip` con la pista de audio y la de subtítulos activas.
- `ReproductorView.xaml(.cs)`: teclas `MarcarInicioClip`/`MarcarFinClip`; panel sobre la barra de progreso (estilos y colores de la paleta, `ToolTip` + `AutomationProperties.Name` en botones de icono).
- `AppSettings.Atajos` + `ObtenerTeclaPara`: `MarcarInicioClip` = `I`, `MarcarFinClip` = `O`.
- Textos ES/EN (skill `localizacion`): nombres de los atajos, panel, avisos (creando, listo, error, sin subtítulos de imagen, recortado a 30 s, GIF máx. 10 s).
- DI: `IClipService` singleton.

## Errores y casos límite

- Un clip a la vez: con uno en marcha, los botones del panel quedan desactivados.
- ffmpeg falla o tarda más de 2 min → aviso de error; el detalle (últimas líneas de stderr) va al registro.
- Se cierra el reproductor mientras se crea: el clip termina igualmente (token propio, no el del reproductor) y queda en la carpeta; sin aviso.
- Video en streaming/sin archivo local: no se ofrece (las marcas no hacen nada).
- Volver a marcar inicio con un panel abierto: empieza un tramo nuevo (se olvida el fin).

## Pruebas

Unitarias (xUnit + FluentAssertions):
- `TasaVideoKbps`: 30 s → ≈ 2528; 2 s → 5000 (tope).
- `Construir` MP4: `-ss` antes de `-i`, `-t`, mapa de audio con el índice, `-b:v`/`-maxrate`/`-bufsize`, filtro con `setpts`+`subtitles`+`scale` en ese orden; sin subtítulos → solo `scale`; audio -1 → `0:a:0?`.
- `Construir` GIF: `palettegen`/`paletteuse`, `fps=15`, sin audio (`-an`), `-loop 0`.
- `EscaparRutaFiltro`: `C:\a\[x] b'c,d;e.mkv` → forma esperada.
- `RangoValido`: intercambia, recorta MP4 a 30 s (marca "recortado"), GIF > 10 s no válido, < 1 s no válido.
- `IndiceRelativoSubtitulos`: pistas internas con índices 2, 3, 5 → la de índice 5 es `si=2`; externas no cuentan.
- `NombreArchivo`: caracteres prohibidos, horas, extensión.
- `ReproductorViewModel`: marcar inicio/fin muestra el panel con el texto del rango; descartar lo oculta.

De integración (ffmpeg real, carpeta temporal, se omiten si falta ffmpeg):
- Video negro de 20 s con un ASS interno que dice "HOLA" de 10 a 12 s, nombre `[SubsPlease] Prueba - 14 (1080p) [AB'C,D;E].mkv`:
  - MP4 de 10 a 12 s: existe, < 10 MB, duración ≈ 2 s, alto ≤ 720, y su primer fotograma es más claro que el de un MP4 de 0 a 2 s (subtítulo incrustado y en su momento).
  - GIF de 10 a 12 s: existe, ancho 480.

En vivo (perfil aislado): marcar, crear MP4 y GIF, pegar en Discord.

## Fuera de alcance

Pintar el tramo en la barra, modo "últimos 30 s", selector de calidad, subtítulos de imagen, estilo propio en el clip, crear desde la ficha.

---

# Plan de implementación

**Ejecución:** en esta sesión, sin subagentes ni commits salvo orden (reglas del repo). Cada tarea: prueba que falla → código → prueba en verde (`repo-build-test`).

## Restricciones globales

- Sin dependencias nuevas; ffmpeg solo vía `FfmpegLocator` y `ProcesoExterno` (en `Core/`).
- Nada de disco ni procesos en el hilo de interfaz; el portapapeles sí, en el hilo de interfaz.
- Textos visibles con `T()`/`{loc:T}` en ES y EN; colores de la paleta.
- Las pruebas escriben solo en carpetas temporales (nunca en `Vídeos` real ni en `AppDataPaths`).

## Focos de revisión

1. Nombres de archivo con `'`, `[`, `,`, `;`, `:` en la ruta del filtro (prueba de integración en la tarea 2).
2. Subtítulos desfasados por `-ss` (prueba de integración en la tarea 2).
3. Pista de audio no japonesa/primera (prueba de `Construir` en la tarea 1).
4. Tramo > 30 s o invertido (prueba de `RangoValido` en la tarea 1).
5. Cerrar el reproductor con un clip en marcha (revisión de código, tarea 3).

## Tarea 1: piezas puras
Crear `SolicitudClip.cs`, `ArgumentosClip.cs`; pruebas en `AnimeLocalTracker.Tests/Services/ClipsTests.cs` (todas las unitarias de arriba salvo la del ViewModel). Rojo → implementar → verde.

## Tarea 2: servicio + integración con ffmpeg
Crear `IClipService.cs`, `ClipService.cs`, registro en DI. Pruebas de integración en `ClipsIntegracionTests.cs`. Rojo → implementar → verde.

## Tarea 3: reproductor, atajos y textos
`ReproductorViewModel.Clips.cs`, constructor (`IClipService? clipService = null` al final), `ObtenerTeclaPara`, `AppSettings.Atajos`, `ReproductorView.xaml(.cs)`, claves ES/EN. Prueba del ViewModel primero. Compilar, pruebas de Clips, Reproductor, Configuración y localización.

## Tarea 4: cierre
Suite completa; verificación en vivo con perfil aislado (marcar, crear MP4 y GIF, abrir los archivos, pegar en Discord).
