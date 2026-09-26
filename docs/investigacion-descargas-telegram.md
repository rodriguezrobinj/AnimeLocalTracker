# Investigación: descargas desde canales y grupos de Telegram

> **Estado: en pausa (no implementado).** Análisis de viabilidad hecho el 2026-09-25. Se documenta para retomarlo
> más adelante. No hay código ni cambios en la app relacionados con esta idea.

## Pregunta

¿Se puede hacer "scraping" de grupos y canales de Telegram que guardan anime para usarlos como una fuente más de
descarga (junto a AnimeAV1 y Nyaa/torrent)?

## Respuesta corta

**Sí, es posible**, pero no como scraping web: hay que conectarse a Telegram con una **cuenta de usuario** (protocolo
MTProto). Encaja bien en la arquitectura actual (daemon Python + `PythonBridgeService`).

## Vías técnicas evaluadas

| Vía | Qué permite | Límite |
|---|---|---|
| Vista web pública (`t.me/s/canal`) | Leer publicaciones de canales públicos sin sesión | Solo texto y enlaces; **no descarga archivos**. Muchos canales la tienen desactivada. |
| API de bots | — | Un bot solo ve un canal si es administrador: no sirve para leer canales ajenos. |
| **Cuenta de usuario (MTProto)**: Telethon, Pyrogram o TDLib | Buscar en el historial de canales/grupos donde el usuario ya está y descargar los videos | Es la única vía viable. Requiere iniciar sesión con la cuenta. |

## Cómo encajaría en la app

- **Dónde:** `tools/python` (daemon empaquetado). Telethon/Pyrogram entraría como una fuente nueva de resolución y
  descarga, otro eslabón de la cadena de proveedores (`OrquestadorMultiProveedor`).
- **Login:** en Configuración → teléfono, código y, si existe, verificación en dos pasos. La sesión se guarda en
  `%LocalAppData%\AnimeLocalTrackerData` (regla de `persistence.md`), nunca en el directorio de instalación.
- **Credenciales:** cada usuario registra su propio `api_id` / `api_hash` (gratis en my.telegram.org). **Nunca** van
  en el repositorio ni en el instalador (un `api_id` compartido arrastra a todos si alguien abusa).
- **Búsqueda:** por título y número de episodio sobre los nombres de archivo de los canales que el usuario elija.
  El parser del daemon (`parsers/anime_parser.py`) cubre parte del trabajo.
- **Selección manual:** los nombres son muy irregulares (`[Grupo] Anime - 05 [1080p].mkv`, `E05`, "Episodio 5"), así
  que convendría un selector como el de torrents (chips de grupo y resolución).

## Riesgo de bloqueo de la cuenta

No se puede dar un porcentaje: Telegram no publica sus criterios y los cambia sin avisar. Lo siguiente sale de su
documentación y de la experiencia de la comunidad (**verificar los Términos de la API vigentes antes de implementar**).
Con solo lectura y ritmo moderado el riesgo es **bajo**.

| Comportamiento | Riesgo |
|---|---|
| Leer historial de canales donde ya está el usuario y descargar (1-2 a la vez) | Bajo |
| Búsquedas ocasionales por episodio | Bajo |
| Búsqueda global en muchos canales en ráfaga | Medio (limitación temporal, `FloodWait`) |
| Descargas en paralelo masivas o ignorar las esperas que pide Telegram | Medio-alto |
| **Unirse a muchos canales desde la app** | **Alto** |
| Pedir el código de acceso repetidamente (reintentos de login) | Medio (bloqueo temporal del login) |
| Enviar mensajes, invitar gente, extraer listas de miembros | Muy alto (la app **no** lo haría) |

Consecuencias posibles, de menos a más graves: limitación temporal → cierre de sesiones/aviso de nuevo login →
restricciones (no poder unirse a canales) → **bloqueo definitivo del número** (poco común con solo lectura, pero
irreversible en la práctica).

### Mitigaciones de diseño (obligatorias si se implementa)

1. **Solo lectura:** la app nunca se une a canales ni envía nada; el usuario entra a los canales desde Telegram.
2. **Descargas en serie** (máx. 1-2) y respeto automático de `FloodWait`.
3. **Sesión persistente** (un solo login) y caché de búsquedas por episodio.
4. Aviso claro sobre riesgos en la pantalla de login.
5. `api_id`/`api_hash` propios de cada usuario.

### Recomendación

**No usar la cuenta principal.** Usar una cuenta secundaria con otro número (SIM prepago/eSIM) que solo esté en los
canales de anime: si algún día la bloquean, se pierde una cuenta desechable y no los chats/contactos reales.

## Otras consideraciones

- **Límites de Telegram:** archivos de hasta 2 GB (4 GB con Premium); límites de velocidad por cuenta.
- **Copyright:** muchos canales de anime redistribuyen contenido con derechos; zona comparable a Nyaa. La
  responsabilidad recae en quien lo use.
- **Tamaño del trabajo:** grande. Fases estimadas: (1) login y sesión, (2) búsqueda en canales, (3) descarga con
  reanudación + selector manual, (4) pulido en Configuración y pruebas.

## Cómo retomarlo (siguiente paso propuesto)

Prototipo aislado en el daemon Python: iniciar sesión, buscar un episodio en un canal ya conocido y descargarlo, **sin
tocar la UI**. Sirve para comprobar si el emparejamiento de nombres de archivo es viable antes de invertir en el resto.

## Decisiones abiertas

- Telethon vs Pyrogram vs TDLib (tamaño del binario empaquetado con PyInstaller, mantenimiento).
- ¿Permitir varias cuentas/sesiones? (recomendado: no en la primera versión).
- Dónde mostrar el aviso de riesgos y cómo pedir el `api_id` sin fricción.
