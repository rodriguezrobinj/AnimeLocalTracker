# Changelog

Todas las versiones notables de AnimeLocalTracker. El formato sigue [Keep a Changelog](https://keepachangelog.com/es/1.1.0/) y el proyecto usa [Versionado Semántico](https://semver.org/lang/es/).

Las versiones publicadas se generan automáticamente al crear un tag `vX.Y.Z` (GitHub
Actions + Velopack); las notas curadas de cada release se mantienen aquí.

## [No publicado]

### Añadido
- Editor de seguimiento de AniList rediseñado (botón «Seguimiento» de un anime): el estado se elige con chips de
  un clic en vez de un desplegable; los episodios vistos tienen botones − / + con el total; la puntuación
  muestra su valor junto al deslizador; las fechas de inicio y fin van en una fila con atajo «Hoy» y un
  calendario con los colores de la app (acento azul, en el idioma elegido y con la semana desde el lunes en
  español). Al elegir «Finalizado» se completa el progreso y la fecha de fin, y al
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
