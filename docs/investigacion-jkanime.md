# Investigación: JKAnime como segundo proveedor además de AnimeAV1 (2026-10-03)

Pregunta: ¿se puede añadir <https://jkanime.net/> como otra fuente de video, para que la app deje de depender de un solo sitio?

Todo lo de abajo se **midió** contra el sitio real con peticiones sueltas (búsqueda, lista de episodios, página de episodio, y resolución de tres de sus servidores con el código actual de la app). No se descargó ningún episodio completo ni se cambió código.

## Veredicto

**Es viable y el esfuerzo es moderado (~2,5-3,5 días)**, porque JKAnime publica los videos en servidores que la app **ya sabe resolver** (MP4Upload, Mega, Voe). No hace falta ningún extractor nuevo para empezar.

| Aspecto | Resultado |
|---|---|
| Acceso | Detrás de Cloudflare pero **sin desafío ni captcha**: responde a una petición HTTP normal (0,3-0,8 s; 8 peticiones seguidas sin límite) |
| Buscar un anime | `GET /buscar/<término>/` (HTML con los slugs) |
| Lista de episodios | `POST /ajax/episodes/<id>/<página>` con el token CSRF de la página (patrón normal de Laravel con cookies de sesión) |
| Servidores de un episodio | En la página `/<slug>/<n>/`: `var servers = [...]` con la URL del embed en **base64 simple** (sin cifrar) |
| Servidores ya resueltos por la app | **MP4Upload, Mega, Voe** (comprobado en vivo) |
| Riesgo principal | La página **no trae ID de MAL ni de AniList**: hay que identificar el anime por nombre |

## Cómo funciona el sitio (medido)

1. **Búsqueda:** `/buscar/tokyo%20revengers/` devuelve las páginas candidatas (`tokyo-revengers`, `tokyo-revengers-santen-sensou-hen`, …). Los slugs siguen la convención de títulos tipo MAL, la misma que AnimeAV1.
2. **Página del anime** (`/<slug>/`): trae el id interno (`data-anime="251"`) pero **no los episodios**: se cargan por AJAX. El número "Episodios" de la ficha no es fiable en series en emisión (Tokyo Revengers S4 mostraba 0).
3. **Lista de episodios:** `POST https://jkanime.net/ajax/episodes/251/1` con `_token` (de `<meta name="csrf-token">`, más las cookies de la sesión). Devuelve JSON paginado de 16 en 16: `total`, y por episodio `number`, `title`, `image`, `timestamp`. Hay también `/ajax/search_episode/<id>/<n>`.
4. **Página de episodio** (`/<slug>/<n>/`): **404 limpio** si el episodio no existe. Si existe, trae `var servers = [{slug, server, lang, size, remote}]`; `remote` es la URL del embed en base64. Además trae reproductores propios (pestañas "Desu", "Magi" → `/jkplayer/…`, con parámetros cifrados) que no se exploraron.

## Servidores que publica (episodio 1 de Tokyo Revengers S4)

Once servidores, todos con el mismo archivo (398 MB declarados):

| Servidor | ¿Lo resuelve la app hoy? | Notas |
|---|---|---|
| **Mp4upload** | **Sí** | Resuelto con el extractor actual: **H.264 1080p, 418 MB** |
| **Mega** | **Sí** | Enlace `mega.nz/embed/ID#CLAVE` (ya lo entiende `MegaTransferIt`); la API dio **418 MB** |
| **VOE** | **Sí** | Resuelto con `voe_extractor.py`: H.264 **720p**, 173 MB |
| Filemoon | No | Es `bysekoze.com`, de la misma familia que Byse (verificación anti-bot): descartado |
| 1Fichier | No | Captcha y esperas en gratis |
| Mediafire, Streamtape, Streamwish, Vidhide, Mixdrop, Doodstream | No | Cada uno necesitaría su extractor (varios con JavaScript empaquetado). **No se probaron.** No hacen falta: ya hay tres espejos del mismo archivo |

