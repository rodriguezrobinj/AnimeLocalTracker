# Investigación: AnimeLocalTracker 100 % offline (2026-09-29)

**Objetivo del producto:** la app es un espacio local. Con conexión se enriquece (AniList, descargas, AnimeThemes), pero **sin conexión todos los apartados deben seguir funcionando** con lo que ya está guardado, sin esperas ni pantallas vacías, y lo hecho offline se sincroniza solo al volver la red.

**Método:** revisión del código de cada apartado y de cada servicio que usa la red (25 archivos con HTTP), cruzado con el registro real del usuario (`app.log`) y con una **medición**: la configuración HTTP real de la app (política Polly de `App.GetRetryPolicy`) con un transporte que simula "sin internet".

> No se hizo una prueba en vivo con la app desconectada: la carpeta de datos es fija (`AppDataPaths`) y solo puede haber una instancia abierta, así que habría que cerrar la del usuario. Queda como paso de verificación (sección 5).

---

## 1. Resumen

La base offline **ya existe y es buena**: biblioteca, portadas, miniaturas, historial, estadísticas, logros, reproductor, marcas de OP/ED, música de la ficha y minijuegos leen de la base de datos o del disco. El progreso visto sin internet queda en cola (`SincronizadoEnNube = false`) y `SyncService` lo sube después sin retroceder el progreso remoto.

Lo que rompe la experiencia offline son **cuatro problemas**:

| # | Problema | Efecto para el usuario | Gravedad |
|---|----------|------------------------|----------|
| A | **Cada consulta a AniList tarda 60 s en rendirse sin internet** | Pantallas cargando un minuto antes de mostrar lo guardado o un error | **Alta** (transversal) |
| B | **Calendario y Actualizaciones no guardan copia local** | Si abres la app sin internet: 1 min de carga y luego vacías | **Alta** |
| C | **El editor de seguimiento (estado, puntuación, fechas) es solo online** | Sin internet muestra valores falsos (Viendo, 0 pts) y al guardar da error sin guardar nada | **Alta** |
| ~~D~~ | ~~La búsqueda global solo busca en AniList~~ — **error del informe**: ese código de `MainViewModel` no está conectado a ninguna vista (código muerto). Las búsquedas reales (Galería, Historial, Descargas) son locales | — | — |

Además hay huecos menores: la cola de descargas no sobrevive a cerrar la app, al volver la red la sincronización puede tardar hasta 10 min, el nombre y avatar de AniList no se guardan, y no hay un indicador global de "sin conexión".

---

## 2. Causa raíz transversal (A): la espera de 60 s

`App.GetRetryPolicy()` (usada por `AniListTrackingService` y `AniSkipService`) reintenta **cualquier error de red** (`HandleTransientHttpError` = `HttpRequestException`, 5xx, 408) con una espera fija de **60 s** pensada para el límite de peticiones de AniList (429). El `HttpClient` de AniList corta a los 60 s (`AniListTrackingService`, `Timeout = 60`), así que:

**Medición (2026-09-29):** con el transporte fallando al instante (como un DNS sin red):

```
ObtenerDatosExtraAsync sin red: 60,0 s, intentos 1, exito False
ObtenerAnimePorIdAsync (2a llamada) sin red: 60,0 s, intentos 2
```

Solo hay **un intento real**; los 59 s restantes son la espera del reintento hasta que el `HttpClient` lo corta. El circuit breaker no ayuda: cuando se abre, la política de reintento también espera 60 s ante `BrokenCircuitException`.

**Evidencia en el uso real:** el `app.log` del usuario muestra el circuit breaker abierto 9 veces entre el 28 y el 29 de septiembre y varios "Timeout al obtener…" (calendario, lote de animes, anime por ID). Eso pasa **con** internet cuando AniList va lento. Sin internet pasaría en cada consulta.

Otros clientes tienen su propio límite: `AnimeThemesService` 20 s, `PersonajesService` 15 s, `AniSkipService` 30 s (y `SkipTimesCoordinator` lo corta a 20 s), `Scraper` con 30 s de conexión.

---

## 3. Estado por apartado

✅ funciona sin conexión · ⚠️ funciona con esperas o limitado · ❌ no funciona

