# Investigación: clips/GIF, recomendaciones locales e integraciones (Discord, scrobbling, webhooks)

Fecha: 2026-10-17. Análisis de viabilidad de cinco funciones (relacionadas con #72, #73, #90 y #91 de `propuestas-nuevas-funciones.md`).
Conclusión: las cinco son viables; orden propuesto al final.

## Piezas que ya existen

- **ffmpeg embebido** (`FfmpegLocator`, carpeta `FFmpeg/`): build 9.0.1 *full* de gyan.dev. Comprobado: filtros `subtitles`/`ass` (libass), `palettegen`/`paletteuse`; codificadores `libx264`, `h264_nvenc`, `h264_qsv`, `h264_amf`, `libvpx-vp9`, `libsvtav1`.
- `AnimeItem.MalId` ya guardado (cruce con MyAnimeList gratis); `Sinopsis` y `Generos` por anime; `SeguimientoLocal` con `Estado`, `Puntaje`, `Pendiente`.
- `SystemMediaControlsService` ya recibe título/episodio/portada y reproduciendo/pausa: mismos puntos de enganche para Discord.
- `ProtectedData` (DPAPI), como el token de AniList en `AuthService`, para cifrar secretos nuevos.
- `Microsoft.Extensions.Http.Polly` para reintentos HTTP.
- `PanicKeyService`: cualquier presencia pública (Discord) debe borrarse al pulsar la tecla de pánico.

## 1. Clips MP4 y GIF (≈1–2 días)

- Solo desde el reproductor (la ficha no tiene línea de tiempo): marcar inicio `[` y fin `]` con el tiempo actual de `MotorMpv`, exportar MP4 o GIF.
- `ClipService` lanza ffmpeg vía `ProcesoExterno` (en `Core/`), avance con `-progress pipe:1`, cancelación matando por PID. Salida en `Videos\AnimeLocalTracker` (nunca `AppDataPaths` ni la carpeta de instalación). Al terminar, `Clipboard.SetFileDropList` para pegar en Discord.
- MP4: tasa de bits calculada para caber en 10 MB (límite gratuito de Discord); 30 s → ~2,5 Mbps a 720p. `h264_nvenc`/`qsv`/`amf` si hay GPU, si no `libx264`.
- GIF en una pasada: `fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5`. Limitar a ~10 s/480 px: un GIF de 30 s pesa 15–30 MB y no cabe en Discord. MP4 como opción por defecto.
- Subtítulos: filtro `subtitles` con la pista activa en mpv (`si=N`) o el `.ass` externo; libass carga las fuentes adjuntas del MKV.
- Trampa: con `-ss` antes de `-i` los tiempos de los subtítulos se desfasan; usar `setpts=PTS+inicio/TB` antes de `subtitles` y `setpts=PTS-STARTPTS` después.
- La construcción de argumentos es una función pura: se prueba con tests unitarios.

## 2. Recomendaciones locales (≈2–3 días)

- Descartado sentence-transformers: `torch` en `AnimeTrackerTools.exe` añade 1–2 GB; ONNX Runtime + modelo multilingüe ~150 MB. Desproporcionado para cientos de animes, y las sinopsis de AniList están en inglés.
- Mejor señal: **tags de AniList con su porcentaje** (dato público, se piden una vez por anime). Requiere columna nueva → migración (`migracion-db`).
- Cálculo en C#, sin dependencias: vector = géneros + tags ponderados + TF-IDF de la sinopsis; similitud coseno.
  - "Te gustó": puntuación alta, o COMPLETED, o favorito.
  - "Pendiente en tu disco": PLANNING/CURRENT/PAUSED con episodios locales sin ver.
  - Explicación con los tags compartidos ("comparten viajes en el tiempo, thriller").
- Límite: solo recomienda lo que ya está en disco; con biblioteca pequeña, pocas sugerencias.

## 3. Discord Rich Presence (≈1 día)

- Sin NuGet: IPC por la tubería `\\.\pipe\discord-ipc-0..9`; trama = opcode (int32 LE) + longitud (int32 LE) + JSON. Handshake (op 0, `{v:1, client_id}`) y `SET_ACTIVITY` (op 1).
- Actividad tipo "Viendo" (`type: 3`), `details` = título, `state` = "Ep. 14", `timestamps` inicio/fin (Discord dibuja la barra), `large_image` = URL de portada de AniList.
- Pausa: sin `timestamps`, estado "En pausa". Saltos: recalcular. Como mucho un envío cada 5 s (detalle en `plan-discord-rich-presence.md`).
- Requiere un ID de aplicación del Discord Developer Portal. Desactivado por defecto, interruptor en Ajustes; se borra con la tecla de pánico.

## 4. Multi-scrobbler

- La sincronización actual tiene una sola marca de pendiente (`SincronizadoEnNube`, `SeguimientoLocal.Pendiente`) pensada para AniList: hace falta una cola por plataforma (tabla nueva + migración) e `IScrobbler` con una implementación por plataforma.

| Plataforma | Autenticación | Cruce de ids | Veredicto |
|---|---|---|---|
| MyAnimeList | OAuth2 PKCE (`plain`), client_id propio | `MalId` ya guardado | Sí, primero (2–3 días) |
| Simkl | PIN (dispositivo) | Acepta ids de AniList y MAL | Sí (1–2 días) |
| Kitsu | Usuario y contraseña en la app | API de mapeos | Posible, menos elegante |
| Trakt | OAuth | Temporadas de TVDB | No: numeración errónea en series largas o partidas por cour |

## 5. Webhook a Discord o Telegram (≈1 día)

- Discord: POST con `embed` (título, `thumbnail` con la portada, color, campos) a la URL del webhook. Telegram: `sendPhoto` con bot de BotFather y `chat_id`.
- Eventos: descarga completada (`DownloadService`) y serie terminada (paso a COMPLETED).
- Agrupar: varias descargas en un minuto → un solo mensaje.
- La URL del webhook es un secreto: cifrada con DPAPI. Reintentos con la política de Polly existente.

## Orden propuesto

1. Discord Rich Presence. 2. Clips MP4/GIF. 3. Webhooks. 4. Recomendaciones (migración). 5. MAL y después Simkl; Trakt descartado.
