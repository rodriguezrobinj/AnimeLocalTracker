# Investigación: minijuegos dentro de la app

> **Estado: investigación (no implementado).** Hecha el 2026-09-25 contra el código del repo y con pruebas reales
> de las APIs externas. Amplía la propuesta #118 ("Anime Music Quiz") de `PROPUESTAS_NUEVAS_FUNCIONES.md`.

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
- **Problema real medido hoy:** `api.animethemes.moe` respondió **HTTP 522 (servidor caído) en 3 de 3 intentos**, y tu
  `app.log` ya muestra timeouts frecuentes contra ese servicio. Por eso el diseño debe ser **offline-first**: descargar
  y cachear los clips (ya existe `AnimeThemesDownloadService` y la carpeta `Music`) y jugar solo con lo cacheado.

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
| AnimeThemes inestable (522/timeouts, medido hoy) | Caché local de clips, jugar solo con lo descargado, mensaje claro si no hay red |
| AniList: hoy limita a **30 peticiones/min** (medido en la cabecera `X-RateLimit-Limit`; el valor "normal" de 90 lleva tiempo degradado) | Presupuesto de peticiones, consultas por lotes y caché en SQLite; cargar personajes solo al abrir el juego |
| Spoilers en descripciones y personajes | Censura de nombres + modo "solo lo que he visto" |
| Biblioteca pequeña → preguntas repetitivas | Mezclar con pool global popular (AniList) cuando haya red |
| Audio de openings (derechos) | Clips cortos, uso personal, sin redistribuir ni empaquetar |

## Orden recomendado (de menor a mayor esfuerzo)

1. **Adivina el anime** por pistas + fotograma local (S): todo offline, sin dependencias nuevas; valida pestaña,
   puntuación y logros.
2. **Adivina el OP/ED** con caché (M): añade audio y descarga; requiere resolver la fiabilidad de AnimeThemes.
3. **Adivina el personaje** con revelado progresivo (M-L): tabla nueva, imágenes en caché, censura de spoilers.
4. Extras (reto diario, rachas) (S cada uno).

## Lo que se probó y lo que no

- **Probado:** consulta real a AniList (campos de personajes, imagen `230×345` RGB sin alfa); AnimeThemes (3 intentos,
  `522`); revisión del código de `AnimeThemesService`, modelos, daemon Python y logros.
- **Sin probar todavía:** reproducción de audio `.ogg` recortado (Flyleaf en modo solo audio vs. otro reproductor),
  rendimiento de extraer fotogramas en lote, y persistencia de los rangos de OP detectados.

## Fuentes de datos

Comparativa probada en vivo de APIs y sitios candidatos: [investigacion-fuentes-datos-minijuegos.md](investigacion-fuentes-datos-minijuegos.md).

## Decisiones abiertas

- ¿Solo biblioteca propia, o también un pool global de animes populares (necesita red)?
- Escritura libre vs. opción múltiple (o ambas por dificultad).
- ¿Puntuación global guardada en local o se conecta a algo de AniList? (recomendado: solo local al principio).
