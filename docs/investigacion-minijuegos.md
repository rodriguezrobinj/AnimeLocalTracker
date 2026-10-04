# Investigación: minijuegos dentro de la app

> **Estado:** investigación hecha el 2026-09-25 contra el código del repo y con pruebas reales de las APIs externas.
> Amplía la propuesta #118 ("Anime Music Quiz") de `propuestas-nuevas-funciones.md`.
>
> **Implementado el 2026-09-26 (sin commit todavía): "Adivina el anime" por pistas** — pestaña *Minijuegos*
> (`MinijuegosViewModel`/`MinijuegosView`, lógica pura en `Services/Minijuegos/AdivinaAnimeJuego.cs`): 10 rondas de 4
> opciones con animes de tu biblioteca, hasta 5 pistas progresivas (géneros → estreno → episodios → estudio/formato/fuente
> → sinopsis con el título tapado), portada difuminada que se aclara, 100 pts menos 20 por pista extra (mínimo 20), atajos
> 1-4 / P / Enter, ES/EN. Funciona sin red.
>
> **Implementado el 2026-09-26 (sin commit todavía): "Adivina el OP/ED"** — mismo esquema de 10 rondas y 4 opciones, pero
> suena un clip de un opening/ending (6 s; "más audio" lo alarga a 12 y 20 s) y hay que decir de qué anime de tu biblioteca
> es. Ayudas que restan 20 pts cada una: más audio, artista, estreno, géneros, título de la canción (artista y canción solo
> se conocen con conexión). Al responder suena un trozo de 30 s y se ve la portada. Los clips salen de AnimeThemes (audio
> `.ogg` → mp3 con el ffmpeg de la app) y se guardan en `Music/{id}` (la misma caché que las descargas de la ficha, ~2 MB
> cada uno): una vez guardados se juega sin conexión, y si AnimeThemes no responde (12 s) se sigue solo con lo ya descargado.
> La ronda siguiente se prepara en segundo plano mientras se juega la actual. Medido en la app real: primera ronda en ~8 s,
> siguientes casi instantáneas. La pestaña *Minijuegos* pasó a ser un menú (`MinijuegosViewModel`) con un ViewModel por juego
> (`AdivinaAnimeViewModel`, `AdivinaOpEdViewModel`, base común `MinijuegoViewModelBase`); lógica pura en
> `AdivinaOpEdJuego.cs`, audio en `ClipPlayer` (un `MediaPlayer` nuevo por reproducción).
>
> **Implementado el 2026-09-26 (sin commit todavía): récords y logros de minijuegos.** Cada partida que llega al resumen se
> guarda en la tabla `PartidaMinijuego` (migración v11: juego, fecha UTC, puntos, rondas, aciertos, mejor racha; las
> abandonadas no cuentan; "Borrar todos mis datos" también las borra). De ahí salen los **récords** por juego (mejor
> puntuación, partidas jugadas, % de aciertos; se ven en las tarjetas del menú, en la presentación de cada juego y en el
> resumen con "¡Nuevo récord!" y el récord anterior; la primera partida no cuenta como récord) y una categoría nueva de
> **logros "Minijuegos"** con 6 familias de 5 niveles (`mini_partidas`, `mini_aciertos`, `mini_puntuacion`,
> `mini_perfectas`, `mini_racha`, `mini_oped`), que se evalúan al terminar cada partida y avisan con el toast habitual.
> Lógica en `RecordsMinijuego`/`MinijuegosRecordsService`; el catálogo pasa de 79 a 109 niveles.
>
> **Implementado el 2026-09-26 (sin commit todavía): "Adivina el personaje".** Sale la imagen de un personaje de un anime
> de tu biblioteca, muy **pixelada** (10 px de ancho, sube hasta 80 con cada pista; al responder sale entera), y hay que
> elegir su nombre entre 4 (dos del mismo género que la respuesta). Pistas de menos a más reveladoras: papel en la
> historia, género, edad, "también le llaman" (primer apodo que no delate el nombre), anime y inicial; cada una resta 20
> puntos como en los otros juegos. Los personajes se piden a AniList **por lotes de 5 animes** (12 personajes por anime,
> principales primero) y se guardan en las tablas `PersonajeAnime`/`PersonajesAnimeSync` (migración v12, vigencia de
> 30 días, "Borrar todos mis datos" también las limpia); las imágenes, en `AppDataPaths.CharactersDir` con tope de 200
> archivos (se borran las más antiguas). Toda la partida —personajes e imágenes— se prepara al pulsar Jugar (8 s la
> primera vez con 10 animes nuevos, ~2 s con la caché) y después se juega sin conexión. No se usa la descripción del
> personaje (spoilers). Récords y un logro nuevo (`mini_personajes`, "Ojo de fan"; catálogo de 109 a 114 niveles).
> Lógica pura en `AdivinaPersonajeJuego`, datos en `PersonajesService`, imagen pixelada con `PersonajePixeladoConverter`.
>
> **Ubicación (2026-09-26):** los minijuegos ya no tienen pestaña propia en la barra lateral: son una **sección de la Galería**,
> con un selector *Biblioteca | Minijuegos* junto al título (`GaleriaViewModel.Minijuegos.cs`, `SelectorSeccionGaleria`). Al cambiar
> de sección o de pestaña una partida en curso se conserva; el audio de "Adivina el OP/ED" se corta al volver a la biblioteca.
> El menú muestra las tres tarjetas en una fila.
>
> **Aún no hecho:** fotograma de un episodio local, modo escritura libre, "solo animes que he visto", y récords en la
> pestaña Estadísticas.

