# Avisos en un canal de Discord (webhook): diseño y plan

Fecha: 2026-10-17. Tercera función de `investigacion-integraciones-y-clips.md`.

## Objetivo

Publicar en un canal de Discord del usuario (por webhook, sin bot) una tarjeta con portada cuando:
- **se emite un episodio** de un anime con "Avisar" activado en su ficha (el mismo aviso que hoy da `EmisionMonitorService`);
- **el usuario termina una serie**: ve de verdad el último episodio de una serie ya finalizada.

Ejemplo: "Frieren · Episodio 15 — Ya se emitió" (ámbar) y, semanas después, "🎉 Terminaste Frieren — 28 episodios" (verde).

## Decisiones tomadas

- Solo Discord (sin Telegram). Eventos: episodio emitido y serie terminada. **No** descarga completada ni archivos nuevos en la biblioteca.
- "Serie terminada" se detecta en local, sin AniList: `MarcarComoVistoYSincronizarAsync` con reproducción real, `Estado == "FINISHED"`, `TotalEpisodios > 0`, `episodio >= TotalEpisodios`, y el episodio NO estaba ya visto (re-ver el final no repite el aviso).
- **Enfoque:** mensajes internos (`WeakReferenceMessenger`). El monitor y `PlaybackStateService` publican `EpisodioEmitidoMensaje` y `SerieTerminadaMensaje`; un servicio singleton los escucha y llama al webhook. Descartado: llamar al servicio directamente (acopla y obliga a cambiar constructores; el reproductor crea su `PlaybackStateService` a mano).
- La URL del webhook es un secreto (permite escribir en el canal): **cifrada con DPAPI** en `AppDataPaths.DataRoot\discord-webhook.bin`, no en `settings.json`. Ruta inyectable para pruebas.
- Sin cola persistente: sin internet o con Discord caído, se registra y se descarta (el aviso de emisión ya necesita internet para existir).

## Contenido (embeds de Discord)

POST `application/json` a la URL:
```json
{ "username": "AnimeLocalTracker",
  "embeds": [{ "title": "…", "description": "…", "url": "https://anilist.co/anime/<id>", "color": <int>,
               "thumbnail": { "url": "<UrlPortada https>" }, "timestamp": "<UTC ISO-8601>" }] }
```
- Episodio emitido: título `"{Título} · " + Act_EpisodioFormato` (uno) o `Webhook_EpisodiosFormato` "Episodios {0}–{1}" (varios); descripción `Webhook_YaSeEmitio`; color ámbar `0xF59E0B`.
- Serie terminada: título `Webhook_TerminasteFormato` "🎉 Terminaste {0}"; descripción `Webhook_EpisodiosTotalFormato` "{0} episodios"; color verde `0x34D399`.
- Sin `thumbnail` si la portada no es `https`; sin `url` si el id es 0. Título ≤ 256 caracteres (límite de Discord).
- Textos en el idioma de la app.

## Ajustes

- `AppSettings`: `WebhookAvisarEmision` y `WebhookAvisarSerieTerminada` (`bool`, `true` por defecto: sin URL no se envía nada).
- Configuración → General, grupo nuevo "Avisos en Discord": `TextBox` de la URL (se muestra enmascarada si ya hay una guardada), botón **Probar** (manda un embed de prueba con la URL escrita) y los dos interruptores. La URL se guarda con "Guardar preferencias" (vacía = borrar). URL válida: `https://` + `discord.com` | `discordapp.com` | `ptb.discord.com` | `canary.discord.com` + `/api/webhooks/`; si no, aviso y no se guarda.
- Textos ES/EN (skill `localizacion`).

## Piezas