| Apartado | Estado | Detalle |
|----------|--------|---------|
| **Galería** | ✅ | Biblioteca de la BD, portadas en disco (`Covers/`). ⚠️ Nombre y avatar de AniList no se guardan: sin red sale el usuario por defecto. "Actualizar biblioteca" espera 60 s antes de fallar. |
| **Ficha** | ⚠️ | Episodios, reproducir, cuenta atrás (`ProximaEmisionLocal`), música (`AnimeThemesCatalog/`) ✅. `DatosExtraService` guarda copia 7 días, pero si está vencida **consulta primero** y solo devuelve lo guardado tras el fallo → hasta 60 s sin formato/estudio/nota. Botón "Actualizar" 60 s. **Editor de seguimiento ❌** (ver C). |
| **Reproductor** | ✅ | Reproducción, progreso y reanudación locales; marcas guardadas (`AnalisisSkipEpisodio`); el análisis nuevo usa los temas ya descargados; AniSkip cortado a 20 s; el visto al 95 % queda en cola para AniList. |
| **Historial** | ✅ | Solo BD. |
| **Estadísticas** | ✅ / ⚠️ | Todo local (franquicias con `RelacionAnime` guardadas). La tarjeta *Wrapped* espera 60 s el perfil de AniList antes de generarse. |
| **Logros** | ✅ | Solo BD. |
| **Calendario** | ❌ | "Conserva el último calendario" solo **en memoria** de la sesión. Abrir la app sin red → 60 s de carga → vacío con el aviso "Sin conexión — mostrando la última programación guardada" (que no existe). |
| **Actualizaciones** | ❌ | Igual que Calendario (feed solo en memoria). |
| **Descargas** | ⚠️ | Las descargas en curso esperan a que vuelva internet (hasta 60 × 1 min) y siguen solas ✅. No encontré código que guarde la cola: lo que quedó en espera se pierde al cerrar la app. |
| **Añadir anime** | ❌ (esperable) | Tendencias y búsqueda son de AniList. Sin red, 60 s y lista vacía, sin decir por qué. |
| **Búsquedas** | ✅ | Galería (título + nombres alternativos), Historial y Descargas filtran en local. La de `MainViewModel` es código muerto (ninguna vista la usa). |
| **Minijuegos** | ✅ / ⚠️ | "Adivina el anime" offline. OP/ED con los temas guardados (ya lo detecta y lo explica). Personajes: elige animes al azar y solo salen rondas de los que tienen personajes guardados (15 s de límite de red); puede quedarse corto de rondas sin avisar el motivo. |
| **Configuración** | ✅ | Local (el inicio de sesión en AniList es online por naturaleza). |
| **Acerca de** | ✅ | Changelog guardado (`release_info.json`). |
| **Sincronización al volver la red** | ⚠️ | Ciclo cada 5 min; tras un fallo, **10 min** de espera. No reacciona al volver la conexión. Solo sube el progreso de episodios (y fechas); los cambios del editor no pasan por la cola (ver C). |

---

## 4. Propuesta por fases

### Fase 1 — Fallar al instante sin red (arregla A; impacto en toda la app)

> **HECHA (2026-09-29, sin commit).**
> - `Services/GuardiaConexion.cs`: `GuardiaConexion` + `SinConexionHandler`, último eslabón de **todos** los clientes (`ConCorteSinConexion()`). Deja pasar una petición de prueba cada 30 s por si el indicador de Windows se equivoca.
> - `Services/PoliticasHttp.cs`: error de red o 5xx → 1 s y 2 s; 429 → Retry-After/60 s con circuito solo para 429. AnimeThemes tampoco reintenta sin conexión.
> - Añadir anime muestra "Sin conexión" en vez de "sin resultados"; "Actualizar biblioteca" ya no dice "completada" si no pudo consultar nada; `AppLogger` registra la falta de conexión como DEBUG de una línea.
> - Interruptor de pruebas `ANIMELOCALTRACKER_SIN_RED=1` (adelantado de la fase 4).
>
> **Medido en la app real con ese interruptor:** Calendario 0,76 s, Actualizaciones 0,15 s y Añadir anime 0,22 s hasta mostrar su aviso (antes 60 s cada una). BD y `settings.json` idénticos al respaldo tras la prueba. Suite 2095.
1. **Manejador HTTP "sin conexión"** (`DelegatingHandler` con `IConectividadRed`) en todos los clientes: si Windows dice que no hay internet, falla en milisegundos sin tocar la red.
2. **Separar reintentos:** 429 → esperar `Retry-After` (como ahora); error de red o 5xx → reintentos cortos (1-2-4 s) y nada si no hay conexión.
3. Mensajes claros ("Sin conexión") en "Actualizar biblioteca", "Actualizar" de la Ficha y Añadir anime, en vez de un minuto de espera.

