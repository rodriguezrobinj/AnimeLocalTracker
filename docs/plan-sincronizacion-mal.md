# Sincronización con MyAnimeList: diseño y plan

Fecha: 2026-10-18. Quinta función de `investigacion-integraciones-y-clips.md` (multi-scrobbler). Simkl va después, con su propio diseño.

## Objetivo

Con la cuenta de MAL conectada, la lista de MAL del usuario refleja su biblioteca: episodios vistos, estado y nota de cada anime con MalId. Un solo sentido (app → MAL). Funciona sin sesión de AniList y sin conexión (lo pendiente sale al volver la red).

Ejemplo: terminas el episodio 14 de Frieren y, como mucho 5 minutos después, MAL marca 14/28. Si en MAL ya ibas por el 20, la app no lo toca.

## Decisiones tomadas

- **Alcance al conectar:** toda la biblioteca, una vez; después solo los cambios. **Nunca se baja nada**: antes de escribir un anime se lee lo que tiene MAL; si MAL va más adelantado en progreso, o lo tiene `completed` y la app no, no se toca (y se apunta como sincronizado para no volver a preguntar).
- **Datos:** progreso, estado y nota. Sin fechas.
- **Enfoque:** espejo por diferencias. Por cada anime con MalId se calcula el estado deseado desde los datos locales y se compara con lo último enviado (tabla `SincronizacionMal`, migración v25); solo se envía lo distinto. Descartados: cola de eventos en los 6 puntos que hoy envían a AniList (frágil) y reenviar dentro del servicio de AniList (sin sesión de AniList no enviaría nada).
- Simkl queda fuera de esta fase.

## Conversión

- Progreso = máx(episodios vistos en local, `SeguimientoLocal.Progreso`), sin pasar de `TotalEpisodios` si se conoce. Con estado `completed` y total conocido, el progreso es el total (terminado en AniList sin archivos en disco: con 0, la regla de no bajar nada impediría marcarlo completado en MAL; visto en la revisión final).
- Estado (de `SeguimientoLocal.Estado` si existe; si no, `AnimeItem.EstadoUsuario`): CURRENT y REPEATING → `watching`, COMPLETED → `completed`, PAUSED → `on_hold`, DROPPED → `dropped`, PLANNING → `plan_to_watch`. Sin estado: `watching` si hay algo visto; si no hay nada visto, el anime no se envía (no se añade a la lista de MAL).
- Nota: `SeguimientoLocal.Puntaje` (0–100) → `round(p / 10)` en 0–10, redondeo hacia arriba en .5 (85 → 9); 0 = sin nota.

## Regla "no bajar nada" (función pura)

Dado lo deseado y lo que MAL tiene (o nada si no está en su lista):
- MAL no lo tiene → enviar lo deseado.
- MAL `completed` y deseado no `completed` → no tocar.
- MAL progreso > deseado progreso → no tocar.
- Si no, enviar solo si algo cambia; la nota deseada 0 no borra una nota que MAL ya tenga.

## Conexión (OAuth2 + PKCE, método `plain`, el único que admite MAL)

- Registro del propietario en myanimelist.net/apiconfig: tipo **other** (sin client secret), redirección `http://localhost:5051/callback`. El Client ID (público en ese tipo) va como constante; vacío = función inerte.
- `Conectar`: `code_verifier` aleatorio (43–128 caracteres seguros para URL), `state` aleatorio; abre `https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id=…&code_challenge=<verifier>&code_challenge_method=plain&state=…&redirect_uri=…`; `HttpListener` en `http://localhost:5051/` recibe `?code=&state=` en `/callback` (comprueba el state; límite 2 min, como AniList).
- Canje: POST `https://myanimelist.net/v1/oauth2/token` (form: `client_id`, `grant_type=authorization_code`, `code`, `redirect_uri`, `code_verifier`) → `access_token`, `refresh_token`, `expires_in`.
- Renovación: mismo POST con `grant_type=refresh_token`; si falla con 400/401 → desconectado (pide conectar de nuevo).
- Tokens cifrados con DPAPI en `AppDataPaths.DataRoot\mal-token.bin` (JSON con caducidad UTC); ruta inyectable. Nunca al registro.

## API de MAL

- `GET https://api.myanimelist.net/v2/users/@me` → nombre de usuario.
- `GET https://api.myanimelist.net/v2/anime/{malId}?fields=my_list_status` → `my_list_status { status, score, num_episodes_watched }` (ausente si no está en la lista).
- `PATCH https://api.myanimelist.net/v2/anime/{malId}/my_list_status` (form: `status`, `num_watched_episodes`, `score`).
- Cabecera `Authorization: Bearer`. Una petición por segundo como mucho; 429 o error → se para la pasada y se reintenta en la siguiente.

## Sincronización

- `MalSyncService` (singleton): pasada al arrancar (30 s después), cada 5 min, y con "Sincronizar ahora". Una pasada a la vez. Lee animes (`ObtenerTodosLosAnimesAsync`), vistos por anime, seguimientos locales y `SincronizacionMal`; para cada anime con MalId cuyo deseado ≠ último enviado: GET, decisión, PATCH si toca, guardar el nuevo "último enviado".
- `SincronizacionMal(AniListId PK, MalId, Estado, Progreso, Nota, EnviadoUtc)` — v25 (`CreateTableAsync`). Desconectar la borra (al reconectar, otra pasada completa sin bajar nada).
- Sin internet (`ConCorteSinConexion`) o sin conectar: la pasada no hace nada.

## Ajustes

Configuración → Descargas y cuenta, grupo "MyAnimeList": estado ("Conectado como X" / "Sin conectar"), Conectar / Desconectar, Sincronizar ahora, y la última pasada ("Última sincronización: hace 3 min · 2 animes actualizados"). Textos ES/EN.

## Pruebas

- Conversión de estado y nota; progreso con total.
- Regla "no bajar nada" (todas las ramas).
- PKCE (longitud, alfabeto) y URL de autorización.
- Canje y renovación de tokens con red simulada; almacén cifrado.
- Cliente de la API: GET/PATCH (método, URL, cuerpo, cabecera) con red simulada.
- Migración v25 con base vieja; métodos de `SincronizacionMal`.
- `MalSyncService`: una pasada envía solo lo distinto, guarda lo enviado, no envía sin conexión/sin token, se detiene ante un 429.
- Ajustes: Conectar/Desconectar/Sincronizar ahora.
- En vivo: con el Client ID y la cuenta del propietario (escribe en su MAL real, sin bajar nada).

---

# Plan de implementación

Ejecución en esta sesión, sin subagentes ni commits salvo orden. Cada tarea: prueba que falla → código → verde. Al cambiar `IDatabaseService`, actualizar `DummyDatabaseService` de los benchmarks.

1. **Piezas puras:** `MapeoMal` (estado, nota, progreso, deseado), `ReglaMal` (no bajar nada), `Pkce`.
2. **Tokens y conexión:** `AlmacenTokenMal` (DPAPI), `MalAuthService` (URL, listener, canje, renovación).
3. **Cliente de la API:** `MalApiClient` (usuario, leer estado, actualizar).
4. **Base de datos:** modelo `SincronizacionMal`, migración v25, métodos de lectura/escritura/borrado.
5. **Sincronización:** `MalSyncService` (pasada, temporizador, ritmo) + DI + arranque.
6. **Ajustes:** ViewModel, vista, textos.
7. **Cierre:** suite completa; verificación en vivo con el Client ID.