## Pregunta

¿Es posible integrar minijuegos como *adivina el OP/ED*, *adivina el personaje* (por silueta o pistas) y *adivina el
anime*?

## Respuesta corta

**Sí, los tres son viables**, con una excepción importante: la **silueta real de personajes no es viable** (ver más
abajo); se sustituye por revelado progresivo, que funciona bien. Además, gran parte del material ya está en la app,
así que el primer minijuego puede funcionar **sin internet**.

## Qué tiene ya el proyecto (verificado en el código)

| Pieza existente | Sirve para |
|---|---|
| `AnimeItem` en SQLite: título, títulos alternativos, sinopsis, géneros, año, temporada, nº de episodios, portada | Pistas y preguntas de "adivina el anime" sin red |
| `DatosExtraAnime`: estudio, formato, fuente (manga/novela…), nota media, tráiler | Más pistas (estudio, "basado en…") |
| `AnimeThemesService`: AniList ID → OP/ED con título de canción, artistas y enlace de audio `.ogg`, con caché | "Adivina el OP/ED" |
| Demonio Python: `generate_thumbnail(video, salida, timestamp)` y extractor de miniaturas | Sacar un fotograma aleatorio de **tus** episodios |
| Detección de OP en episodios (log: `Opening detectado [39 - 124]`) y AniSkip | Localizar el opening dentro de un episodio local |
| Sistema de logros por niveles (`Services/Logros`) y pestaña de Estadísticas | Logros y récords del minijuego |
| Patrón de pestaña (skill `wpf-add-view`) | Añadir la pestaña "Minijuegos" |

## Modos de juego evaluados

### 1. Adivina el anime — viabilidad: **alta, funciona offline**

- **Pistas progresivas** (cada pista extra resta puntos): género → año/temporada → nº de episodios → estudio/fuente →
  sinopsis con el título censurado → portada difuminada que se aclara.
- **Fotograma de un episodio tuyo:** se extrae un frame aleatorio con el daemon (ya existe la función) y se muestra
  con desenfoque/zoom que va cediendo. Es 100 % local.
- Opciones múltiples o escritura libre (con comparación por títulos alternativos ya guardados en `NombresAlternativos`).

### 2. Adivina el OP/ED — viabilidad: **alta, con cautelas**

- **Fuente A (AnimeThemes):** audio `.ogg` + título de la canción + artistas. Permite preguntas extra
  ("¿quién lo canta?", "¿qué anime?"). Duración del clip configurable (3/5/10/20 s) como dificultad.
- **Fuente B (tu propio disco):** recortar el opening de un episodio local usando el rango que ya detecta la app.
  Funciona offline, pero **hay que comprobar** si esos rangos se guardan de forma persistente (no aparecen en la BD;
  hoy parecen calcularse al reproducir).
- **Estado de AnimeThemes:** el 25-sep `api.animethemes.moe` respondió **HTTP 522 en 3 de 3 intentos** y tu `app.log`
  ya muestra timeouts frecuentes. **El 26-sep volvió** (200 en <1 s, audio descargable), pero `song.artists` da 500
  (los artistas se piden ahora por `song.performances.artist`). Por la inestabilidad, el diseño sigue siendo
  **offline-first**: descargar y cachear los clips (ya existe `AnimeThemesDownloadService` y la carpeta `Music`) y
  jugar solo con lo cacheado.
- **Precisión del emparejamiento:** se hace por ID de AniList, no por título. Probado con los 207 animes de la
  biblioteca: 153 emparejados sin ningún error de anime, 49 sin mapeo (películas/OVAs/sin estrenar, correcto que
  salgan vacíos). Solo había omisiones en casos con varios *resources* (Grand Blue S3, Re:Zero OVAs), ya corregidas (26-sep). Para el juego:
  deduplicar por `Slug` (73 temas tienen varias versiones), omitir la pregunta del artista cuando falte (26 %) y
  respetar `EsSpoiler`. Detalle en `investigacion-fuentes-datos-minijuegos.md` (hallazgo 7).

### 3. Adivina el personaje — viabilidad: **media-alta, la silueta NO es viable**

Prueba real contra AniList (`Media.characters`): devuelve nombre (completo y nativo), imagen, género, edad,
favoritos y descripción. Con *Tensei shitara Slime Datta Ken* salieron Rimuru, Milim, Shion y Diablo.

