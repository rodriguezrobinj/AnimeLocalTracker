# Investigación: fuentes de datos para los minijuegos

> **Estado: investigación (no implementado).** Pruebas hechas el 2026-09-25 desde esta máquina (Windows, red
> doméstica) con `curl` y, para licencias y límites, documentación oficial y búsquedas web. Complementa
> [investigacion-minijuegos.md](investigacion-minijuegos.md).
>
> Las APIs de la comunidad cambian y se caen: **repetir las pruebas antes de implementar**.

## Resumen ejecutivo

- La mejor combinación encontrada: **AniList** (personajes, pistas, imágenes de escenas) + **AnisongDB** (audio de
  openings/endings con dificultad) + **base local de la app** (funciona sin red) + **anime-offline-database**
  (conjunto grande de títulos para distractores, también offline).
- **AnimeThemes estuvo caída el 25-sep (HTTP 522) y el 26-sep ya responde** (200 en <1 s, audio `.ogg` descargable).
  Sigue con un fallo parcial: `song.artists` da 500; los artistas se piden por `song.performances.artist`
  (corregido en `AnimeThemesService`). Con AnisongDB hay además una alternativa real para el audio.
- **Sin clave ni registro** funcionan AniList, AnisongDB, Kitsu, Jikan (parcial), trace.moe, AnimeChan, Bangumi y
  ANN. Con clave gratuita: TMDB (no probada aquí).

## Qué se probó y qué salió

| Fuente | Resultado de la prueba (2026-09-25) | Límite / condición |
|---|---|---|
| **AniList** (GraphQL) | ✅ Funciona. Devuelve etiquetas con rango y marca de spoiler, sinónimos multi-idioma, banner, color de portada, personajes (con nombres alternativos y **nombres-spoiler**), actores de voz, staff y `streamingEpisodes` | **30 peticiones/min** hoy (cabecera `X-RateLimit-Limit: 30`; el "normal" de 90 lleva tiempo degradado). Pasarse = 1 min de bloqueo |
| **AnisongDB** (`anisongdb.com/api`) | ✅ Funciona. Búsqueda por anime → openings/endings/inserts con canción, artista, compositor, **dificultad**, duración e **IDs cruzados** (MAL, AniList, AniDB, Kitsu). *Slime*: 39 temas | Proyecto comunitario, sin contrato ni documentación oficial |
| **Audio de AnisongDB** (CDN de AnimeMusicQuiz) | ✅ `.mp3` ~320 kbps; soporta `Range` (respuesta 206) → se puede pedir solo un tramo | Es el CDN de otro proyecto: uso ligero y con caché; sin garantías |
| **Jikan** (MyAnimeList no oficial) | ⚠️ `/anime/{id}` respondió bien, pero `/characters`, `/pictures` y `/random` dieron **504** ("MyAnimeList may be down") en los 3 intentos | 60 peticiones/min, respuestas en caché 24 h. Depende de que MAL responda |
| **Kitsu** (JSON:API) | ✅ Metadatos, póster, portada, `youtubeVideoId`, rankings de popularidad y nota. Sin miniaturas de episodio en el anime probado; el endpoint de personajes devolvió vacío | Sin clave; no se vieron límites |
| **AnimeThemes** | 25-sep: ❌ **522** en la raíz y en la API (~20 s por intento, además de los timeouts de tu `app.log`). 26-sep: ✅ 200 en <1 s; `.ogg` de 3,5 MB descargado en ~3,5 s. ⚠️ `include=song.artists` → **500** en todos los animes probados; funciona `song.performances.artist` | Ya integrada en la app (emparejamiento por ID de AniList, ver hallazgo 7). Estuvo caída una semana: no depender de ella en línea, cachear |
| **trace.moe** | ✅ Identificó una captura de *Slime* con similitud 0.996: anime, episodio, segundo exacto y vídeo/imagen de vista previa | Anónimo: concurrencia 1 y cuota mensual (`/me` mostró 100; la documentación habla de más); clave opcional |
| **AnimeChan** | ✅ Frases aleatorias con anime y personaje | **100 peticiones/día** gratis (1 000/hora de pago) |
| **anime-offline-database** (GitHub) | ✅ Volcado semanal de todo el catálogo; última versión `2026-27`, **62 MB** (6 MB comprimido `.zst`) | Licencia **ODbL + DbCL**: exige atribución y compartir igual las bases derivadas |
| **Bangumi** | ✅ Datos y nombres en chino, imágenes, etiquetas | Idioma; útil como complemento, no como base |
| **Anime News Network** (XML) | ✅ Responde; enciclopedia antigua con relaciones y créditos | XML, límites no verificados |
| **MyAnimeList oficial** | ❌ 403 sin *client id* (requiere registrar una app) | No hace falta si se usa AniList |
| **Shikimori** | ↪️ 301 (cambió de dominio); no se siguió | Sin interés claro |
| **Danbooru** y similares (imágenes de personajes) | ❌ Sin respuesta útil | **No recomendadas**: contenido para adultos mezclado |
| **TMDB** | No probada (pide clave gratuita) | Tiene *backdrops* reales de series y exige atribución |

## Hallazgos que cambian el diseño

1. **Fotogramas de escenas sin tener el archivo:** `streamingEpisodes` de AniList da miniaturas **de escena** de cada
   episodio (imágenes JPEG de 640×360 de Crunchyroll; se descargó y se comprobó que es una escena real). Es la
   fuente online para "adivina el anime por una escena" y para "adivina el personaje". Cuando no hay red, se usan
   los fotogramas de tus propios episodios.
2. **Dificultad con datos reales:** AnisongDB trae `songDifficulty` (14–78 en las pruebas), calculada por
   AnimeMusicQuiz a partir de cuánta gente acierta. Sirve para armar niveles fácil/medio/difícil de openings sin
   inventar criterios.