*Efecto:* sin internet, cada pantalla muestra lo guardado al instante. Con AniList lento, las esperas bajan de 60 s a unos segundos.

### Fase 2 — Copias locales donde faltan (arregla B y C)

> **HECHA (2026-09-29, sin commit).**
> - **Calendario y Actualizaciones:** `ProgramacionEmisionService` + tabla `EmisionGuardada` (migración v17). Con conexión se guarda la ventana consultada; sin ella se muestra lo guardado, completado con `ProximaEmisionLocal`. El aviso dice si no hay nada guardado, y lo que ya estaba en pantalla no se vacía si la copia sale vacía.
> - **Editor de seguimiento:** tabla `SeguimientoLocal` (v18).
>   - Abre al instante con lo local; lo de AniList solo lo reemplaza si el usuario no tocó nada ni hay un cambio sin enviar.
>   - Guardar siempre escribe en local. Sin conexión queda `Pendiente` y `SyncService` lo envía primero; solo lo cierra si no cambió mientras tanto. Sin cuenta de AniList se guarda en local sin pendiente y sin error.
> - **Datos extra de la Ficha:** la copia vieja sale al instante y se refresca detrás.
> - **Perfil de AniList:** `PerfilAniListService` (`perfil_anilist.json` + `avatar_anilist.img`, solo de la CDN de AniList). Lo usan la Galería y la tarjeta Wrapped.
> - **Guardia más robusta:** en el equipo del usuario el indicador de Windows parpadea ("sin internet" 3 veces en 10 s con internet funcionando). La guardia ya no bloquea si algún servidor respondió en los últimos 2 min.
>
> **Verificado en la app real:**
> - con conexión se guardaron 39 episodios de programación;
> - con el interruptor sin red, el Calendario salió con 14 episodios y portadas, Actualizaciones con 26 (con descargado, visto y progreso) y la Galería con nombre y avatar;
> - las tablas existentes y `settings.json` quedaron idénticos al respaldo.
>
> Suite 2112 (una prueba de orden de portadas de la Galería es intermitente bajo carga: pasa 3/3 sola).
1. **Calendario y Actualizaciones:** guardar la última programación en la BD (migración nueva) y mostrarla **al abrir**, refrescando en segundo plano. Sin copia: reconstruir la semana con `ProximaEmisionLocal`.
2. **Editor de seguimiento offline:**
   - abrir con los valores locales (`EstadoUsuario`, `EpisodiosVistos`, puntuación y fechas guardadas);
   - guardar siempre en local;
   - dejar el cambio en una cola (tabla de cambios pendientes) que `SyncService` sube después, con la misma regla de no pisar un progreso remoto mayor.
3. ~~Búsqueda global~~: descartado, ver D.
4. **Datos extra de la Ficha:** mostrar la copia guardada al instante y refrescar detrás (*stale-while-revalidate*).
5. **Perfil de AniList** (nombre y avatar) guardado en disco.

### Fase 3 — Volver la red sin que el usuario haga nada

> **HECHA (2026-09-29, sin commit).**
> - `Services/EstadoConexionService.cs`: evalúa cada 5 s y con `NetworkStatusChanged`, usando `GuardiaConexion.PareceSinConexion`. Pide 2 lecturas iguales seguidas antes de cambiar de estado (el indicador de Windows parpadea en el equipo del usuario). Al volver la conexión sincroniza al momento si hay pendientes y envía `ConexionRecuperadaMensaje`, con el que Calendario y Actualizaciones se refrescan solos si mostraban la copia.
> - Indicador en la barra lateral (encima de Configuración): sin wifi o nube con el número de cambios pendientes (episodios vistos + editor), con tooltip localizado. Sin cuenta de AniList no cuenta pendientes.
> - Cola de descargas persistente: `cola_descargas.json` se reescribe en cada alta, baja, pausa o reanudación (escritura atómica). `RestaurarColaPendiente()` al arrancar: las pausadas vuelven en pausa, el resto se reanuda por su camino (HTTP o torrent) desde el `.downloading`/`.state`; se salta lo ya terminado y un archivo dañado no rompe el arranque. Sin ruta (pruebas) no se escribe nada.
> - "Adivina el personaje" sin conexión: mira toda la biblioteca y solo pregunta por personajes con imagen guardada.
>
> **Verificado en la app real (interruptor sin red):** indicador con "1" (el episodio pendiente real del usuario) y datos idénticos al respaldo. La transición de reconexión y la restauración de la cola solo están cubiertas por pruebas: con el interruptor la app no puede "volver" a tener red, y probar la cola en vivo lanzaría descargas reales. Suite 2120.
1. Escuchar `NetworkInformation.NetworkStatusChanged` para sincronizar al momento, refrescar Calendario y Actualizaciones y cerrar el circuit breaker.
2. Indicador global "Sin conexión · N cambios pendientes" en la barra lateral.
3. Cola de descargas persistente: se retoma al abrir la app.
4. Minijuego de personajes: sin red, elegir solo animes con personajes guardados y avisar si no alcanzan.

