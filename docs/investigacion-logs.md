# Investigación: sistema de registros (logs) y diagnóstico (2026-09-30)

Objetivo: que el registro sea ordenado, corto de leer y barato de generar, sin perder la información necesaria para diagnosticar fallos.

## Estado anterior (medido)

- **Un solo `app.log`** para todo: 3.614 líneas / 473 KB con **118 sesiones de 3 días mezcladas**. Rotación a 5 MB con una sola copia.
- **Sin filtro de nivel**: el 57 % de las líneas eran DEBUG (2.067). El reproductor solo: 161 KB de trazas de rendimiento.
- **Sin control de repetidos**: sin internet, AnimeThemes escribió 239 líneas casi iguales ("Reintento # en # s", "Host desconocido") en una tarde. (Ese caso concreto ya lo cortó el arreglo offline del 29/09, pero nada evitaba el siguiente.)
- **Coste por llamada: 7–9 µs y 529 bytes**, sobre todo por pedir a Windows las rutas del perfil (para ocultarlas) en CADA mensaje. Se registra desde el hilo de la interfaz (reproductor, descargas).
- Horas sin milisegundos (imposible medir tiempos) y trazas de excepción sin sangrar.

## Fallos encontrados

1. **El error que tumba la app se perdía.** Los mensajes se escriben cada 500 ms; ante un error fatal el proceso muere antes (y en ese caso `ProcessExit` no llega): justo la entrada más importante nunca llegaba al disco.
2. **Cierre con dos escritores a la vez.** Al cerrar, el vaciado de `ProcessExit` y el consumidor en segundo plano podían escribir a la vez (archivo ocupado → entradas perdidas), y el lote que el consumidor tenía en memoria se perdía.
3. **Daemon Python: salida de errores redirigida y nunca leída.** Además de perder sus avisos y trazas, cuando se llenaba el búfer (avisos de numpy/librosa/yt-dlp) el daemon se quedaba **colgado** al escribir.
4. **Modo "one-shot" de Python con bloqueo clásico**: leía toda la salida normal y después la de errores; si Python escribía mucho por la de errores, ambos procesos se esperaban hasta que vencía el tiempo del comando.
5. Tres mensajes del buscador de AnimeAv1 usaban `Debug.WriteLine` (invisibles en la app instalada).

## Qué se cambió

- **Estructura** (`Logs/`):
  - `sesiones/AAAA-MM-DD_HH-mm-ss.log`: un archivo por cada vez que se abre la app, con cabecera (versión de Windows, .NET, modo de registro). Se conservan las 30 últimas y como mucho 25 MB entre todas.
  - `errores.log`: solo avisos y errores de todas las sesiones, con fecha completa (tope 2 MB + una copia).
  - El `app.log` antiguo se aparta como `sesiones/0000-anteriores (app.log antiguo).log`.
- **Formato**: `00:09:00.123 INFO  [Fuente] mensaje` (milisegundos, nivel alineado; la fecha va en el nombre del archivo). Trazas sangradas.
- **Registro detallado** (Configuración → General → Registro de diagnóstico; o `ANIMELOCALTRACKER_LOG_DETALLADO=1`): apagado por defecto. Apagado, las entradas DEBUG no se escriben… salvo como **contexto**: se guardan las últimas 40 (hasta 2 min) y se escriben, marcadas `DEBUG ·`, justo antes del siguiente aviso o error. Los fallos llegan con lo que los precedió sin llenar el disco en uso normal.
- **Repetidos**: mismo mensaje salvo números, más de 5 veces por minuto → una línea "(repetido N veces más en el último minuto: …)".
- **Rendimiento**: rutas del perfil leídas una sola vez; cola acotada (50.000) con aviso de entradas descartadas.
- **Fiabilidad**: `AppLogger.Flush()` síncrono, llamado ante errores fatales, en el cierre y al abrir la carpeta de registros; un solo escritor (candado) y las entradas quedan en la cola hasta escribirse.
- **Python**: la salida de errores del daemon se lee continuamente y va al registro (`[PythonDaemon]`); el one-shot lee ambas salidas a la vez.
- **Configuración**: interruptor "Registro detallado" y botón "Abrir carpeta" (ES/EN).

## Resultado medido

| | Antes | Después |
|---|---|---|
| Coste de un DEBUG (registro detallado apagado) | 7–9 µs, 529 B | **0,8 µs**, 303 B |
| Coste de un mensaje escrito | 7–9 µs | **3–4 µs** |
| Coste de un mensaje repetido | 7–9 µs | **0,7–1,2 µs** |
| Arranque normal de la app (líneas) | ~30 en un archivo compartido | **5** en su propio archivo |

(En la primera versión, 100.000 mensajes distintos costaban 1,4 ms cada uno: la tabla de repetidos se recorría entera en cada mensaje sin poder vaciarse. Detectado con la medición y corregido antes de terminar.)

Verificado en la app real: migración del `app.log` antiguo, archivo de sesión, interruptor (se guarda y se aplica al momento; `settings.json` solo gana la clave nueva). Tests nuevos del logger (sesión, errores.log, contexto, repetidos, formato, limpieza, rutas). La captura de pantalla no fue posible (el terminal queda siempre encima y `PrintWindow` sale negro); la sección de Configuración se verificó por UIA (elementos, posiciones y comportamiento).

## Visor de registros dentro de la app (2026-09-30)

Configuración → General → Registro de diagnóstico → **Ver registros** (sin botón propio en la barra lateral: Configuración sigue marcado mientras está abierto).

- **Selector de archivo**: sesión actual (con indicador EN VIVO: se añade sola lo nuevo cada 3 s, leyendo solo los bytes añadidos), sesiones anteriores ("Hoy 00:41 · 1 KB"), errores.log y el registro antiguo.
- **Filtros**: chips por nivel con contador (errores, avisos, información, depuración), "Solo avisos y errores", fuente (las que más escriben primero), búsqueda de texto (también dentro de las trazas, con espera de 250 ms mientras se escribe). Lo más reciente arriba.
- **Filas**: hora (con milisegundos en sesiones, con fecha en errores.log/antiguo), nivel en color, fuente, mensaje; "Detalle técnico (N)" despliega la traza (seleccionable); las DEBUG de contexto van más tenues; botón para copiar la entrada.
- **Acciones**: Copiar lo visible (orden cronológico, hasta 3.000 entradas, con traza) para pegar en un reporte; Actualizar; Abrir carpeta.
- Lectura (`Services/LectorRegistros`) y filtrado fuera del hilo de la interfaz; lista virtualizada. Registro antiguo real (3.415 entradas, 472 KB): **~60 ms** en total.
- Verificado en la app real por UIA (abrir desde Configuración, selector, contadores, "Solo avisos y errores", volver) y 12 tests. Arreglados al probar: "6 de 5 entradas" (contaba el separador de sesión) y el nombre accesible de las opciones del selector.

## Pendiente / ideas

- Varios mensajes INFO de arranque (Python detectado, SMTC, backup) podrían resumirse en la cabecera de la sesión.
- Las trazas de rendimiento del reproductor (`[Arranque]`, `[Cambio]`: 5–6 líneas por episodio) podrían unirse en una sola línea por apertura.
