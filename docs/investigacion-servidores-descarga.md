# Investigación: más servidores de descarga además de MP4Upload (2026-10-03)

Pregunta: ¿se pueden añadir **UPNShare, Voe y Byse** (los otros servidores que publica AnimeAV1) para que, si MP4Upload falla o no sirve, la app tenga respaldo?

Todo lo de abajo se **midió** contra los servidores reales con peticiones sueltas (una por comprobación, sin descargar episodios completos, sin simular clics, sin resolver desafíos anti-bot). No se cambió código de la app.

## Resumen

| Servidor | ¿Viable? | Por qué | Esfuerzo |
|---|---|---|---|
| **Voe** | **Sí, el mejor candidato** | Solo HTTP, sin navegador ni captcha. Entrega un MP4 directo (con reanudación) y un HLS sin cifrar. | ~1 día |
| **Mega** (bloque `downloads`, hoy excluido) | Sí, segundo | API oficial anónima, sin captcha. Mismo archivo que MP4Upload. Cuota gratis ~5 GB / 6 h por IP. | 2-3 días (hay que descifrar al bajar) |
| **UPNShare** | No por ahora | Respuestas cifradas, reproductor ofuscado de 1 MB, paso "Verifying human…". Frágil. | Alto + mantenimiento continuo |
| **Byse** | **No** | Muro anti-bot explícito (prueba de trabajo + atestación + captcha). Ya solo funciona con navegador real y en desarrollo. | No recomendable (ver nota) |
| 1Fichier / TransferIt (bloque `downloads`) | No / sin verificar | 1Fichier: captcha, esperas y 1 descarga a la vez en gratis. TransferIt: servicio nuevo de MEGA, sin verificar. | — |

## Estado actual (medido)

- La página de episodio publica hoy 4 servidores de *streaming* (`embeds`): **UPNShare, Voe, Byse, MP4Upload** (ya no aparecen HLS/zilla ni Mega ahí), cada uno para pistas SUB y DUB.
- Además publica un bloque **`downloads`** que la app **no lee**: TransferIt, Mega, 1Fichier y MP4Upload (enlaces de descarga, no de reproductor).
- `StreamExtractor.extract_stream_info` (yt-dlp 2026.08.19) responde **`Unsupported URL`** para Voe y UPNShare. yt-dlp ni siquiera tiene extractor para ellos, y su `KnownPiracyIE` rechaza a propósito hosts parecidos: **nunca habrá soporte oficial**; el mantenimiento sería 100 % del proyecto.
- Por tanto, hoy si MP4Upload falla no hay respaldo real: `ProveedorVideoAnimeAv1` recorre el resto de servidores y todos fallan al instante.
- El comentario de `browser_stream_extractor.py` (Voe "se ahoga en ~150 peticiones de publicidad") describe un intento **con navegador**. Un enfoque solo-HTTP no se había probado.

## Voe (recomendado)

Flujo medido (episodio 1 de Tokyo Revengers S4, SUB):

1. `GET https://voe.sx/e/<id>` devuelve una página mínima con una **redirección por JavaScript** a un dominio rotativo (esta vez `jeremyparticipantanything.com`; la herramienta pública `voedl` cita otros dos). El resolutor debe seguirla, no fijar el host.
2. En esa segunda página hay un `<script type="application/json">` con una cadena ofuscada (ROT13 → quitar marcadores → base64 → desplazar caracteres → invertir → base64). Decodificada es un JSON con `source` (HLS), `direct_access_url` (MP4), `title`, `file_code`…
3. **Sin captcha ni Cloudflare.** El MP4 responde `206` (acepta rangos → reanudación y troceado, como hoy) y **no exige `Referer`** (probado sin él, con el de animeav1 y con el de voe.sx). El HLS es una lista maestra **sin cifrar** (`#EXT-X-KEY` ausente).

Calidad real del mismo episodio:

| | Códec | Resolución | Tamaño | Bitrate |
|---|---|---|---|---|
| MP4Upload | **AV1** 10-bit | 1920×1080 | 206 MB | 1124 kb/s |
| Voe | **H.264** 8-bit | 1280×720 | 166 MB | 903 kb/s |

Consecuencia práctica: Voe es **más pequeño y menos nítido**, pero H.264 lo decodifica la tarjeta gráfica de cualquier equipo. En la Intel HD 620 del usuario evita justo el problema del AV1 por GPU (ver `av1_gpu_sin_imagen_tokyo_revengers`). Es un buen respaldo y una buena opción "compatibilidad".

Riesgos:
- **La ofuscación cambia.** Voe la ha rehecho varias veces: hace falta una prueba con una página guardada y un aviso claro cuando el JSON no se pueda decodificar (que caiga al siguiente servidor, no que rompa la descarga).
- **Dominio rotativo = riesgo SSRF.** Seguir la redirección solo a destinos `https` públicos (ya existe `RedirectSeguroHandler` / `UrlSeguridad.EsHostPublico`); no añadir esos dominios a la lista blanca, que hoy solo trae `voe.sx`.
- Puede limitar por IP o añadir anti-bot mañana: no se puede garantizar.