## Comparación con AnimeAV1 (mismo episodio)

| | Códec | Resolución | Tamaño |
|---|---|---|---|
| AnimeAV1 (MP4Upload / TransferIt / Mega) | **AV1** 10-bit | 1080p | 206 MB |
| **JKAnime (MP4Upload / Mega)** | **H.264** | 1080p | **418 MB** |
| Voe (ambos sitios) | H.264 | 720p | ~170 MB |

Consecuencia práctica: el archivo de JKAnime es **el doble de grande pero mucho más compatible**: la tarjeta gráfica decodifica H.264 en cualquier equipo (en la Intel HD 620 del usuario evita el AV1 por software y el caso de Tokyo Revengers). La calidad varía según el anime: Dragon Ball Z (antiguo) declaraba solo 59 MB por episodio.

Idiomas (`lang`): `1` = *Japonés Sub. Español*, `3` = *Español latino* (equivalen al SUB/DUB de la app). En las dos páginas vistas cada episodio mostraba **un solo idioma**; no se verificó cómo expone el sitio un anime que tiene ambos.

Cobertura probada con 8 títulos de la biblioteca (Kage no Jitsuryokusha, Dungeon Meshi, Re:Zero, Grand Blue, Black Clover, Dragon Ball Z Kami to Kami, Isekai Quartet, One Piece): **todos encontrados**.

## Riesgos

1. **Identificar el anime sin MAL ID** (el mayor). AnimeAV1 permite comprobar el MAL ID exacto; aquí solo hay nombres, slug y número de episodios. La app ya tiene la maquinaria de comparación (`FirmaTitulo`, `TituloSimilaridad`, desfase por precuelas), y los slugs coinciden con los de AnimeAV1, pero habrá más casos dudosos. Mitigación: guardar la página verificada de cada anime (como ya se hace) y exigir que el episodio pedido exista en la lista.
2. **Espejos compartidos:** MP4Upload, Mega y Voe son los mismos hosts. Un segundo *sitio* quita el punto único de fallo de AnimeAV1, pero **no** protege si cae MP4Upload.
3. **Cloudflare / CSRF:** hoy no hay desafío, pero el sitio puede endurecerse. Hace falta sesión con cookies y token (más frágil que el HTML estático de AnimeAV1).
4. Los hosts heredados (Voe, Mega) conservan sus propios riesgos (cambio de ofuscación, cuota de MEGA).

## Cómo encajaría (arquitectura)

- **Nuevo `ProveedorVideoJkAnime : IProveedorVideo`** registrado junto a `ProveedorVideoAnimeAv1`; el `OrquestadorMultiProveedor` ya los prueba por prioridad y con *cooldown* ("no está" no cuenta como fallo, justo lo que hace falta para un segundo sitio).
- **Reutilizar la resolución por servidor:** hoy vive dentro de `ProveedorVideoAnimeAv1` (MP4Upload, TransferIt/Mega, daemon). Se extraería a un componente compartido para no copiarla; JKAnime solo aportaría la lista de servidores `(nombre, URL)`.
- **Cliente y parser nuevos** (`JkAnimeClient`, `JkAnimeHtmlParser`, con fixtures como `AnimeAv1HtmlParser`): búsqueda, token CSRF + cookies, lista de episodios por POST, `var servers` (base64).
- **Seguridad:** las URLs de embed salen de un base64 de terceros → pasarlas por la misma lista blanca de hosts (`UrlSeguridad.EsUrlEmbedPermitida`); el destino de descarga ya se valida en `DownloadService`.
- **Ajustes:** el orden de servidores configurable ya aplica por nombre dentro de cada proveedor. Falta decidir el **orden entre sitios** (ver abajo).

## Plan por fases