- **Silueta real: descartada.** Las imágenes son `230×345 px` **RGB sin canal alfa** (comprobado en el PNG
  descargado) y suelen ser **recortes de cara/busto con fondo**. Sacar una silueta limpia exigiría segmentación por IA
  (modelo de cientos de MB en el daemon) y aun así fallaría en recortes de cara.
- **Sustitutos que sí funcionan:** pixelado o desenfoque que se reduce con cada pista, recorte que se amplía, escala
  de grises y luego color.
- **Pistas de texto:** género, edad, especie (sale en la descripción), de qué anime es, ranking de favoritos.
  Ojo: **el cumpleaños viene vacío casi siempre**, no contar con él.
- **Spoilers:** las descripciones traen spoilers y el nombre del propio personaje. Hay que censurar nombre, apodos y
  nombre nativo, y ofrecer el modo **"solo animes que ya he visto"** (la app conoce tu progreso).

### 4. Extras baratos (una vez hecha la base)

*Reto diario* con semilla por fecha (misma partida para todos, sin servidor), *¿mayor o menor?* (notas/popularidad),
*ordena por año de estreno*, racha de días, récords en Estadísticas y una familia de logros ("Otaku Quiz", niveles).

## Propuesta de arquitectura

- Nueva pestaña **Minijuegos** siguiendo la cadena completa del skill `wpf-add-view` (mensaje → `NavigationService` → DI →
  `DataTemplate` → botón con `ToolTip` localizado).
- Un generador de preguntas por juego detrás de una interfaz común (`IGeneradorPreguntas`): lógica **pura y
  testeable** (selección de distractores, censura de spoilers, puntuación) separada de la UI.
- Audio: un reproductor ligero e independiente del `Player` de Flyleaf, para no interferir con el reproductor
  principal (cuidado con `EsEntornoPruebas()`; ver memoria del proyecto).
- Datos externos nuevos (personajes) en una **tabla nueva con migración** (`Migraciones` de `DatabaseService`) y
  imágenes en la carpeta de caché de `AppDataPaths`. Nada de escribir en el directorio de instalación.
- Localización ES/EN con `LocalizationService.T()` (incluidos textos generados en código; ver LOC-08).
- Accesibilidad: responder con teclado (1-4), `AutomationProperties.Name` en botones de opción, colores solo de la
  paleta de `App.xaml`.

## Riesgos y cautelas

| Riesgo | Mitigación |
|---|---|
| AnimeThemes inestable (522 el 25-sep, include de artistas roto el 26-sep) | Caché local de clips, jugar solo con lo descargado, mensaje claro si no hay red |
| AniList: hoy limita a **30 peticiones/min** (medido en la cabecera `X-RateLimit-Limit`; el valor "normal" de 90 lleva tiempo degradado) | Presupuesto de peticiones, consultas por lotes y caché en SQLite; cargar personajes solo al abrir el juego |
| Spoilers en descripciones y personajes | Censura de nombres + modo "solo lo que he visto" |
| Biblioteca pequeña → preguntas repetitivas | Mezclar con pool global popular (AniList) cuando haya red |
| Audio de openings (derechos) | Clips cortos, uso personal, sin redistribuir ni empaquetar |

## Orden recomendado (de menor a mayor esfuerzo)

1. **Adivina el anime** por pistas + fotograma local (S): todo offline, sin dependencias nuevas; valida pestaña,
   puntuación y logros.
2. **Adivina el OP/ED** con caché (M): añade audio y descarga; AnimeThemes ya responde (26-sep) y el emparejamiento por ID es fiable, y las omisiones con varios *resources* ya están corregidas; falta deduplicar versiones (por `Slug`) en el juego y cachear los clips por el historial de caídas.
3. ~~**Adivina el personaje** con revelado progresivo (M-L): tabla nueva, imágenes en caché, censura de spoilers.~~ Hecho (26-sep): pixelado progresivo en vez de censura, porque no se usa la descripción.
4. Extras (reto diario, rachas) (S cada uno).

## Lo que se probó y lo que no

- **Probado:** consulta real a AniList (campos de personajes, imagen `230×345` RGB sin alfa); AnimeThemes (25-sep: 3 intentos,
  `522`; 26-sep: operativa, emparejamiento probado con los 207 animes de la biblioteca); revisión del código de `AnimeThemesService`, modelos, daemon Python y logros.
- **Sin probar todavía:** reproducción de audio `.ogg` recortado (Flyleaf en modo solo audio vs. otro reproductor),
  rendimiento de extraer fotogramas en lote, y persistencia de los rangos de OP detectados.

## Fuentes de datos

Comparativa probada en vivo de APIs y sitios candidatos: [investigacion-fuentes-datos-minijuegos.md](investigacion-fuentes-datos-minijuegos.md).

## Decisiones abiertas

- ¿Solo biblioteca propia, o también un pool global de animes populares (necesita red)?
- Escritura libre vs. opción múltiple (o ambas por dificultad).
- ¿Puntuación global guardada en local o se conecta a algo de AniList? (recomendado: solo local al principio).