Dónde encajaría (sin tocar la arquitectura): un `voe_extractor.py` (~60 líneas) junto al de Byse, enganchado en `StreamExtractor.extract_stream_info` por dominio; devuelve `direct_access_url` y `ProveedorVideoAnimeAv1` ya lo trata como URL directa segura. `DownloadService` manda siempre `Referer: mp4upload.com` en 4 sitios: con Voe es inofensivo (el CDN no lo exige). En Configuración ya existe el desplegable de servidor preferido (`ServidorPreferidoAnimeAv1`): bastaría añadir la opción "Voe".

## Mega (segunda opción, hoy excluido a propósito)

- El enlace del bloque `downloads` (`mega.nz/file/<id>#<clave>`) responde a la **API oficial anónima** (`a:"g"`) con el tamaño (**206 MB: el mismo archivo AV1 1080p que MP4Upload**) y una URL temporal. Sin captcha.
- Los archivos de Mega vienen **cifrados**: hay que descifrar al bajar (AES-CTR, que sí permite rangos). Existe la biblioteca .NET `MegaApiClient`, pero `DownloadService` hoy baja archivos en claro: es un cambio de verdad (2-3 días).
- Cuota gratuita de MEGA: del orden de **5 GB cada 6 h por IP** (cifra de fuentes secundarias, no publicada por MEGA). Una temporada de 12 episodios de ~200 MB (≈2,4 GB) cabe, pero dos seguidas pueden toparse con el límite (error 509).
- Valor: **no mejora la calidad** (es el mismo archivo), pero da independencia si MP4Upload cae.

## UPNShare (no por ahora)

- `animeav1.uns.bio/#<id>` es una SPA vacía (`Loading…`). Su API (`/api/v1/video`, `/api/v1/info`, `/api/v1/player`, `/api/v1/download`) responde con **blobs hexadecimales cuyo tamaño es múltiplo de 16 bytes** (coherente con un cifrado de bloque): el reproductor los descifra en el cliente.
- El JavaScript (≈1 MB, cadenas en matriz ofuscada, nombre con hash que cambia cada versión) menciona "Verifying human…" y "Cloudflare" en el flujo de descarga.
- No se intentó descifrar: sería ingeniería inversa de un reproductor ofuscado que cambia a cada despliegue, y la prueba previa con navegador (documentada en el código) no obtuvo ninguna petición de video. Mucho coste y mantenimiento por un servidor más.

## Byse (descartar)

- `byselapuix.com/e/<id>` también es una SPA. `embed/details` devuelve metadatos en JSON; `embed/playback` rechaza el acceso directo (405). Según el código existente, el video solo se autoriza tras una cadena **desafío (prueba de trabajo) → atestación → captcha → verificación**.
- Eso es un muro anti-bot diseñado para impedir justo el acceso automatizado. No es una obfuscación que decodificar sino una verificación humana, y **no recomiendo construir nada para saltársela**.
- Hoy ya está deshabilitado en el `.exe` empaquetado (falla el 100 %), y Playwright se sacó del paquete. Opciones: dejarlo como está (cuesta milisegundos fallar) o retirarlo de la lista de preferencia para no intentarlo.

## Otros hosts del bloque `downloads`

- **1Fichier:** en gratis hay captcha, esperas y una sola descarga a la vez; no apto para automatizar.
- **TransferIt (`transfer.it`):** servicio nuevo de MEGA sin cuenta. Las fuentes dicen que era gratis "hasta el 1-ene-2026, luego podrían aplicarse límites"; no se pudo verificar su estado ni su API. Si algún día se implementa Mega, se podría estudiar aparte porque comparten la criptografía.

## Recomendación y plan

1. **Fase 1 — Voe** (1 día): `voe_extractor.py` + prueba con página guardada + opción "Voe" en el desplegable de servidor preferido + aviso de calidad (720p H.264). Es la única que da respaldo real, rápido y sin infraestructura nueva.
2. **Fase 2 — Mega** (2-3 días, solo si hace falta independencia de MP4Upload): leer el bloque `downloads`, descifrar en `DownloadService`, manejar el error 509.
3. **No hacer:** UPNShare (coste/mantenimiento) ni Byse (muro anti-bot).

### Fase 1 implementada (2026-10-03): Voe como respaldo automático

- `tools/python/resolvers/voe_extractor.py` + enganche por dominio en `StreamExtractor.extract_stream_info` (junto a Byse). Devuelve `direct_access_url` (MP4) y, si no hay, `source` (HLS). Sigue la redirección por JS (máx. 4 pedidas) validando cada destino: solo `https` hacia servidores públicos (IP literal, nombres de intranet o dominios que resuelven a IP privada se rechazan sin pedirlos).
- Sin cambios de lógica en C#: `ProveedorVideoAnimeAv1` ya recorría los servidores en orden (MP4Upload → HLS → Voe → …) y `DownloadService` descarga cualquier URL `https` pública; solo se corrigieron comentarios que decían "sin extractor".
- Pruebas: 14 nuevas en `tests/test_voe_extractor.py` (decodificación, salto JS, 3xx, HLS de reserva, destinos no públicos, límite de saltos, errores) + enrutado en `test_stream_extractor.py`. Verificado en vivo con el daemon empaquetado: SUB y DUB del ep. 1 resuelven un MP4 con `206`.
- El `.exe` del daemon (`AnimeLocalTracker/Tools`, ignorado por git) se regeneró con `build_binary.py`; hay que repetirlo al empaquetar una release.
- Límite conocido: el respaldo actúa cuando MP4Upload **no se puede resolver** (embed roto, sin video). Si MP4Upload resuelve pero la descarga se corta por red, siguen los reintentos de siempre (y el torrent si está activado), no Voe.