| Fase | Contenido | Estimación |
|---|---|---|
| 1 | Cliente + parser + identificación del anime + pruebas con fixtures | ~1 día |
| 2 | Extraer la resolución por servidor, `ProveedorVideoJkAnime`, orquestador, inyección de dependencias y pruebas | ~1 día |
| 3 | Idioma (lang 1/3 → SUB/DUB), ajuste de orden entre proveedores, verificación de una descarga real | ~0,5-1 día |
| 4 (opcional) | Extractores de Mediafire/Streamtape/Streamwish/Mixdrop (más espejos) | ~1 día cada uno; **no recomendado** |

## Implementado (2026-10-03)

- **`ProveedorVideoJkAnime`** (+ `JkAnimeClient`, `JkAnimeHtmlParser`) como segundo proveedor del `OrquestadorMultiProveedor`. Busca por nombre con el buscador del sitio, lee `var servers` (URLs en base64) y resuelve con el componente compartido `ResolvedorServidoresVideo`, extraído de `ProveedorVideoAnimeAv1` sin cambiar su comportamiento: MP4Upload, TransferIt/Mega y Voe. Los demás servidores del sitio se ignoran por la lista blanca.
- **Sin sesión ni token:** en vez de la lista AJAX de episodios (POST con CSRF) se pide directamente `/<slug>/<n>/` (404 si no existe). Menos piezas frágiles; el número de episodio de la app coincide con el del sitio (cada temporada/parte es su propia página).
- **Identificación sin MAL ID:** `FirmaTitulo` + `TituloSimilaridad` (umbral 0,75, rechaza "misma serie, otra temporada"). Solo se prueban las páginas que **empatan con la mejor**: si el episodio 5 de la temporada buscada aún no existe, NO se baja el 5 de una serie de nombre parecido. La página verificada de cada anime se recuerda en memoria (por AniListId) durante la sesión.
- **Orden entre sitios configurable:** Configuración → Reproducción → "Orden de los sitios de video" (lista con flechas). Se guarda en `OrdenProveedoresVideo` (lista con comas; nulo = AnimeAV1 primero) y el orquestador lo lee en cada búsqueda, sin reiniciar. Reutiliza la misma fila de la lista del orden de servidores.
- **Verificado contra el sitio real:** 6 títulos (Tokyo Revengers S4 y la serie original, Grand Blue S3, Re:Zero 2nd Season, One Piece, Dungeon Meshi) eligieron la página correcta con **una sola petición de episodio**; un episodio inexistente (ep. 99) dio nulo sin tocar otras series; dos de ellos cayeron a Mega porque su MP4Upload no resolvía (la cascada funciona); un episodio completo bajó por Mega descifrado: **417 727 102 bytes en 48 s, H.264 1920×1080, 1469 s**.
- **Lección:** el primer intento real falló (0 resultados) porque mis fixtures tenían el espacio en blanco colapsado y el HTML real parte las etiquetas en varias líneas (`<a` y `href` separados). Los fixtures de HTML llevan ahora el espacio real.
- **Pendiente / límites:** el idioma (lang 1 → SUB, 3 → DUB) se traduce, pero no se verificó cómo expone el sitio un anime con ambos; la página verificada no se guarda en la base de datos (tras reiniciar se vuelve a buscar, ~0,4 s); no se probó una descarga completa por MP4Upload de JKAnime (sí su resolución y formato).

## Los demás servidores de JKAnime (medido el 2026-10-03)

Pregunta: ¿se pueden incorporar los servidores que JKAnime publica y que la app aún no resuelve? Se probaron con peticiones sueltas (sin simular clics ni resolver desafíos) sobre el episodio 1 de Tokyo Revengers S4. **Velocidad:** una sola conexión, 20 MB, una vez, desde esta red: es orientativa (la app descarga por trozos en paralelo, así que va más rápido que estas cifras).

