# Changelog

Todas las versiones notables de AnimeLocalTracker. El formato sigue [Keep a Changelog](https://keepachangelog.com/es/1.1.0/) y el proyecto usa [Versionado Semántico](https://semver.org/lang/es/).

Las versiones publicadas se generan automáticamente al crear un tag `vX.Y.Z` (GitHub
Actions + Velopack); las notas curadas de cada release se mantienen aquí.

## [No publicado]

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

## [1.0.5] - 2026-08
- Versión base documentada: lanzamiento con pipeline Velopack firmado, SCA bloqueante y 265+ tests.
