# Changelog

Todas las versiones notables de AnimeLocalTracker. El formato sigue [Keep a Changelog](https://keepachangelog.com/es/1.1.0/) y el proyecto usa [Versionado Semántico](https://semver.org/lang/es/).

Las versiones publicadas se generan automáticamente al crear un tag `vX.Y.Z` (GitHub
Actions + Velopack); las notas curadas de cada release se mantienen aquí.

## [No publicado]

### Añadido
- Subtítulos que se solapan en el tiempo (dos personajes hablando a la vez) ahora se ven los dos, uno arriba y otro
  abajo del video, en vez de que uno tape al otro. La app extrae la pista completa de subtítulos (incrustada o en un
  archivo aparte) al abrir el episodio y decide por su cuenta qué va arriba y qué abajo; si la pista no se puede leer
  por algún motivo se sigue mostrando el subtítulo de siempre abajo, así nunca desaparecen por culpa de esta mejora.
- Filtro Todos/Openings/Endings en la ventana de música de la Ficha: con animes de muchos temas (One Piece: 34 openings +
  39 endings) evita una sola lista larguísima donde cuesta distinguir cuáles son openings y cuáles endings. Solo aparece
  cuando el anime tiene de los dos tipos; el filtro es solo de vista, nunca cambia qué suena ni la reproducción continua.
- La lista de capítulos de la Ficha ya no se queda atrás cuando otra pestaña (Actualizaciones) muestra un episodio recién
  emitido: en cuanto la cuenta atrás del próximo episodio detecta que ya salió, se añade su fila (sin descargar) y se
  actualiza el contador, sin tener que pulsar «Actualizar». Reutiliza el mismo dato que ya consulta la cuenta atrás, así
  que no añade ninguna llamada nueva a AniList.
- Marcadores de opening y ending en la barra de progreso del reproductor: franjas de color del mismo grosor que la barra (azul
  para el opening, ámbar para el ending) que crecen igual al pasar el cursor; la parte ya reproducida y la bolita toman el color del
  tramo en el que estás. Para ubicarlos la app usa primero el audio oficial de AnimeThemes: baja
  sola el audio de los temas del anime (unos 3 MB cada uno, en una caché con tope de 400 MB) y lo busca dentro del episodio,
  probando todos los temas y aceptando solo coincidencias claras. AniSkip queda como respaldo y solo se consulta si el audio no
  encontró el opening o el ending. El resultado se guarda por episodio (migración v13): la segunda vez los marcadores y el botón
  «Saltar intro» salen al instante y sin conexión. Los tramos aparecen en cuanto el audio los encuentra, sin esperar al resto.
- Ficha del anime, tres mejoras:
  - **Espacio en disco:** una etiqueta con lo que ocupa el anime en tu equipo y, en el menú ⋯, «Liberar espacio», que
    borra (con confirmación) los archivos de los episodios que ya viste. Siguen marcados como vistos en el historial.
  - **Avisos y descarga automática:** pulsando la cuenta atrás del próximo episodio se activa «Avisarme cuando salga»
    y/o «Descargar automáticamente». Solo cuentan los episodios posteriores a activarlo. La descarga espera unos
    minutos tras la emisión y reintenta con espera creciente (10 min hasta 16 h) si el episodio aún no está en el
    servidor, sin llenar el historial de descargas con fallos. Funciona con la app abierta; si estuvo cerrada, se pone
    al día al abrirla (hasta 5 episodios atrasados por anime).
  - **Más datos de AniList en etiquetas:** nota media, formato y duración, estudio, obra original y botón de tráiler.
    Se guardan en local y se refrescan como mucho una vez por semana.
- Cuenta atrás del próximo episodio en la ficha de un anime en emisión («Ep. 12 en 3 d 5 h», y en horas:minutos:
  segundos el último día). La hora de emisión se guarda en local (migración v8), así que el contador corre sin
  conexión y sin consultar AniList en cada visita. Solo se vuelve a preguntar a AniList cuando puede haber cambiado
  la programación, y con más frecuencia cuanto más cerca está la emisión: cada 24 h si faltan más de 3 días, cada
  6 h entre 6 h y 3 días, cada hora en las últimas 6 h; al pasar la hora de emisión se busca el episodio siguiente. El
  botón «Actualizar» de la ficha también revalida la hora. Si falla la red se sigue mostrando la cuenta guardada.
- Fechas de seguimiento automáticas en AniList: al ver (de verdad, reproduciendo) el primer episodio de un anime
  que aún no tenías empezado en AniList se guarda la fecha de inicio, y al ver el último episodio OFICIAL de un
  anime ya finalizado (el total que indica AniList, no simplemente el último de la lista) se guarda la de fin y el
  estado pasa a Completado.
  Nunca pisa una fecha que ya tengas, no toca los re-visionados (estado Completado o Repitiendo), no las inventa
  al marcar episodios como vistos a mano, y también se envían al recuperar la conexión si viste episodios sin
  internet.
- Editor de seguimiento de AniList rediseñado (botón «Seguimiento» de un anime): el estado se elige con chips de
  un clic en vez de un desplegable; los episodios vistos tienen botones − / + con el total; la puntuación
  muestra su valor junto al deslizador; las fechas de inicio y fin van en una fila y, al pulsarlas, el calendario
  se abre como tarjeta superpuesta (igual que el editor) en vez de un popup, con los colores de la app y en el idioma elegido; un clic en un día lo
  aplica y cierra, y hay «Quitar fecha». Pulsando el título del calendario («octubre de 2024») se pasa a la vista de meses y, pulsándolo otra vez, a la de años por décadas, para saltar
  a 2014 en pocos clics en vez de retroceder mes a mes; en la vista de décadas, pulsar el título de nuevo
  vuelve a los días. Al elegir «Finalizado» se completa el progreso y la fecha de fin, y al
  elegir «Viendo» se pone la fecha de inicio (sin pisar lo que ya hubiera escrito). El editor ya no arrastra
  el estado del anime anterior al abrirse.
- Pestaña **Descargas** rediseñada: dos pestañas, *Activas* e *Historial*. En Activas ves un resumen
  (descargando, en cola, velocidad total, completadas hoy) y cada tarjeta muestra su estado (descargando,
  en cola, en pausa, reintento), velocidad, tiempo restante estimado y un botón para **saltar la cola**
  ("descargar ahora") en las que esperan turno. El historial conserva entre sesiones las descargas
  completadas y las fallidas (hasta 500, migración v7 de la base de datos), agrupadas por día, con búsqueda,
  filtros (todas/completadas/fallidas) y acciones: reproducir, mostrar en la carpeta, reintentar (también
  cuando el archivo ya no está en disco), quitar del historial, reintentar todas las fallidas y vaciar.
  "Borrar todos mis datos" también vacía este historial.

### Cambiado
- El instalador ahora declara sus requisitos (`--framework net8-x64-desktop,vcredist143-x64`): si el PC no
  tiene el runtime de .NET 8 Desktop o el Visual C++ Redistributable 2015-2022, `Setup.exe` los descarga e
  instala solo (y Velopack también los instala al actualizar si una versión nueva sube el requisito). Antes,
  en un Windows recién instalado la app no abría (falta .NET) y el núcleo nativo Rust no cargaba
  (`animetracker_core.dll` importa `vcruntime140.dll`). Se puede cambiar o desactivar con `-Framework` en
  `build_velopack_release.ps1`.
- El instalador y los paquetes completos pesan unos 70 MB menos (~30 %): `ffmpeg.exe` y `ffprobe.exe`
  pasaron de builds estáticos de ~98 MB cada uno a builds "shared" de menos de 1 MB que reutilizan las
  DLLs de FFmpeg de su misma carpeta (las del reproductor). Miniaturas, análisis con ffprobe, audio y
  detección de escenas dan resultados idénticos. La versión de FFmpeg y su SHA256 quedan fijados en
  `tools/Get-FFmpegBinaries.ps1` (antes se descargaba "la última release" sin control). Ese script instala
  también las DLLs cuando faltan (checkout limpio, CI; las DLLs que ya haya en un equipo de desarrollo no se
  tocan) y comprueba que sean de la misma versión mayor; unas pruebas lo verifican en el CI.

### Accesibilidad
- Nombres accesibles (`AutomationProperties.Name`) en las tarjetas de anime de la galería y en
  los botones "Qué veo hoy", "Conectar", menú de usuario, "Cerrar sesión", "Filtros" y el badge
  de versión/actualizaciones — antes eran anunciados como "Botón" sin contexto por lectores de
  pantalla (Narrator).

### Corregido
- La lista de capítulos de la Ficha podía quedarse atascada en el episodio anterior aunque ya hubiera salido uno nuevo:
  si AniList todavía no tenía programada la fecha del episodio siguiente (algo habitual justo después de una emisión,
  antes de que confirme el próximo horario), la app se quedaba sin ninguna referencia de "qué es lo último que salió" y
  la corrección automática de la lista (ver «Añadido») no se disparaba. Ahora, cuando pasa esto, se pregunta además por
  el último episodio que SÍ consta como emitido en el calendario de AniList, así la lista se sigue poniendo al día sin
  esperar a que AniList programe el episodio siguiente.
- La lista de capítulos de la Ficha ya no se agranda sin sentido por un video local mal nombrado (p. ej. «Episodio
  3000.mp4» en una carpeta con 12 episodios reales): el total oficial de AniList manda cuando se conoce, y un número de
  archivo muy por encima de ese total simplemente no recibe fila (el archivo sigue en el disco, solo no aparece listado).
  Un episodio real solo un poco por delante (preestreno o filtración de 1-2 días, o algún especial numerado justo
  después del final) sí se sigue mostrando: el límite deja un margen razonable antes de tratar el número como ruido.
- El flujo de firma de código de las releases tenía cuatro errores que habrían impedido firmar aunque hubiera
  certificado: la plantilla de `signtool` usaba `$file` en vez del marcador `{{file}}` que sustituye `vpk`, no
  había sello de tiempo (las firmas dejarían de ser válidas al caducar el certificado), el paso de verificación
  del CI buscaba `Releases\Setup.exe` (el archivo real es `AnimeLocalTracker-win-Setup.exe`) y solo se
  comprobaba el instalador. Ahora se usa `--signParams` con `/tr`+`/td`, y `tools/Test-ReleaseSignature.ps1`
  verifica el instalador y los binarios propios de dentro (exe, núcleo Rust, motor Python). Se probó de punta a
  punta con un certificado de prueba autofirmado. `build_velopack_release.ps1` admite además `-SignParams` y
  `-ReleasesDir`.
- La resolución, el códec de video, los fps y el indicador de 10 bits de cada episodio ya se
  obtienen: el motor Python le pasaba a `ffprobe` la opción `-nostdin` (solo válida en `ffmpeg`), así
  que `ffprobe` rechazaba el comando siempre y el fallo se descartaba en silencio. Lo mismo impedía
  conocer la duración del video en el plan B de detección de intro/ending.
- El plan B de detección de intro/ending (cuando OpenCV falla) usaba un filtro `scdet` inválido
  (`s=0.30` es un booleano, no un umbral) y respondía "éxito" sin ninguna escena porque no se
  comprobaba el código de salida de `ffmpeg`. Ahora usa `t=30:s=1` y un fallo de `ffmpeg` se informa.
- La barra de pestañas de filtro ("Todos/Viendo/Completados/Planeando") ya no se solapa con el
  buscador ni recorta contenido al reducir el ancho de la ventana por debajo de ~800px — ahora
  se reordena a una segunda línea.
- El proyecto `AnimeLocalTracker.Benchmarks` volvía a compilar: el mock `DummyDatabaseService`
  no implementaba 5 miembros nuevos de `IDatabaseService`.
- `run_benchmarks_and_reports.ps1` ya no reporta éxito cuando el build o los tests fallan —
  ahora propaga el código de salida real, para que el workflow de CI de Benchmarks deje de
  marcar el job en verde con builds rotos.

### Desarrollo
- Refactor de navegación, corrección del cálculo de episodios vistos e inclusión de velocidad de
  descarga en tiempo real.
- Corrección del enlazado del ComboBox de velocidad de reproducción (tipo `double`).
- Refactor del daemon Python a distribución "onedir" (remediación UX-001/ARC-009).
- Fallback a AniSkip cuando el plugin de audio no resuelve tiempos de salto; se removió lógica
  obsoleta del reproductor.

## [1.0.5] - 2026-08

### Seguridad
- Validación exacta (Uri) del origen del callback OAuth y token de un solo uso por login (SEC-001/SEC-005).
- Cierre de sesión local automático cuando AniList rechaza el token (401) (SEC-002).
- Descargas: tope de 35 GB en modo secuencial y redirecciones validadas solo-https (SEC-003).
- Portadas: solo desde la CDN de AniList por https y con firma de imagen validada antes de guardar (SEC-004).
- Codificación UTF-8 estricta y LibraryImport source-generated en el FFI Rust; aviso si falta el ffmpeg embebido (SEC-006/SEC-007/ARC-008).
- Rutas del perfil de usuario saneadas en los logs (SEC-012).
- CI con mínimo privilegio; el proyecto de tests se audita en el SCA; xunit 2.9.3 (SEC-008/SEC-009).

### Funcionalidad
- La sincronización ya no degrada el progreso remoto de AniList y detecta errores GraphQL en el cuerpo de la respuesta (FUN-001/FUN-002).
- El ajuste "porcentaje para marcar como visto" gobierna el auto-marcado; un video truncado ya no se marca como visto; los archivos sin número no tocan el progreso (FUN-003/004/012).
- Intervalo de sincronización configurable y "buscar actualizaciones al iniciar" operativo (FUN-005).
- Reanudación segura (valida la ruta del archivo y acota el seek a la duración real) y "Reanudar" elige el episodio más reciente (FUN-006/010).
- Guardado de progreso serializado; notificador de episodios nuevos periódico (cada 30 min) (FUN-008/011/017).
- Descargas: reanudación sin 206 reinicia limpio; watchdog de 60 s sin datos; trazabilidad en app.log (FUN-014/015/016).
- Import JSON con dedupe y errores claros; la descarga no resetea episodios ya vistos (FUN-013/019).
- Escrituras por lotes: enriquecimiento de episodios, actualización de biblioteca y categorización (PERF-005/006).
- Proyecciones ligeras sin sinopsis en calendario/notificador y comprobación de existencia sin cargar la biblioteca (PERF-002/003).
- Export/import JSON fuera del hilo de UI; índice de la cola de sincronización (migración v2) (PERF-007/010).
- Refrescos coalescidos en la ficha de episodios y limpieza de portadas corruptas (PERF-001/008).

### Arquitectura
- Navegación con NavigationService y DataTemplates VM→Vista; vistas registradas en DI eliminadas; el reproductor usa el contrato IVentanaPrincipal (ARC-002/ARC-004).
- Caché única del mapeo AniList→MAL; migraciones versionadas de SQLite; sin async void en producción (ARC-005/006/011).
- El daemon Python se reintenta con backoff tras un handshake fallido (INT-004).

### Privacidad y UX
- "Borrar todos mis datos" con doble confirmación en Configuración (PRI-001).
- Consentimiento informado antes de conectar AniList y sección de privacidad en Acerca de (PRI-002/003).
- Contraste AA en texto terciario, botones primarios, chips y badges; nombres accesibles en botones de icono (UX-001/002/003).
- Archivo LICENSE MIT publicado (MKT-001).

### Desarrollo
- Benchmarks ejecutables con historial unificado y workflow manual/semanal (OPS-001).
- clippy -D warnings en CI; umbral de ramas en la cobertura; versión en el log de arranque (OPS-004/005/008).