| Servidor | Calidad real | Cómo se resuelve | Velocidad (1 conexión) | Veredicto |
|---|---|---|---|---|
| **Mediafire** | **H.264 1080p, 418 MB** (el mismo archivo que MP4Upload/Mega de JKAnime) | Página → enlace `aria-label="Download file" href` = **descarga directa** (acepta rangos) | **8,6 MB/s** | **Recomendado** (fácil, rápido, sin cuota de MEGA) |
| **Vidhide** (`callistanise.com`…) | **HLS 480/720/1080p**, sin cifrar | Página → JavaScript empaquetado (p.a.c.k.e.r) → `links.hls4/hls2/hls3` (ruta relativa al host) | **7,5 MB/s** | **Recomendado** (medio; ver nota de HLS) |
| Streamwish (`flaswish.com`, `sfastwish.com`) | HLS 720p, sin cifrar | **El mismo motor que Vidhide** (mismo extractor) | 0,4 MB/s | Entra gratis con Vidhide, pero es lento |
| Streamtape | H.264 **1080p, 234 MB** (encode más ligero) | Página → `robotlink` (concatenación + `substring`) → `get_video` → CDN `tapecontent.net` (acepta rangos) | 2,7 MB/s | Opcional (fácil, pero la ofuscación cambia a menudo) |
| Mixdrop (`mxdrop.top`) | H.264 720p, 197 MB | Página → empaquetado → `MDCore.wurl` (MP4 firmado, sin captcha; el "captcha" que aparece es solo texto) | **0,1 MB/s** | No recomendado (muy lento) |
| Reproductores propios (`jkplayer/um`, `/umv`) | **H.264 1080p** (HLS, 423 segmentos pequeños) | El iframe publica la lista `.m3u8` en claro en su CDN (`playmudos.com`) | 0,4 MB/s | Posible, pero lento y específico del sitio |
| Filemoon (`bysekoze.com`) | — | SPA de la familia **Byse** (verificación anti-bot) | — | **No** (mismo motivo que Byse) |
| Doodstream (`dsvplay.com`) | — | **No respondió** desde esta red (tiempo agotado); yt-dlp rechaza esta familia | — | No |
| 1Fichier | — | Error de certificado en la prueba; además captcha y esperas en gratis | — | No |

**Para la comparación:** MP4Upload de JKAnime dio 0,2 MB/s en la misma prueba (el usuario ve 2-3 MB/s en la app con varias conexiones); Mega ya va a ~9 MB/s en descarga completa. Mediafire y Vidhide son **tan rápidos como Mega**, sin el límite de ~5 GB cada 6 h de MEGA.

### Implementado (2026-10-03): Mediafire, Vidhide y Streamwish

- **Extractores del daemon de Python** (como Voe): `mediafire_extractor.py` (enlace de descarga directa del botón; también la variante ofuscada en base64) y `packed_extractor.py` (Vidhide y Streamwish: JavaScript empaquetado p.a.c.k.e.r → `links.hls4/hls3/hls2`, listas HLS sin cifrar). Comparten con Voe un módulo común (`http_seguro.py`): cada destino se valida (solo https hacia servidores públicos) y las redirecciones se siguen a mano con tope.
- **Espejos de Streamwish:** publica dos listas en dominios distintos y una (`hls3`) da 404 sin el `Referer` de la página, que el daemon no manda al descargar HLS. El extractor prueba cada candidato (en orden de calidad) y se queda con el primero que responde solo; si ninguno contesta, no descarta el video.
- **Dominios que rotan:** Vidhide y Streamwish cambian de dominio (`vidhidevip.com`, `callistanise.com`, `flaswish.com`, `sfastwish.com`…). Manda el **nombre del servidor** que da JKAnime: `resolve-stream` recibe ahora `server` junto a la URL, y `UrlSeguridad.EsEmbedDeServidorPermitido` admite cualquier https público solo si el sitio lo llama Vidhide o Streamwish (un nombre desconocido no abre la puerta; la IP local, redes privadas y URLs con usuario siguen rechazadas). Mediafire se añadió a la lista blanca fija.
- **Orden por defecto:** MP4Upload → HLS → TransferIt → Mega → **Mediafire → Vidhide** → Voe → **Streamwish** → UPNShare → Byse. La lista de Configuración pasó de 4 a 7 servidores ordenables (un ajuste guardado antiguo sigue valiendo: los listados primero y el resto detrás).
- **Arreglo de un fallo latente del camino HLS:** yt-dlp escribía ~1800 líneas de progreso por la salida estándar del daemon y el puente de C# intentaba interpretar toda la salida como un único JSON → "respuesta vacía del daemon". Nadie lo había visto porque el HLS de AnimeAV1 nunca funcionó (Cloudflare). Ahora el daemon usa `noprogress` y el puente toma la **última línea JSON** (`PythonBridgeService.InterpretarSalida`).
- **Verificado contra los servidores reales (proveedor → daemon real → `DownloadService`):** Mediafire 417 727 102 bytes en 35 s (12 MB/s), H.264 1920×1080; Vidhide (HLS) 287 375 807 bytes en 26 s (11 MB/s), H.264 1920×1080, 1469 s. Streamwish resuelve (espejo sin Referer); no se descargó completo por ser lento (~0,4 MB/s). 100 pruebas de Python y 2592 de C# en verde.
- **Sin verificar:** vigencia de los enlaces firmados; límites de Mediafire tras varias descargas; descarga completa de Streamwish; la barra de progreso HLS sigue sin existir (el comando es bloqueante).