### Fase 4 — Verificación
1. Interruptor de pruebas "forzar sin conexión" (variable de entorno leída por `IConectividadRed`) para probar cada pantalla sin desconectar el equipo.
2. Pruebas por apartado: con el transporte fallando, cada carga termina en menos de 1 s y muestra lo guardado.
3. Prueba en vivo con la app del usuario cerrada:
   - respaldar antes con `VACUUM INTO` y `settings.json`;
   - recorrer todas las pestañas;
   - comparar la BD al terminar.

**Fuera de alcance, y por qué:** añadir un anime nuevo o buscar torrents/fuentes de descarga necesita internet por naturaleza. Lo correcto ahí es decirlo claro y al instante, no simularlo. Una variante futura: "añadir desde carpeta" sin datos de AniList y vincularlo cuando vuelva la red. Es más grande y queda como decisión aparte.

---

## 5. Archivos clave

- `App.xaml.cs`: `GetRetryPolicy()` y registro de clientes HTTP (causa A).
- `Services/ConectividadRed.cs`: `IConectividadRed`, hoy solo la usan las descargas.
- `Services/SyncService.cs`: cola de progreso (`SincronizadoEnNube`) y ciclo de 5/10 min.
- `Services/AniListTrackingService.cs`: `Timeout = 60 s`.
- `ViewModels/CalendarioViewModel.cs`, `ViewModels/ActualizacionesViewModel.cs`: sin copia persistente (B).
- `ViewModels/DetalleViewModel.cs`: `AbrirEditorSeguimiento…` / `GuardarEditorSeguimientoAsync` (C) y `CargarProximosEpisodiosDeAniListAsync`.
- `Services/DatosExtraService.cs`: consulta antes de devolver la copia vencida.
- `ViewModels/MainViewModel.cs`: búsqueda en vivo sin vista que la use (código muerto; se puede borrar).
- `ViewModels/GaleriaViewModel.cs`: `CargarPerfilUsuarioAsync`.
- `ViewModels/EstadisticasViewModel.cs`: `ConstruirDatosWrappedAsync`.

---

## 6. Correcciones tras el uso real (2026-09-29)

1. **La caída con la app abierta no se detectaba.**
   - Causa: tras una respuesta la guardia confiaba 2 min en la red y dependía del indicador de Windows, que siguió diciendo "hay internet".
   - Solución: los fallos de red reales (sin respuesta del servidor) cuentan y anulan esa confianza. `EstadoConexionService` hace su propia comprobación (`SondaInternet`: Microsoft, AniList, AnimeThemes; basta con uno) ante cualquier sospecha y en cada evaluación mientras está sin conexión. Se marca "sin conexión" tras 2 comprobaciones fallidas y se vuelve con una que funcione.
2. **La portada de la Ficha se quedaba cargando sin conexión.**
   - Causa: `AnimeItem.NotificarPortadaActualizada()` borraba la ruta del archivo guardado, así que tras la carga de portadas de la Galería todas apuntaban a la URL; con conexión se volvían a descargar cada vez.
   - Solución: ya no la borra, y quien avisa resuelve antes la ruta local en segundo plano.
3. **"Reintentar" de Añadir anime no se podía pulsar.**
   - Causa: la lista de resultados (transparente) iba encima del aviso en la misma celda y se quedaba con los clics. Afectaba también a "Limpiar filtros" y "Ver tendencias".
   - Solución: `Panel.ZIndex`, verificado con un clic de ratón real (la automatización de UI "pulsa" sin pasar por la capa de clics).