- `Messages/EpisodioEmitidoMensaje.cs` (`record(int AnimeId, string Titulo, string? UrlPortada, int Desde, int Hasta)`) y `Messages/SerieTerminadaMensaje.cs` (`record(int AnimeId, string Titulo, string? UrlPortada, int TotalEpisodios)`).
- `Services/Webhook/EmbedWebhook.cs` (puro): `EpisodioEmitido(...)`, `SerieTerminada(...)`, `Prueba()` → `string` JSON; `EsUrlValida(string)`.
- `Services/Webhook/AlmacenWebhook.cs`: `string? Leer()`, `void Guardar(string? url)` (null/vacía borra), DPAPI `CurrentUser`; ruta inyectable.
- `Services/Webhook/IWebhookDiscordService.cs` + `WebhookDiscordService.cs` (singleton, `IRecipient` de los dos mensajes): lee ajustes y URL, `Task<bool> EnviarAsync(string url, string json)` con `IHttpClientFactory` (cliente con nombre `"Webhook"`, `ConCorteSinConexion`); `Task<bool> ProbarAsync(string url)`; nunca lanza; registro sin la URL (es secreta).
- Enganches: `EmisionMonitorService.AvisarEpisodios` publica `EpisodioEmitidoMensaje`; `PlaybackStateService.MarcarComoVistoYSincronizarAsync` publica `SerieTerminadaMensaje`; arranque del servicio en `App.xaml.cs` (paso junto al monitor de emisión).
- `ConfiguracionViewModel` + `ConfiguracionView.xaml`: URL, Probar, interruptores.

## Pruebas

- `EmbedWebhook`: JSON de los dos eventos (título, descripción, color, thumbnail, url, uno/varios episodios, portada no https, id 0, título largo); `EsUrlValida` (válidas, http, otro dominio, sin `/api/webhooks/`).
- `AlmacenWebhook`: guardar → leer en carpeta temporal; el archivo no contiene la URL en claro; vacía borra.
- `WebhookDiscordService` con `HttpMessageHandler` falso: un mensaje con ajuste encendido → POST a la URL con el JSON; ajuste apagado o sin URL → nada; error HTTP → false sin lanzar.
- `PlaybackStateService`: último episodio de serie FINISHED → un `SerieTerminadaMensaje`; episodio intermedio, serie en emisión, marcado manual o re-visionado → ninguno.
- `EmisionMonitorService`: un ciclo con episodio nuevo publica `EpisodioEmitidoMensaje`.
- En vivo: el propietario con un webhook de un canal suyo (botón Probar y un evento real).

## Fuera de alcance

Telegram, descarga completada, archivos nuevos, cola persistente, varios webhooks.

---

# Plan de implementación

Ejecución en esta sesión, sin subagentes ni commits salvo orden. Cada tarea: prueba que falla → código → verde (`repo-build-test`).

## Restricciones globales
- Sin dependencias nuevas; HTTP por `IHttpClientFactory` con `ConCorteSinConexion`.
- La URL nunca en claro en disco ni en el registro. Las pruebas no escriben en `AppDataPaths` ni hacen peticiones reales.
- Textos con `T()`/`{loc:T}` en ES y EN; colores de la paleta en XAML.

## Focos de revisión
1. Re-ver el último episodio no repite "Terminaste" (prueba en tarea 3).
2. URL de otro dominio (posible fuga de datos a un tercero) se rechaza (tarea 1).
3. Mensaje recibido en hilo de fondo: el servicio no toca la interfaz (revisión).
4. Sin red: no lanza ni bloquea (tarea 2).
5. Títulos largos o con caracteres especiales en el JSON (serializador, tarea 1).

## Tarea 1: piezas puras y almacén
`EmbedWebhook`, `AlmacenWebhook`, mensajes, claves de texto; pruebas `WebhookTests.cs`.

## Tarea 2: servicio
`WebhookDiscordService` + interfaz + cliente `"Webhook"` + arranque; pruebas con handler falso.

## Tarea 3: enganches
Publicación de los dos mensajes; pruebas en `PlaybackStateServiceTests` y `EmisionMonitorServiceTests`.

## Tarea 4: Ajustes
`AppSettings`, `ConfiguracionViewModel` (URL, Probar, interruptores, guardar), vista y textos; pruebas del ViewModel.

## Tarea 5: cierre
Suite completa; revisión propia; prueba en vivo por el propietario.
