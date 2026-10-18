# Discord Rich Presence: diseño

Fecha: 2026-10-17. Primera función de `investigacion-integraciones-y-clips.md` (#72 de `propuestas-nuevas-funciones.md`).

## Objetivo

Mientras se ve un episodio en el reproductor, el perfil de Discord del usuario muestra qué anime y qué episodio está viendo, con la portada, el tiempo transcurrido y restante, y un botón "Ver en AniList". Desactivado por defecto.

Ejemplo: "Viendo AnimeLocalTracker · Frieren: Beyond Journey's End · Episodio 14", portada de AniList y la barra "12:35 / 24:10".

## Decisiones tomadas

- **ID de aplicación:** el propietario crea la aplicación "AnimeLocalTracker" en el Discord Developer Portal y aporta su ID (público, no secreto). Queda como constante en el código. También sube un recurso de imagen `logo` (portada de reserva).
- **Alcance:** solo durante la reproducción de episodios. Sin música (AnimeThemes) ni estado "navegando la biblioteca".
- **Botón:** "Ver en AniList" → `https://anilist.co/anime/{AniListId}`. Etiqueta en el idioma de la app. El propio usuario no ve sus botones en Discord; solo los demás.
- **Implementación:** cliente IPC propio, sin dependencias nuevas. Descartados: NuGet `DiscordRichPresence` (dependencia para ~150 líneas, soporte de "Viendo"/botones por confirmar) y Discord Social SDK (DLL nativa para juegos, desproporcionada).

## Protocolo (IPC local de Discord)

- Tubería con nombre `\\.\pipe\discord-ipc-{0..9}`: se prueba en orden y se usa la primera que conecte.
- Trama: opcode `int32` little-endian + longitud `int32` little-endian + JSON UTF-8.
  - Opcode 0 (HANDSHAKE): `{"v":1,"client_id":"<ID>"}`. Discord responde con un evento `READY`.
  - Opcode 1 (FRAME): `{"cmd":"SET_ACTIVITY","args":{"pid":<pid>,"activity":{…}},"nonce":"<guid>"}`. `activity: null` borra el estado.
  - Opcode 2 (CLOSE): Discord cierra la conexión; se trata como desconexión.
- Al cerrar la app (o morir el proceso), Discord borra el estado solo al cerrarse la tubería.

## Contenido de la actividad

| Campo | Reproduciendo | En pausa |
|---|---|---|
| `type` | 3 (Viendo) | 3 |
| `details` | Título del anime | Título del anime |
| `state` | "Episodio 14" (`Act_EpisodioFormato`) | "Episodio 14 · En pausa" (clave nueva) |
| `timestamps` | `start` = ahora − posición; `end` = `start` + duración (segundos Unix) | sin `timestamps` |
| `assets.large_image` | `UrlPortada` del anime si es `https`; si no, `logo` | igual |
| `assets.large_text` | Título del anime | igual |
| `buttons` | `[{"label":"Ver en AniList","url":"https://anilist.co/anime/{id}"}]` | igual |

- La portada se lee de `AnimeItem.UrlPortada` por `AniListId` (lectura de BD una vez por episodio, fuera del hilo de interfaz). La `rutaPortada` que recibe el reproductor puede ser un archivo local de la caché y no sirve para Discord.
- Si la duración aún no se conoce (0), se envía `start` sin `end`.
- Textos acotados a 128 caracteres (límite de Discord para `details`/`state`/`large_text`) y 32 para la etiqueta del botón.

## Piezas

### `Services/Discord/ClienteIpcDiscord.cs`
- Conecta, envía el saludo, envía `SET_ACTIVITY` (actividad o `null`) y lee y descarta las respuestas.
- Sin Discord abierto o con la tubería rota: no lanza; devuelve fallo y el servicio reintenta como mucho cada 30 s. Se registra una sola vez por desconexión (`AppLogger`), no en cada intento.
- Funciones puras (probadas): `CodificarTrama(opcode, json)` y su lectura.

### `Services/Discord/IDiscordPresenceService.cs` + `DiscordPresenceService.cs` (singleton en DI)
- `void Actualizar(EstadoPresencia estado)`, donde `EstadoPresencia` = `(int AnimeId, string Titulo, int Episodio, double PosicionSeg, double DuracionSeg, bool Reproduciendo)`.
- `void Limpiar()`: borra el estado en Discord.
- `void Suprimir()`: tecla de pánico; borra y no vuelve a publicar hasta un `Actualizar` con otro episodio u otro anime.
- Lee el ajuste `DiscordRichPresence`; apagado → no conecta y no publica nada. Al apagarse en caliente (`ConfiguracionModificada`), `Limpiar()` y cierra la tubería.
- Reglas de envío (función pura, probada con `TimeProvider`):
  - Cambio de anime, episodio o reproduciendo↔pausa → enviar.
  - Reproduciendo y la posición se desvía más de 3 s de la esperada desde el último envío (salto en el video) → enviar.
  - Nunca más de un envío cada 5 s; el último estado pendiente se envía al vencer el plazo (gana el más reciente).
  - Sin cambios → no enviar (el tic de 250 ms del reproductor no genera tráfico).
- Todo el trabajo de red y de BD va en segundo plano; `Actualizar` y `Limpiar` solo dejan el estado y vuelven. Un solo trabajador procesa los envíos en orden.
- `ConstruirActividad(estado, urlPortada, ahora)` → JSON: función pura probada.

### Enganches
- `ReproductorViewModel` (dependencia opcional, como `_smtc`):
  - Bucle `RastrearProgresoAsync` (cada 250 ms, en cualquier estado del motor): `Actualizar(...)` con la posición real y si el motor está reproduciendo. Un solo enganche cubre pausa, reanudación, saltos, fin y cambio de episodio, vengan del teclado, del SMTC o del mini reproductor. No publica hasta que el video abrió (`_haCompletadoOpen`) ni con la reanudación pendiente.
  - `Dispose()` (donde hoy se llama a `_smtc.Deshabilitar()`): `Limpiar()`.
- `MainWindow.PanicKeyService_Activado`: `Suprimir()`.
- `AppSettings.DiscordRichPresence` (`bool`, `false` por defecto) + interruptor en `ConfiguracionView.xaml` / `ConfiguracionViewModel` junto a la tecla de pánico. Textos ES/EN con el skill `localizacion`: interruptor, descripción, "En pausa" y "Ver en AniList".
- Registro en `App.xaml.cs`.

## Errores y casos límite

- Discord cerrado al abrir el episodio y abierto después: el reintento cada 30 s lo encuentra y publica el estado vigente.
- Discord se cierra a mitad: escritura fallida → desconectado, reintento a los 30 s; el reproductor no se entera.
- Saludo rechazado (p. ej. ID inválido) o respuesta `ERROR` a `SET_ACTIVITY`: se registra una vez como aviso; el reintento sigue cada 30 s (una conexión local, sin coste).
- ID de aplicación aún vacío: el servicio no conecta ni publica (inerte) hasta que se rellene la constante.
- Mini reproductor / pantalla completa: mismo `ReproductorViewModel`, sin casos aparte.
- Video sin anime asociado (`AnimeId` 0): sin botón ni portada de AniList; se usa `logo`.

## Pruebas

xUnit + FluentAssertions + Moq, sin red ni Discord real:
- `CodificarTrama`: cabecera de 8 bytes little-endian + longitud correcta con caracteres no ASCII (UTF-8).
- `ConstruirActividad`: reproduciendo (con `start`/`end`), en pausa (sin `timestamps`, "En pausa"), duración desconocida, URL no `https` → `logo`, botón con id y recorte a 128/32 caracteres.
- Reglas de envío con `TimeProvider` falso: cambio de episodio, pausa, salto > 3 s, desvío < 3 s ignorado, límite de 5 s con último estado pendiente, `Suprimir` hasta otro episodio, ajuste apagado.
- `ReproductorViewModel` con `IDiscordPresenceService` simulado: `Limpiar()` al cerrar.

Verificación en vivo (con el skill `perfil-aislado`): requiere Discord abierto; en la máquina de desarrollo no lo estaba al diseñar, así que la confirma el propietario.

## Fuera de alcance

Música de AnimeThemes, estado en reposo, campo de ID personalizado en Ajustes, segundo botón.

---

# Plan de implementación

**Objetivo:** el estado "Viendo" de Discord refleja el episodio en reproducción, según el diseño de arriba.
**Arquitectura:** tres piezas puras y probadas (`ActividadDiscord`, `ReglaEnvioPresencia`, tramas de `ClienteIpcDiscord`), un servicio singleton con un trabajador en segundo plano, y tres enganches (reproductor, tecla de pánico, Ajustes).
**Tecnología:** .NET 8, `System.IO.Pipes`, `System.Text.Json.Nodes`, `TimeProvider`; pruebas con xUnit + FluentAssertions + Moq.
**Ejecución:** en esta sesión (sin subagentes, regla del repo). Sin commits: solo si el propietario lo pide (`commit-es`).

## Restricciones globales

- Sin dependencias NuGet nuevas.
- Textos visibles con `LocalizationService.T` / `{loc:T}` en las dos mitades (ES/EN), skill `localizacion`.
- Nada de trabajo de red o BD en el hilo de interfaz; el servicio nunca lanza hacia el reproductor.
- Las pruebas no abren la tubería real de Discord (con Discord abierto publicarían en el perfil del propietario).
- Compilar y probar con el skill `repo-build-test`.

## Focos de revisión (casos que el diseño implica y que pueden fallar)

1. Velocidad de reproducción distinta de 1×: la posición se desvía de lo esperado → reenvío como mucho cada 5 s, nunca más (prueba en la tarea 1).
2. Títulos de 1 carácter (p. ej. "K"): Discord rechaza `details`/`state` de menos de 2 caracteres → se rellenan (prueba en la tarea 1).
3. Pulsar la tecla de pánico y reanudar el mismo episodio: no debe volver a publicarse (prueba en la tarea 3).
4. Apagar el interruptor con el estado publicado: se borra (prueba en la tarea 3).
5. Respuestas de Discord no leídas llenando la tubería en sesiones largas: el cliente lee una respuesta por cada envío (revisión de código, tarea 2).

## Tarea 1: piezas puras (actividad y regla de envío)

**Archivos:**
- Crear: `AnimeLocalTracker/Services/Discord/EstadoPresencia.cs`, `ActividadDiscord.cs`, `ReglaEnvioPresencia.cs`.
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (claves `Discord_EnPausaFormato`, `Discord_VerEnAniList`).
- Probar: `AnimeLocalTracker.Tests/Services/DiscordPresenciaTests.cs`.

**Produce:**
- `public sealed record EstadoPresencia(int AnimeId, string Titulo, int Episodio, double PosicionSeg, double DuracionSeg, bool Reproduciendo);`
- `internal static JsonObject ActividadDiscord.Construir(EstadoPresencia e, string? urlPortada, DateTimeOffset ahora)`
- `internal static string ActividadDiscord.ComandoSetActivity(JsonObject? actividad, int pid)`
- `internal static bool ReglaEnvioPresencia.HayCambio(EstadoPresencia? enviado, DateTimeOffset enviadoEn, EstadoPresencia nuevo, DateTimeOffset ahora)`; constantes `IntervaloMinimo = 5 s`, `DesvioMaximoSeg = 3`.

- [ ] Pruebas que fallan:
  - Reproduciendo, posición 755 s, duración 1450 s, ahora = T → `type` 3, `details` = título, `state` = "Episodio 14", `timestamps.start` = T − 755, `end` = `start` + 1450.
  - En pausa → sin `timestamps`, `state` = "Episodio 14 · En pausa".
  - Duración 0 → `start` sin `end`.
  - URL `https://…` → `large_image` = URL; URL vacía o ruta local → `"logo"`.
  - `AnimeId` 101 → botón `https://anilist.co/anime/101`; `AnimeId` 0 → sin `buttons`.
  - Título de 200 caracteres → `details` de 128; título "K" → `details` de 2 caracteres.
  - `ComandoSetActivity(null, 1234)` → JSON con `"cmd":"SET_ACTIVITY"`, `"pid":1234`, `"activity":null`.
  - `HayCambio`: sin enviado → true; otro episodio → true; pausa ↔ reproducción → true; misma posición esperada (+2 s tras 2 s) → false; salto de +60 s → true; desvío de 2 s → false; en pausa con otra posición → false; duración conocida tarde (0 → 1450) → true.
- [ ] Ejecutar y ver que fallan (no compilan).
- [ ] Implementar las tres piezas y las dos claves ES/EN.
- [ ] Ejecutar las pruebas del archivo: pasan.

## Tarea 2: cliente IPC

**Archivos:** crear `AnimeLocalTracker/Services/Discord/ClienteIpcDiscord.cs`; pruebas en `DiscordPresenciaTests.cs`.

**Produce:**
- `internal static byte[] CodificarTrama(int opcode, string json)` y `internal static (int Opcode, int Longitud) LeerCabecera(ReadOnlySpan<byte> cabecera)`.
- `Task<bool> ConectarAsync(string clientId, CancellationToken ct)`: prueba `discord-ipc-0..9` (200 ms cada una), saludo opcode 0, éxito si responde opcode 1 con `READY`.
- `Task<bool> EnviarActividadAsync(string comando, CancellationToken ct)`: escribe opcode 1 y lee UNA respuesta (máx. 3 s); `ERROR` → aviso una vez; opcode 2 o fallo de E/S → cierra y devuelve false.
- `bool Conectado`, `void Cerrar()`, `IDisposable`.

- [ ] Pruebas que fallan: trama de `{"a":"ñ"}` → 8 bytes de cabecera + longitud en bytes UTF-8 (no en caracteres), little-endian; `LeerCabecera(CodificarTrama(1, x))` devuelve `(1, bytes(x))`.
- [ ] Implementar. La parte de E/S no se prueba con la tubería real.
- [ ] Pruebas en verde.

## Tarea 3: servicio y registro en DI

**Archivos:**
- Crear: `AnimeLocalTracker/Services/Discord/IDiscordPresenceService.cs`, `DiscordPresenceService.cs`.
- Modificar: `AnimeLocalTracker/Models/AppSettings.cs` (`public bool DiscordRichPresence { get; set; } = false;`), `AnimeLocalTracker/App.xaml.cs` (singleton).
- Probar: `DiscordPresenciaTests.cs`.

**Produce:**
- `IDiscordPresenceService { void Actualizar(EstadoPresencia estado); void Limpiar(); void Suprimir(); }`
- Constructor público `(ISettingsService ajustes, IDatabaseService db)`; constructor interno `(…, string clientId, TimeProvider reloj, bool conTrabajador)` para pruebas; `internal EstadoPresencia? Deseado`.
- `internal const string ClientIdDiscord = "";` (se rellena con el ID del portal).

- [ ] Pruebas que fallan (con `conTrabajador: false`):
  - Ajuste apagado → `Actualizar` deja `Deseado` en null.
  - Ajuste encendido → `Deseado` = último estado.
  - `Limpiar()` → `Deseado` null.
  - `Suprimir()` y luego `Actualizar` del mismo anime y episodio → sigue null; con otro episodio → vuelve a publicarse.
  - `ConfiguracionModificada` con el ajuste apagado → `Deseado` null.
- [ ] Implementar. Trabajador: un bucle en `Task.Run` que despierta con cada cambio o cada segundo; aplica `HayCambio` + `IntervaloMinimo`; conecta con espera de 30 s entre intentos; lee `UrlPortada` con `ObtenerAnimePorIdAsync` (en caché por anime); sin deseado y con algo publicado → envía `activity: null`; ajuste apagado → además cierra la tubería; todo dentro de `try/catch` con registro.
- [ ] Pruebas en verde.

## Tarea 4: enganches y Ajustes

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` (parámetro opcional `IDiscordPresenceService? discordPresence = null` al final del constructor; `PublicarPresencia()` al final de cada vuelta de `RastrearProgresoAsync`; `Limpiar()` en `Dispose`).
- Modificar: `AnimeLocalTracker/Views/MainWindow.xaml.cs` (`PanicKeyService_Activado` → `Suprimir()`).
- Modificar: `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs` (propiedad `DiscordRichPresence`: cargar y guardar como `TeclaPanicoActiva`) y `AnimeLocalTracker/Views/ConfiguracionView.xaml` (`SettingRow` + `CfgSwitch` en el grupo general, con `AutomationProperties.Name`).
- Modificar: `LocalizationService.cs` (`Cfg_DiscordPresencia`, `Cfg_DiscordPresenciaSub`).
- Probar: `AnimeLocalTracker.Tests/ViewModels/ReproductorViewModelTests.cs`.

- [ ] Prueba que falla: `Dispose()` del reproductor con `IDiscordPresenceService` simulado llama a `Limpiar()` una vez.
- [ ] Implementar los enganches, el ajuste y los textos.
- [ ] Compilar la app y ejecutar las pruebas de Discord, del reproductor y de Configuración.

## Tarea 5: cierre

- [ ] Suite completa una vez (`repo-build-test`).
- [ ] Informar al propietario: falta el ID de la aplicación y el recurso `logo`; la prueba en vivo con Discord abierto la hace él (con `perfil-aislado`).