### Qué costaría

| Pieza | Trabajo | Notas |
|---|---|---|
| Mediafire | ~0,5 día | Un extractor pequeño (HTTP + una expresión regular); el archivo directo entra por la descarga normal (trozos, reanudación). |
| Vidhide + Streamwish | ~1 día | Un desempaquetador del JavaScript + leer `links`; la descarga HLS ya existe (daemon/yt-dlp), pero **no se probó de punta a punta** y no tiene barra de progreso. Sus dominios **rotan** (`callistanise.com`, `flaswish.com`, `sfastwish.com`, `vidhidevip.com`…): no valen en una lista blanca fija; se aceptaría cualquier destino `https` público validado (como con Voe). |
| Streamtape | ~0,5 día | Extractor pequeño; la ofuscación cambia cada pocos meses. |
| Reproductor propio | ~0,5-1 día | Específico de JKAnime y lento. |

**Dónde encajarían:** como extractores del daemon de Python (como Voe), sin cambios en el orquestador; solo servirían para episodios de JKAnime (AnimeAV1 no publica estos hosts). **Recomendación:** Mediafire + Vidhide/Streamwish (~1,5 días). Dejar fuera Mixdrop, Doodstream, 1Fichier y Filemoon.

### Lo que NO se verificó en esta ronda

- Ninguna descarga completa por estos servidores (solo resolución, formato y 20 MB de velocidad).
- La descarga HLS de Vidhide con el daemon (yt-dlp) de punta a punta.
- Cuánto duran los enlaces firmados (Mixdrop, Streamtape, Mediafire) ni si Mediafire limita tras varias descargas.
- Doodstream y 1Fichier: el fallo puede ser de esta red/entorno, no del servicio.

## Decisiones tuyas

1. **¿Quieres JKAnime como segundo proveedor?** (es el trabajo de las fases 1-3).
2. **Orden entre sitios:** ¿AnimeAV1 primero (archivo AV1 de la mitad de tamaño) o JKAnime primero (H.264, compatible con tu tarjeta)? Lo natural es un ajuste en Configuración, como el de servidores.
3. **¿Más hosts** (Mediafire, Streamtape…)? Mi recomendación es no.

## Lo que NO se verificó

- No se descargó ningún episodio completo de JKAnime (se comprobó la resolución y el formato con ffprobe).
- Los seis hosts nuevos (Mediafire, Streamtape, Streamwish, Vidhide, Mixdrop, Doodstream) ni se abrieron.
- Cómo publica el sitio un anime con SUB y DUB a la vez; los reproductores propios (`jkplayer`); la calidad de más de dos animes.
- Comportamiento con mucha carga (solo 8 peticiones seguidas) o desde otra red.
- No se tocó código de la app; los archivos temporales de la investigación se borraron.