3. **Audio por tramos:** el CDN acepta `Range`, así que un clip de 10 s pesa unos ~400 KB en vez de bajar la canción
   completa (3,5 MB).
4. **Spoilers ya marcados:** AniList indica qué etiquetas son spoiler (`isMediaSpoiler`) y separa los alias de
   personaje que revelan la trama (`alternativeSpoiler`). Las pistas deben excluirlos.
5. **Un pool grande sin pedir nada:** el volcado de anime-offline-database (mejor su versión de AniList o una lista
   filtrada por popularidad, no los 62 MB completos) da distractores creíbles offline.
6. **Cuidado con el presupuesto de AniList:** una partida de 10 rondas con una consulta por anime son ~10
   peticiones de las 30/min disponibles. Hay que agrupar y cachear en SQLite.
7. **Precisión del emparejamiento de OP/ED con AnimeThemes (medida el 26-sep con los 207 animes de la biblioteca):**
   la app no busca por título sino por **ID de AniList** (`/resource?filter[site]=AniList&filter[external_id]=…`).
   - 153 de 207 quedaron emparejados y **ninguno con el anime equivocado**: los 2 títulos poco parecidos
     (*Kami no Tou* / *Kami no Tou: Tower of God*, *Honzuki no Gekokujou…*) son solo nombres más cortos o más largos
     del mismo anime y año.
   - 49 sin mapeo: casi todo películas (One Piece, Dragon Ball), OVAs/especiales, "Break Time" de Re:Zero y
     temporadas aún sin estrenar. Devolver vacío es lo correcto (no hay tema que traer).
   - **Fallos por omisión (nunca por mezcla) — corregidos el 26-sep:** `ResolverSlugAsync` solo miraba el primer
     *resource* y el primer anime. *Grand Blue Season 3* (199111) devuelve un resource vacío y otro con el anime, así
     que se perdía; *Re:Zero OVAs* (100049) tiene 2 resources con 2 animes distintos y solo se leía uno. Ahora
     `ResolverSlugsAsync`/`ExtraerSlugs` leen todos y `MapearVarios` junta sus temas; el segundo anime lleva sufijo
     en el slug (`ED1-2`) porque ambos OVAs tienen un "ED1" v1 y compartirían archivo local.
   - Calidad de los 565 temas de los 155 animes: 0 sin título de canción, 2 sin audio, **148 (26 %) sin artista**
     (p. ej. One Piece OP3–OP10, por lo que "¿quién lo canta?" debe omitirse cuando falte), 37 con marca de spoiler
     y **73 temas con varias versiones con audio** (el mapeo genera una fila por versión: para un juego hay que
     deduplicar por `Slug`).

## Qué fuente para qué juego

| Juego | Fuente principal | Respaldo | Offline |
|---|---|---|---|
| Adivina el anime (pistas) | Base local de la app (`AnimeItem`, `DatosExtraAnime`) | AniList (etiquetas, sinónimos) | ✅ |
| Adivina el anime (escena) | Fotogramas de tus episodios (daemon Python) | AniList `streamingEpisodes` | ✅ / online |
| Adivina el OP/ED | **AnisongDB** (audio, dificultad, IDs) | AnimeThemes (de nuevo operativa desde el 26-sep; sin dificultad y ~26 % de temas sin artista) | Con clips cacheados |
| Adivina el personaje | **AniList** (personajes, VA, nombres alternativos) | Jikan (poco fiable hoy) | Con caché |
| Frase → anime/personaje (reto diario) | AnimeChan (100/día) | — | Con caché |
| Distractores y catálogo | anime-offline-database (subconjunto) | AniList populares | ✅ |
| Verificar una captura | trace.moe (opcional, cuota baja) | — | ❌ |

## Riesgos

| Riesgo | Detalle | Mitigación |
|---|---|---|
| Servicios comunitarios inestables | AnimeThemes caída el 25-sep y con un include roto el 26-sep; Jikan con 504; AnisongDB y su CDN sin contrato | Caché local de todo lo usado, cada juego con al menos una fuente offline, mensajes claros si no hay red |
| Hotlinking a CDN ajenos (AMQ, Crunchyroll, AniList) | Ancho de banda de terceros | Descargar solo lo necesario, cachear en `AppDataPaths`, `User-Agent` propio, no empaquetar contenido |
| Licencias | ODbL (atribución + compartir igual) en anime-offline-database; TMDB exige atribución | Usarlo como dato de apoyo, citar la fuente en Acerca de |
| Derechos del audio | Los clips son de openings comerciales | Solo clips cortos, uso personal, sin redistribuir |
| Cambios de API | Sin garantías de estabilidad en las comunitarias | Aislar cada fuente tras una interfaz y tests con respuestas de ejemplo guardadas |

## Lo que no se pudo verificar

- Términos de uso de AnisongDB y del CDN de AnimeMusicQuiz (no encontré documentación pública).
- Límites y términos de ANN, Kitsu y Bangumi.
- TMDB (requiere clave), y si `streamingEpisodes` existe para la mayoría de animes de tu biblioteca (se probó con uno).
- Cuota real de trace.moe: la respuesta de `/me` (100) no coincide con lo que dicen algunas páginas (~1 000/mes).

## Siguiente paso propuesto

Prototipo de línea de comandos en `tools/python` (sin tocar la UI) que, dado un anime de tu biblioteca, junte: 1 escena
(local o `streamingEpisodes`), 1 opening (AnisongDB, clip de 10 s por `Range`) y 3 personajes (AniList), y mida
peticiones gastadas y tiempos. Con eso se valida el presupuesto de 30/min antes de diseñar el juego.