### Fase 2 implementada (2026-10-03): TransferIt y Mega

- **Lectura de la página:** `AnimeAv1HtmlParser.ExtraerDescargas` lee el bloque `downloads` (la app solo leía `embeds`); de él solo se usan TransferIt y Mega (MP4Upload ya sale de los embeds y 1Fichier pide captcha). `transfer.it` se añadió a la lista blanca de hosts de `UrlSeguridad`.
- **Orden:** MP4Upload → HLS → **TransferIt → Mega → Voe** → UPNShare → Byse. TransferIt y Mega entregan el MISMO archivo (AV1 1080p) que MP4Upload, así que van antes que Voe (H.264 720p). Cambiarlo es una línea (`OrdenarEmbedsPorPreferencia`) y el desplegable de Configuración permite forzar cualquiera.
- **TransferIt** (`MegaTransferIt` + `AnimeAv1VideoSourceResolver.ResolverTransferItAsync`): la API `bt7.api.mega.co.nz` lista la transferencia (`a:f`) y da la URL temporal (`a:g`); el archivo llega **en claro**, así que entra por la descarga normal (por trozos y reanudable).
- **Mega** (`ResolverMegaAsync`): la API anónima da la URL temporal del archivo **cifrado**. La clave del enlace viaja en el fragmento de la URL (`#mega=…`; un fragmento nunca se envía por HTTP), el archivo se baja por la descarga normal y `DownloadService` lo descifra al terminar (AES-CTR; se escribe a un `.dec` y se sustituye al final; si ya es un contenedor de video en claro no se vuelve a descifrar, así un corte en mal momento no deja basura).
- **Cuota de MEGA:** antes de devolver la URL se pide el primer byte; si MEGA contesta **509** (cuota gratuita agotada) se salta al siguiente servidor en vez de fallar a mitad de la descarga.
- **Verificado contra los servidores reales:** episodio completo (206 358 939 bytes) por TransferIt en 17 s y por Mega en 20 s; **SHA-256 idéntico** (el descifrado de Mega coincide byte a byte con el archivo en claro de TransferIt) y ambos son AV1 1080p de 1469 s. 63 pruebas nuevas, con vectores de cifrado generados con PyCryptodome.
- **No se hizo:** verificación del MAC de integridad de Mega (el archivo se valida por tamaño y por la huella comparada arriba); una sola descarga completa por servidor, así que la cuota real de TransferIt no se midió.

### Orden de servidores configurable (2026-10-03)

- **Configuración → Reproducción → "Orden de los servidores (AnimeAV1)"**: lista numerada con flechas subir/bajar y botón "Orden predeterminado" (los cambios se aplican al pulsar *Guardar Preferencias*). Sustituye al desplegable de "servidor preferido".
- **Se guarda en el mismo ajuste** (`ServidorPreferidoAnimeAv1`) como lista separada por comas (`Voe,MP4Upload,TransferIt,Mega`); `null` si es el orden por defecto (MP4Upload → TransferIt → Mega → Voe), así un cambio futuro del predeterminado también le llega. Un valor antiguo de un solo servidor (`Mega`) sigue significando "este primero", sin migración.
- **Semántica:** los listados se prueban primero y en ese orden; el resto (HLS, UPNShare, Byse y lo que falte) después, como siempre. Si uno falla, se pasa al siguiente. Solo se pueden ordenar los 4 que resuelven hoy.
- Código: `Models/OrdenServidores.cs`, `ServidorOrdenItem` + comandos en `ConfiguracionViewModel`, `OrdenarEmbedsPorPreferencia` acepta la lista. Verificado en la app real por UIA (orden inicial = el guardado, flechas de los extremos desactivadas, "Bajar" y "Orden predeterminado" funcionan) y con captura de pantalla.

Decisiones que son tuyas:
- ¿Quieres Voe como **respaldo automático** (solo si MP4Upload falla) o también como opción **preferida** para equipos sin AV1 por hardware?
- ¿Vale la pena Mega aunque no mejore la calidad?

## Lo que NO se verificó

- No se descargó ningún episodio completo ni se probó la estabilidad de Voe a lo largo de días/semanas.
- No se probó desde otra red ni con muchas peticiones seguidas (límites por IP).
- Las cifras de cuota de MEGA y las condiciones de 1Fichier/TransferIt vienen de fuentes secundarias.
- No se tocó código de la app.
