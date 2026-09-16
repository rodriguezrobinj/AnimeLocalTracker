---
Proyecto: AnimeLocalTracker
Fase: 5 - Comportamiento Funcional y Observabilidad
Fecha de auditoría: 2026-09-14
Hora de inicio: 00:19:00
Duración de la fase: 12:00
---

# Fase 5 — Comportamiento Funcional y Observabilidad

## 1. Manejo global de excepciones — Verificado, cobertura sólida

`App.xaml.cs` registra los **tres** puntos de captura global recomendados para una app WPF:

1. `DispatcherUnhandledException` (excepciones no manejadas en el hilo de UI): loguea el detalle
   completo vía `AppLogger.Error`, muestra al usuario un `MessageBox` **genérico y sanitizado**
   ("Ocurrió un error inesperado... El detalle técnico se ha guardado en el registro") — el
   comentario `SEC-12` documenta explícitamente la decisión de no exponer rutas/versiones/drivers
   en la UI — y marca `args.Handled = true` para evitar el cierre abrupto de la app.
2. `AppDomain.CurrentDomain.UnhandledException` (excepciones fatales en hilos secundarios,
   no recuperables por diseño del CLR): logueadas antes de que el proceso termine.
3. `TaskScheduler.UnobservedTaskException` (excepciones de `Task` async nunca observadas):
   logueadas y marcadas `SetObserved()` para evitar que el finalizador las relance.

Esta cobertura de los tres puntos de fallo no manejado es más completa que lo que suele
encontrarse en apps de escritorio de un solo desarrollador — hallazgo positivo, no una carencia.

**Limitación de diseño a tener en cuenta (no necesariamente un bug):** al marcar
`args.Handled = true` en `DispatcherUnhandledException`, la app **continúa ejecutándose** tras un
error de UI no anticipado. Esto evita el cierre abrupto (bueno para no perder la sesión de
reproducción activa) pero también implica que la app puede seguir operando en un estado
potencialmente inconsistente tras el error, en vez de reiniciar limpio. Es una decisión de
trade-off razonable para software de consumo, documentada explícitamente en el propio código
(no un descuido) — se deja constancia para que el equipo confirme que es la decisión deseada en
todos los casos, no solo el caso feliz que motivó el comentario.

## 2. Calidad del logging — Verificado, arquitectura sólida

`Services/AppLogger.cs` implementa un logger asíncrono no bloqueante:
- Cola `Channel<LogEntry>` sin límite con un único consumidor en background (`ProcesarColaAsync`)
  — el comentario del propio archivo explica el porqué: el bucle de tracking del reproductor y
  los ticks de descarga loguean a alta frecuencia desde el hilo de UI, y un logger síncrono ahí
  introduciría jank perceptible.
- **Rotación por tamaño**: 5 MB máximo por archivo (`MaxLogBytes`), consistente con el claim de
  la UI ("Acerca de" → Privacidad: "5 MB log, 5 rotating backups", verificado en Fase 1).
- **Sanitización activa** (`Sanitizar()`, comentario `SEC-12`) antes de escribir a disco — evita
  volcar rutas completas del perfil de usuario en los logs, reduciendo el riesgo de fuga de PII
  (nombre de usuario de Windows) si el usuario comparte un log para soporte.
- Ring buffer en memoria de los últimos 500 registros (`RecentLogs`) expuesto vía evento
  `LogEmitted` — sugiere que existe (o se puede construir fácilmente) un visor de logs en vivo
  dentro de la propia UI de configuración, lo cual es una buena práctica de
  "self-service debugging" para un usuario final sin conocimientos técnicos.

**Trazabilidad ante fallo en producción: buena.** Con esta arquitectura, reconstruir "qué pasó"
ante un reporte de bug de un usuario es viable pidiéndole el archivo `app.log` — cumple el
objetivo de la fase.

## 3. Monitoreo/telemetría/health checks — Ninguno (por diseño, consistente con el claim de privacidad)

Confirmado en Fase 1: no hay ningún SDK de APM (Application Insights, Sentry, etc.) integrado.
Esto es coherente con el posicionamiento "local-first / sin telemetría invasiva" del README y con
el string de la propia UI de Privacidad. **No es un hallazgo negativo** dado el modelo de negocio
declarado (software libre, sin backend propio) — el costo es que el mantenedor depende
enteramente de que el usuario reporte bugs manualmente adjuntando `app.log` (no hay forma de que
el desarrollador se entere de un crash rate elevado en la población de usuarios sin que cada
usuario individualmente abra un issue). Se documenta como una limitación aceptada del modelo, no
como una carencia a corregir — introducir telemetría de crash-only opt-in (ej. un simple conteo
anónimo de crashes, sin contenido) sería una mejora incremental compatible con la postura de
privacidad, si el mantenedor decide que vale la pena en el futuro.

## 4. Manejo de errores de red/APIs externas — Parcialmente verificado (ver Fase 1 §4)

Confirmado en Fase 1: `AniListTrackingService.cs:79` maneja explícitamente HTTP 401 (token
rechazado) cerrando sesión y forzando reautenticación. No se generó tráfico de error controlado
(mocks de 5xx/timeout) contra AniList/AniSkip en esta corrida por estar fuera del alcance
permitido (no probar contra APIs reales de terceros) y por no haber revisado en profundidad la
suite de tests en busca de mocks existentes para estos escenarios — **recomendación de
seguimiento**: verificar en `AnimeLocalTracker.Tests` si existen tests parametrizados para 5xx,
timeout y JSON con esquema inesperado de AniList/AniSkip; si no existen, es una brecha real de
cobertura de resiliencia (no solo de código de producción).

## 5. Estados vacíos y edge cases de flujos críticos — No evaluado exhaustivamente

La app se encontró en esta corrida con una biblioteca real de 168 animes ya poblada (entorno del
propio usuario) — no se limpió ni se simuló un estado vacío ("sin animes añadidos", "carpeta de
biblioteca no configurada", "sin conexión a internet en el primer arranque") por ser un cambio de
estado potencialmente destructivo sobre datos reales del usuario, fuera del modo seguro de esta
auditoría. **Recomendación:** repetir esta sub-verificación en un perfil de Windows limpio o con
`ANIMELOCALTRACKER_LOG_DIR`/una carpeta de datos alternativa apuntada a un directorio vacío
(el propio `AppLogger.cs:27` ya soporta un override por variable de entorno, lo que sugiere que
el resto de rutas de `AppDataPaths` podrían tener un mecanismo similar para testing aislado —
a confirmar).

## 6. Resumen de hallazgos

| # | Hallazgo | Severidad |
|---|---|---|
| 1 | Cobertura de los 3 manejadores globales de excepciones (Dispatcher/AppDomain/TaskScheduler) | Ninguna (positivo) |
| 2 | Logger asíncrono, con rotación y sanitización de PII | Ninguna (positivo) |
| 3 | Continuar ejecución tras error de UI no anticipado (`Handled = true`) | Informativa — trade-off a confirmar con el equipo |
| 4 | Sin telemetría de crash — dependencia total de reporte manual del usuario | Informativa — coherente con el modelo de privacidad declarado |
| 5 | Cobertura de tests para fallos 5xx/timeout de AniList/AniSkip | No verificado — pendiente de revisión de la suite de tests |
| 6 | Estados vacíos / primer arranque sin biblioteca | No evaluado — requiere entorno aislado para no afectar datos reales del usuario |
