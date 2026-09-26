# Catálogo Maestro de Propuestas y Nuevas Funciones — AnimeLocalTracker

Este documento recopila de forma estructurada, exhaustiva y priorizada **todas las propuestas de innovación, mejoras de calidad de vida y herramientas de alta utilidad práctica** diseñadas para el ecosistema de **AnimeLocalTracker** (.NET 8 WPF, Flyleaf, SQLite, Rust FFI, Python Daemon, AniList).

---

## Índice General

1. [⭐ Funciones de Máxima Utilidad Práctica (Prioridad Alta)](#1--funciones-de-máxima-utilidad-práctica-prioridad-alta)
2. [🎬 Reproductor Multimedia y Experiencia de Visionado](#2--reproductor-multimedia-y-experiencia-de-visionado)
3. [🎨 Calidad de Imagen, Shaders y Pantallas](#3--calidad-de-imagen-shaders-y-pantallas)
4. [📥 Descargas, Red y Gestión de Almacenamiento](#4--descargas-red-y-gestión-de-almacenamiento)
5. [🗂️ Organización de Biblioteca y Automatización de Archivos](#5-️-organización-de-biblioteca-y-automatización-de-archivos)
6. [🌐 Ecosistema Social, AniList y Multi-Plataforma](#6--ecosistema-social-anilist-y-multi-plataforma)
7. [🔊 Audio, Música y Creación de Contenido](#7--audio-música-y-creación-de-contenido)
8. [💻 Integración con Windows, Dispositivos y Productividad](#8--integración-con-windows-dispositivos-y-productividad)
9. [🎌 Aprendizaje de Japonés, Lectura y Comunidad Otaku](#9--aprendizaje-de-japonés-lectura-y-comunidad-otaku)
10. [🤖 Inteligencia Artificial, Diagnóstico y Plugins](#10--inteligencia-artificial-diagnóstico-y-plugins)

---

## 1. ⭐ Funciones de Máxima Utilidad Práctica (Prioridad Alta)

Estas funcionalidades resuelven dolores de cabeza reales, fricción diaria y pérdidas de tiempo comunes al gestionar y ver anime localmente:

1. **Descarga Masiva con 1 Clic ("Descargar Temporada Completa")**:
   * *Problema*: Descargar una serie de 24 episodios requiere entrar y pulsar el botón 24 veces seguidas.
   * *Solución*: Un botón en la cabecera: *"Descargar temporada completa"* o *"Descargar solo los que faltan"*. Calcula el espacio total requerido en disco y encola todos los episodios en orden.
2. **Detector de Episodios Faltantes en la Carpeta (Missing Episodes Alert)**:
   * *Problema*: Falta el episodio 7 de 12 sin darte cuenta y saltas del 6 al 8 comiéndote spoilers cruciales.
   * *Solución*: Banner de alerta en la ficha: ⚠️ *"Faltan 2 episodios en tu carpeta local (Ep. 7 y Ep. 11)"*, con un botón directo *"Descargar faltantes"*.
3. **Limpiador Inteligente de Espacio ("Eliminar tras ver")**:
   * *Problema*: Temporadas de 24 episodios en 1080p pesan ~20 GB y saturan los discos duros con rapidez.
   * *Solución*: Al marcar como completada una serie, pregunta: *“¿Deseas liberar 18 GB eliminando los archivos de video, conservando la ficha y tus puntuaciones?”*. Incluye un modo de "Consumo ligero" que conserva solo los últimos 3 episodios descargados.
4. **Mudar Biblioteca de Disco sin Perder Nada (Path Relocator / Migrador)**:
   * *Problema*: Al cambiar de disco (ej. de `C:\Anime` a `D:\Anime`), todas las series quedan con *"Carpeta no encontrada"*.
   * *Solución*: Herramienta de migración con 1 clic: indicas la ruta vieja y la nueva, actualizando cientos de registros en SQLite en un segundo sin tocar el historial.
5. **Creación Automática de Carpetas al Añadir Anime (`+` Tab Auto-Folder)**:
   * *Problema*: Añadir una serie obliga a abrir el Explorador de Windows, crear la carpeta a mano y buscarla en la app.
   * *Solución*: Al pulsar *"Añadir a mi biblioteca"*, la app crea automáticamente `D:\Anime\NombreDelAnime` basándose en la ruta base configurada.
6. **Cola de Sincronización AniList Offline (Sync Queue Persistente)**:
   * *Problema*: Ver anime en viajes sin internet o ante caídas de red provoca pérdida de sincronización con AniList.
   * *Solución*: Cola en SQLite de episodios pendientes de sincronizar que se despacha silenciosamente en cuanto Windows recupera conexión.
7. **Banner "Continuar Viendo" en Inicio de Biblioteca**:
   * *Problema*: Tener que ir a Galería o Historial para buscar qué estabas viendo ayer.
   * *Solución*: Tarjeta destacada en la parte superior con el último anime, miniatura del episodio, minutos restantes y botón de ▶ Reproducir inmediato.
8. **Guardia de Espacio en Disco previo a Descargas (Pre-Allocation Check)**:
   * *Problema*: Quedarse sin espacio a mitad de descarga corrompe archivos y congela el sistema operativo.
   * *Solución*: Comprueba el espacio libre real antes de iniciar la descarga y avisa si quedan menos de 5 GB libres.
9. **Actualización Automática al Copiar Archivos en Disco (`FileSystemWatcher`)**:
   * *Problema*: Pegar un archivo en la carpeta requiere abrir la ficha y pulsar "Actualizar" manualmente.
   * *Solución*: Detección reactiva en segundo plano que incorpora el capítulo y genera su miniatura al instante.
10. **Reordenación y Priorización de Descargas (Drag & Drop / "Descargar Primero")**:
    * *Problema*: Encolar 10 capítulos pero querer ver el capítulo 4 de inmediato sin esperar a los anteriores.
    * *Solución*: Arrastrar y soltar en la lista de descargas o botón para mover un episodio al primer lugar de la cola.

---

## 2. 🎬 Reproductor Multimedia y Experiencia de Visionado

11. **Selector de Pistas de Audio (Dual Audio / Multi-idioma)**:
    * Botón en controles inferiores con popup para listar las pistas de audio disponibles (`Japonés [FLAC 2.0]`, `Español Latino [AAC 5.1]`) y conmutar el stream activo en Flyleaf.
12. **Gestos Multimedia de Ratón (VLC/mpv Standard)**:
    * `MouseWheel` sobre el video: subir/bajar volumen en pasos de 5% con OSD visual.
    * `Doble Clic`: alternar pantalla completa.
    * Tecla `M`: silenciar/restaurar volumen (*Mute*).
13. **Detección y Carga Automática de Subtítulos Externos (`.ass` / `.srt`)**:
    * Si existen archivos de subtítulos con el mismo nombre base en la carpeta del video, incorporarlos como pistas seleccionables en Flyleaf.
14. **Cajón Lateral de Episodios en Reproductor (Playlist Drawer)**:
    * Panel semitransparente que se desliza (tecla `L`) mostrando toda la temporada con miniaturas para saltar de capítulo sin salir a la ficha.
15. **Corrector de Desfase de Audio y Subtítulos en Vivo (Delay Offset)**:
    * Teclas `Z`/`X` para retrasar o adelantar subtítulos (±100ms) y `J`/`K` para audio, con indicador OSD en pantalla.
16. **Conmutador A/B Instantáneo de Doblaje vs Japonés (Tecla `T`)**:
    * Alterna la pista de audio en el mismo milisegundo exacto sin pausar ni reiniciar el buffer para comparar actuaciones.
17. **Modo Sakuga / Cámara Lenta Frame-a-Frame**:
    * Teclas `.` y `,` para avanzar o retroceder exactamente 1 fotograma individual a 24 fps, y modos 0.25x / 0.5x con audio compensado.
18. **Temporizador de Dormir / Sleep Timer**:
    * Configurar apagado automático o suspensión del PC al terminar el episodio o tras 30/60 minutos, con atenuación de volumen gradual.
19. **Marcadores con Notas y Timestamps**:
    * Tecla `B` para guardar momentos favoritos con minuto exacto y nota personalizada, consultables desde la ficha del anime.
20. **Aviso de "Opening Especial / Versión Clímax"**:
    * Detecta si la pista de audio del opening difiere de episodios anteriores (efectos de sonido o giros en el clímax) y avisa en lugar de saltarlo.
21. **Detector de Escenas Post-Créditos**:
    * Si AniSkip indica metraje tras el ending, el salto de ending transporta al inicio de la escena post-créditos en vez de cerrar el video.
22. **Atenuación Suave de Openings (Mute al 15% en vez de saltar)**:
    * Para disfrutar de la animación del opening sin escuchar la misma canción repetida decenas de veces.
23. **Modo "Flashback / Recap Skipper"**:
    * Salta resúmenes del episodio anterior en animes clásicos largos que dedican 3 a 5 minutos iniciales a recaps.
24. **Pausa Inteligente en Cortes de Escena (Smart Pause)**:
    * Al pausar, avanza suavemente hasta el final de la frase o el corte de plano más cercano para no dejar la escena rota a medias.
25. **Modo "Ghost Player" (Mini-reproductor Semitransparente con Click-Through)**:
    * Ventana flotante siempre visible con opacidad regulable que permite hacer clics a través de ella para trabajar mientras ves anime.
26. **Control por Gestos con la Cámara Web (Modo Manos Sucias)**:
    * Palma abierta para Pausa/Play y barrido para saltar 10s cuando estás comiendo y tienes las manos ocupadas.
27. **Soporte para Microsoft Surface Dial y Perillas de Teclados**:
    * Scrubbing analógico suave fotograma a fotograma con respuesta háptica.
28. **Guardado Anti-Cortes de Luz y Apagados Bruscos (Crash-Safe Resume)**:
    * Registro de posición volátil cada 3 segundos para reanudar con exactitud ante cortes de luz o reinicios de Windows.
29. **Ocultar Vistas Previas en la Barra de Tiempo (Anti-Spoilers de Barra)**:
    * Difumina o bloquea miniaturas de la barra de tiempo en episodios no vistos para no spoilear escenas clave al buscar un punto.
30. **Extracción Rápida de Subtítulos Incrustados a `.srt`/`.ass`**:
    * Clic derecho en el episodio para extraer la pista de subtítulos a un archivo suelto con ffmpeg en 1 segundo.

---

## 3. 🎨 Calidad de Imagen, Shaders y Pantallas

31. **Motor de Estilizado de Subtítulos**:
    * Configurar tamaño, tipografía, color del texto y grosor del borde (*outline*) negro para legibilidad absoluta en cualquier fondo.
32. **Subtítulos Dobles Simultáneos (Oficial abajo + Fansub con notas arriba)**:
    * Proyecta dos pistas al mismo tiempo para disfrutar de las notas culturales del fansub sin perder la traducción principal.
33. **Shaders Anime4K en Tiempo Real**:
    * Algoritmo de reescalado y reconstrucción de bordes HLSL/Direct3D para ver ripeos 720p/1080p con nitidez cercana a 4K en pantallas grandes.
34. **Filtros de Color en Tiempo Real ("Anime Color Boost")**:
    * Ajustes de Brillo, Contraste, Saturación y Gamma por hardware para dar viveza cinematográfica a la animación.
35. **Shader Retro CRT / Pantalla de Tubo**:
    * Recreación nostálgica de scanlines y brillo de fósforo para animes de los 80s y 90s (*Evangelion*, *Cowboy Bebop*).
36. **Filtro de Luz Azul y Temperatura Cálida en Video**:
    * Shader ámbar para visionado nocturno que reduce fatiga visual y previene insomnio sin tocar la configuración global de Windows.
37. **Selector de Relación de Aspecto (4:3 vs 16:9 / Crop / Zoom)**:
    * Alterna relaciones de aspecto con la tecla `A` para adaptar animes antiguos 4:3 o películas 21:9 a tu pantalla.
38. **Efecto "Pillarbox Blur" para Monitores Ultra-Wide (21:9 y 32:9)**:
    * Rellena las barras negras laterales con una versión desenfocada y ambiental del propio video.
39. **Soporte Nativo de HDR10 para Pantallas OLED**:
    * Mapeo de tonos Direct3D de 10 bits en espacio BT.2020 para películas y animes masterizados en HDR.
40. **Modo "Negro Puro" para Pantallas OLED**:
    * Tema con fondos `#000000` absoluto que apaga píxeles OLED y ahorra batería en laptops.
41. **Protector Fotosensible (Filtro Anti-Epilepsia)**:
    * Atenúa ráfagas de luz estroboscópica intensas a alta frecuencia para visionado seguro y descansado.
42. **Atenuación Automática de Monitores Secundarios**:
    * Apaga a negro absoluto las pantallas secundarias al poner el video en pantalla completa para eliminar distracciones.
43. **Modo "Segundo Monitor" (Pantalla Completa Dedicada)**:
    * Proyecta el video directamente en la TV o monitor secundario mientras mantienes la biblioteca abierta en el monitor principal.

---

## 4. 📥 Descargas, Red y Gestión de Almacenamiento

44. **Espejos Alternativos y Selector de Calidad (1080p / 720p)**:
    * Cambiar de servidor o resolución si el host principal está saturado o caído.
45. **Streaming durante la Descarga (Ver mientras descarga)**:
    * Reproducción en buffer desde el primer minuto sin esperar a que el archivo baje al 100%.
46. **Scraper Multifuente (Integración con Nyaa / AnimeTosho)**:
    * Búsqueda e indexación de fuentes alternativas cuando la fuente principal no dispone de la obra.
47. **Limitador de Ancho de Banda y Pausa Masiva**:
    * Fijar velocidad tope (ej. 3 MB/s) para no saturar el Wi-Fi de casa y pausar/reanudar todas las descargas con 1 clic.
48. **Optimizador de Espacio en Disco (Recompresor AV1 / HEVC en Reposo)**:
    * Convierte videos H.264 antiguos a AV1 moderno cuando la PC está inactiva, ahorrando hasta un 60% de espacio sin pérdida visual.
49. **Almacenamiento Multi-Disco con Desvío Inteligente**:
    * Define discos secundarios (`D:`, `E:`); si el disco principal baja del 10%, desvía las descargas automáticamente.
50. **Limpiador de Carpetas Vacías y Basura Huérfana**:
    * Escanea y elimina carpetas vacías, archivos temporales `.part` y restos de series ya borradas.
51. **Limpiador de Versiones Duplicadas del Mismo Episodio**:
    * Detecta si tienes el mismo capítulo en 720p y 1080p en carpetas distintas y ofrece borrar la versión inferior.
52. **Doctor de Integridad de Video (Escáner de Archivos Corruptos)**:
    * Verifica los contenedores de los episodios descargados para avisar de archivos rotos antes de que te sientes a verlos.
53. **Copia de Seguridad Automática en la Nube (Google Drive / OneDrive / WebDAV)**:
    * Automatiza snapshots `VACUUM INTO` periódicos hacia carpetas sincronizadas en la nube.
54. **Modo Suite Portátil 100% (Zero-Install en USB)**:
    * Mantiene la base de datos, portadas y configuración dentro de la carpeta del ejecutable para llevar en un pendrive a cualquier PC.

---

## 5. 🗂️ Organización de Biblioteca y Automatización de Archivos

55. **Paleta de Comandos / Búsqueda Global Rápida (`Ctrl + K` / Spotlight)**:
    * Ventana modal flotante accesible desde cualquier pantalla para buscar animes y reproducir episodios en 1 segundo.
56. **Corrector Manual de Número de Episodio (Smart Renumber)**:
    * Clic derecho para reasignar el número a archivos con nombres confusos o mal etiquetados por fansubs.
57. **Renombrador Masivo de Archivos con Motor Rust (Batch Anime Renamer)**:
    * Previsualiza y renombra carpetas enteras a patrones estándar limpios (`Anime - 01 - Título.mkv`).
58. **Clasificación Automática de OVAs, Películas y Especiales Cortos**:
    * Separa automáticamente la ficha en pestañas (*Serie regular*, *Película*, *Especiales*) analizando la duración de los videos.
59. **Avisador de Nuevas Temporadas para Animes Completados (Sequel Announcer)**:
    * Notifica en el inicio cuando se estrena o anuncia una secuela de una serie que tienes marcada como terminada.
60. **Avisador Canónico de OVAs y Películas Intermedias**:
    * Muestra notas en el reproductor recomendando ver OVAs canónicas que van entre dos episodios antes de avanzar.
61. **Modo Selección Múltiple en Galería (Acciones en Lote con `Ctrl`)**:
    * Borrar, mover o marcar favoritos en decenas de animes seleccionados simultáneamente.
62. **Filtro Rápido "Listos para Ver"**:
    * Toggle en biblioteca para ver únicamente animes que tienen capítulos locales descargados y sin ver.
63. **Filtro por Duración de Episodio**:
    * Filtrar por series cortas (< 15 min), estándar (20-25 min) o especiales largos (> 40 min).
64. **Búsqueda Instantánea por Texto Completo (SQLite FTS5)**:
    * Búsqueda ultrarrápida que indexa títulos en Romaji, Kanji, inglés, sinopsis y nombres de personajes.
65. **Buscador de Episodios por Título de Capítulo**:
    * Buscar episodios escribiendo palabras de su título oficial en la lista de la ficha.
66. **Soporte para Discos USB Portátiles (Rutas Relativas por GUID de Volumen)**:
    * No pierde enlaces de archivos aunque Windows cambie la letra de unidad (`E:`, `F:`) al reconectar el disco.
67. **Soporte Robusto para Rutas de Red (NAS / Samba UNC `\\Servidor\Anime`)**:
    * Compatibilidad nativa para reproducir y escanear colecciones almacenadas en servidores de red local.
68. **Detector de Episodios de Relleno (Anime Filler Detector)**:
    * Consulta bases de datos comunitarias y añade insignias de *Canon del Manga*, *Mixto* o *Relleno (Filler)*, con opción de auto-skip.
69. **Modo SFW / Ocultar Portadas Ecchi o Explícitas**:
    * Difumina portadas sugerentes automáticamente para usar la app con tranquilidad en oficinas o lugares públicos.
70. **Protección Anti-Spoilers en Sinopsis de Episodios**:
    * Difumina resúmenes y títulos de capítulos no vistos hasta que pasas el ratón o los reproduces.
71. **Selector de Pósters Alternativos y Arte Oficial de Blu-Ray**:
    * Cambia portadas promocionales básicas por artes de Blu-Ray arrastrando una imagen o eligiendo en AniList.

---

## 6. 🌐 Ecosistema Social, AniList y Multi-Plataforma

72. **Discord Rich Presence (RPC) Nativo**:
    * Muestra en tu perfil de Discord qué estás viendo con carátula oficial, episodio y tiempo transcurrido.
73. **Sincronización Cruzada con MyAnimeList (MAL) y Kitsu**:
    * Actualiza tu progreso en AniList, MyAnimeList y Kitsu a la vez con un solo clic.
74. **Sincronización de Listas Personalizadas de AniList (Custom Lists)**:
    * Integra y filtra por tus listas propias creadas en la web de AniList.
75. **Árbol de Franquicia / Relacionados en Detalle**:
    * Carrusel visual de precuelas, secuelas, películas y spin-offs con badge de *"En biblioteca"* y adición en 1 clic.
76. **Personajes y Actores de Voz (Seiyuus) con Cruce en tu Biblioteca**:
    * Al consultar un Seiyuu, la app te muestra qué otros personajes de tu biblioteca interpreta.
77. **Guía de Orden Cronológico vs Emisión para Franquicias Complejas**:
    * Mapa interactivo para sagas como *Fate*, *Monogatari* o *Steins;Gate* con checks de completitud.
78. **Feed de Actividad de Amigos de AniList**:
    * Muestra en qué episodios van los amigos que sigues y qué puntuaciones han publicado.
79. **Modo "A Ciegas" (Blind Watch)**:
    * Oculta notas medias de la comunidad para ver series sin expectativas condicionadas.
80. **Modo "Watch Party" Local (Sincronización P2P)**:
    * Conexión por WebSockets entre dos computadoras para ver el mismo archivo sincronizando play, pausa y seek.

---

## 7. 🔊 Audio, Música y Creación de Contenido

81. **Jukebox de Openings y Endings (Anime OST Player)**:
    * Lista de canciones oficiales en la ficha con reproducción y enlaces directos a Spotify y YouTube Music.
82. **Reconocedor de Canciones en Vivo (Shazam Acústico de Anime)**:
    * Identifica temas de fondo e insert songs emocionantes con un clic en el reproductor.
83. **Modo Karaoke en Openings y Endings**:
    * Letras sincronizadas en Romaji y Kanji con efecto visual de cambio de color y traducción en español.
84. **Soundboard de Frases y Efectos de Sonido**:
    * Recorta clips de 2 segundos para crear una botonera rápida de frases míticas (*"Nani?!"*, *"Za Warudo"*).
85. **Extractor de Audio con un Clic (Clip a MP3 / FLAC)**:
    * Extrae fragmentos musicales de episodios en alta fidelidad con metadatos incrustados.
86. **Normalizador de Audio / "Modo Noche" (Dynamic Range Compression)**:
    * Nivelador DSP para escuchar diálogos nítidos sin sobresaltos por explosiones u openings estridentes.
87. **Sonido Envolvente Virtual 3D para Auriculares (Binaural 5.1 DSP)**:
    * Transforma pistas 5.1 en audio espacial tridimensional envolvente para auriculares comunes.
88. **Modo "Solo Audio / Podcast" (Pantalla Apagada)**:
    * Apaga el renderizador de video para escuchar capítulos en laptops ahorrando 90% de batería mientras caminas o descansas.
89. **Captura de Pantalla HD Limpia con un Clic (`Ctrl + S`)**:
    * Vuelca el buffer de video en Direct3D a PNG en resolución nativa, con o sin subtítulos.
90. **Instant Replay / Buffer de Clip de 30 Segundos (`Alt + F10`)**:
    * Guarda en MP4 los últimos 30 segundos de video y audio para compartir momentos épicos en Discord.
91. **Generador Rápido de GIFs / Clips Cortos (`GIF Maker`)**:
    * Selecciona inicio y fin para exportar un GIF animado o WebM ligero en pocos segundos.
92. **Modo "Estudio de Fandub" (Voz Silenciada + Teleprompter)**:
    * Atenúa la voz original conservando música y efectos para practicar doblaje de escenas.
93. **Analizador de Espectro de Audio en la Barra de Progreso**:
    * Visualizador de ondas sonoras dinámico integrado con elegancia en la barra de tiempo.

---

## 8. 💻 Integración con Windows, Dispositivos y Productividad

94. **Minimizar a la Bandeja del Sistema (System Tray)**:
    * Menú rápido junto al reloj de Windows para reanudar el último capítulo y mantener notificaciones activas en segundo plano.
95. **Notificaciones Nativas en Windows (Día de Emisión)**:
    * Avisos del Centro de Notificaciones el día y hora exactos en que se emite un nuevo episodio.
96. **Barra de Progreso en el Icono de la Barra de Tareas (`TaskbarItemInfo`)**:
    * Progreso verde de descargas y reproducción reflejado directamente en el icono de Windows.
97. **Integración con el Explorador ("Abrir con AnimeLocalTracker")**:
    * Asociación de archivos `.mkv`/`.mp4` para abrir en la app con tracking automático de AniList.
98. **Modo No Molestar Automático en Pantalla Completa**:
    * Activa *Focus Assist* de Windows para silenciar popups y sonidos externos mientras ves una serie.
99. **Control Remoto Web desde el Móvil (Wi-Fi Local con QR)**:
    * Controla play, pausa, volumen y capítulos desde el navegador de tu smartphone en la misma red.
100. **Transmitir a Smart TV / Chromecast (DLNA & Google Cast)**:
     * Envía el video y subtítulos a tu televisor sin cables HDMI.
101. **Transferencia Directa al Móvil por Código QR**:
     * Escaneas un código QR en pantalla y el archivo de video se transfiere directo a VLC en tu teléfono.
102. **Soporte Nativo para Mando de Consola (Xbox / PlayStation XInput)**:
     * Control total de la interfaz y reproducción con el mando desde el sofá.
103. **Iluminación Ambiental RGB Reactiva (Razer Chroma / Corsair iCUE)**:
     * Sincroniza luces LED de periféricos con los colores dominantes del anime en tiempo real.
104. **Bloqueo Biométrico con Windows Hello (Huella / Rostro / PIN)**:
     * Protege tu biblioteca, historial y notas si compartes la computadora con otras personas.
105. **Perfiles Multi-Usuario Locales**:
     * Múltiples cuentas locales en la misma PC, cada una con su biblioteca y AniList independiente.
106. **Modo "Family / Kids" con PIN de Seguridad**:
     * Oculta series con clasificación adulta o gore detrás de un código PIN.
107. **Modo "Pomodoro Otaku"**:
     * Temporizador de estudio de 50 minutos que desbloquea 1 capítulo de anime como recompensa.
108. **Alarma Amigable "Mañana Madrugas"**:
     * Mensaje simpático a partir de medianoche que te recuerda descansar antes de arrancar otro capítulo.
109. **Exportador de Metadatos NFO para Plex / Jellyfin / Kodi**:
     * Genera metadatos `.nfo` e imágenes locales compatibles con servidores multimedia domésticos.
110. **Modo Batería Extremo para Laptops (Forzar 24 FPS)**:
     * Adapta la tasa de refresco a los 24 FPS del anime para exprimir hasta 2 horas extra de batería.
111. **Efecto "Pillarbox Blur" en Monitores 21:9 y 32:9**:
     * Elimina las barras negras verticales con desenfoque ambiental dinámico en monitores curvos.

---

## 9. 🎌 Aprendizaje de Japonés, Lectura y Comunidad Otaku

112. **Modo Aprendizaje de Japonés (Subtítulos Dobles + Salto por Frases)**:
     * Japonés arriba y español abajo, con navegación frase a frase y repetición en bucle (`R`).
113. **Buscador de Subtítulos y Transcripción Interactiva**:
     * Panel con todas las frases del capítulo para buscar palabras y saltar al segundo exacto.
114. **Detector "¿En qué capítulo del manga continúa?" + Lector CBZ**:
     * Información comunitaria de en qué tomo sigue la historia y lector integrado con lectura derecha-izquierda.
115. **Música Ambiental Dinámica para Lectura de Manga**:
     * Pistas de lluvia, piano o viento adaptadas al género del manga que estés leyendo.
116. **Asistente de Lectura para Diálogos Ultra-Rápidos**:
     * Retención dinámica en pantalla de subtítulos acelerados (*Tatami Galaxy*, *Monogatari*).
117. **Búsqueda Inversa de Escenas por Imagen (Trace.moe)**:
     * Arrastra un meme o captura y la app localiza el anime, episodio y segundo exacto en tu disco.
118. **Minijuego "Anime Music Quiz" Local**:
     * Trivia interactiva que reproduce 10s de openings al azar de tu biblioteca para adivinar el anime.
     * *Investigación de viabilidad (adivina OP/ED, personaje y anime): [docs/investigacion-minijuegos.md](docs/investigacion-minijuegos.md).*
119. **Creador de Tier Lists Interactivo (Drag & Drop a PNG)**:
     * Organiza tus series en niveles S, A, B, C, D y exporta la imagen en alta calidad.
120. **Generador de Cartas Coleccionables (Anime TCG Card)**:
     * Crea cartas holográficas digitales de tus personajes favoritos con sus actores de voz y estadísticas.
121. **Mapa de Peregrinación Anime (Turismo Otaku con Google Maps)**:
     * Fotos comparativas entre escenas del anime y localizaciones reales en Japón con coordenadas.
122. **Mapa de Calor de Visionado Anual (Estilo GitHub)**:
     * Mosaico anual de 365 días en tonos verdes según la cantidad de episodios vistos cada día.
123. **Calculadora de la "Regla de los 3 Episodios" y Drop Rate**:
     * Análisis de cuándo y por qué abandonas series y cuáles son tus géneros infalibles.
124. **Calculadora de Maratones (Binge Planner)**:
     * Estima los días y horas que te tomará terminar una serie fijando metas de capítulos diarios.
125. **Generador de Episode Recap Cards para Redes**:
     * Plantilla cuadrada/vertical con miniatura, nota y reacción lista para compartir en Twitter o Stories.
126. **Generador de Fichas de Presentación (BBCode / Reddit Markdown)**:
     * Plantillas estilizadas para publicar reseñas en foros y comunidades con 1 clic.
127. **Ruleta de Anime Interactiva ("¿Qué veo hoy?")**:
     * Ruleta animada con filtros de género y duración para elegir un anime al azar de tus pendientes.
128. **Modo "Canal de Anime" (Shuffle Marathon)**:
     * Reproduce capítulos aleatorios de series de comedia en bucle continuo como un canal de TV.
129. **Comparador Manga vs Anime en Pantalla Dividida**:
     * Muestra la viñeta original dibujada por el autor al lado del fotograma animado.
130. **Ficha de Merchandising y Figuras Oficiales (MyFigureCollection)**:
     * Muestra Nendoroids y figuras oficiales de los personajes de la serie que estás viendo.

---

## 10. 🤖 Inteligencia Artificial, Diagnóstico y Plugins

131. **Asistente de Biblioteca con IA Local ("Pregúntale a tu Colección")**:
     * Consulta tu base de datos en lenguaje natural (*“¿En qué anime salía un detective que comía dulces?”*).
132. **Alineador Automático de Subtítulos con IA (Whisper Auto-Sync)**:
     * Corrige desfases variables en subtítulos sincronizando el texto con la onda de voz real del video.
133. **Traductor Automático de Subtítulos Offline**:
     * Traduce subtítulos raros en inglés o japonés a español manteniendo los estilos intactos.
134. **Generador Automático de Mini-Tráilers de 15 Segundos**:
     * Recorta 4 fragmentos del primer episodio con música para previsualizar el estilo de la serie.
135. **Monitor de Rendimiento en Tiempo Real (OSD Técnico / `Shift + I`)**:
     * Métricas de Bitrate Mbps, dropped frames, códec exacto y aceleración Direct3D activa.
136. **Arquitectura Formal de Plugins Comunitarios (Drop-in Scripts)**:
     * Soporte de plugins en Python y C# para añadir nuevos scrapers, índices o temas fácilmente.
137. **Sincronización con Google Calendar / Feed iCal**:
     * Feed de suscripción para ver los estrenos de tus animes en la app de calendario del celular.
138. **Exportador de Historial y Biblioteca a Excel / CSV**:
     * Exporta toda tu colección y estadísticas en tablas limpias para Excel o Google Sheets.
139. **Buscador Automático de Subtítulos en Español (OpenSubtitles)**:
     * Descarga y asocia subtítulos en español con un clic para videos que vengan solo en versión original.
140. **Calculadora de Valor Económico de tu Colección**:
     * Métrica curiosa del valor equivalente de tu biblioteca si se comprara en Blu-Rays físicos japoneses.

---

*Documento generado para el proyecto **AnimeLocalTracker**.*
