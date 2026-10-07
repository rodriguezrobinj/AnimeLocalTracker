# Diseño: descarga de temporada completa y "Eliminar tras ver" (2026-10-06)

Propuestas #1 y #3 de `docs/propuestas-nuevas-funciones.md`. Son dos funciones independientes: se implementan por separado (primero la 1, después la 3), cada una con su plan. Este documento recoge lo decidido con el usuario y el diseño acordado. Sin commit hasta que el usuario lo pida.

## 1. Lo que ya existe (no se duplica)

- **Descargar faltantes** (`EpisodiosFichaViewModel.DescargarFaltantesAsync`): solo rellena huecos entre los episodios de la carpeta (`EpisodiosOrganizador.CalcularFaltantes`). Con la carpeta vacía no hace nada. Confirma desde `UmbralConfirmarDescargaFaltantes = 3` episodios y encola con `DescargarEpisodioAsync`.
- **Liberar espacio** (`DetalleViewModel.LiberarEspacioAsync`) y **EliminarEpisodio** (`EpisodiosFichaViewModel`): borran el video, la miniatura, conservan el registro (`ConservarRegistroTrasEliminarArchivoAsync`), llaman a `episodio.QuitarArchivo()` y envían `ArchivoEpisodioEliminadoMensaje`. La lógica está **duplicada** en los dos sitios.
- **Cola de descargas** con `DescargasSimultaneas = 3`; **descarga automática** de episodios nuevos (`EmisionMonitorService` + `PreferenciaEmision.AutoDescargar`).
- **`IEmisionMonitorService.UltimoEmitido(proxima, anime, ahoraUtc)`** (público; ya lo usa `DetalleViewModel`): serie terminada → `TotalEpisodios`; en emisión → último episodio ya emitido.
- **Visto por reproducción real**: `ReproductorViewModel.RealizarAutoTrackingAsync` (umbral `AppSettings.UmbralMarcadoVisto`, 90 %).
- **Salida del reproductor**: `SalirDelReproductor` (única salida) e `IrAEpisodio` (cambio de episodio).

## 2. Decisiones del usuario

1. "Eliminar tras ver" ofrece estos modos, **uno activo a la vez**: Apagado (predeterminado), Automático al terminar de ver, Al completar la serie, Consumo ligero (conservar los últimos N). Se descarta "Preguntar tras cada episodio".
2. El borrado es **definitivo** (como "Liberar espacio" hoy), no a la Papelera.
3. "Temporada completa" encola **solo los episodios ya emitidos**; los siguientes los trae la descarga automática que ya existe.

## 3. Parte 1: descarga de temporada completa

**Enfoque:** ampliar lo que ya hace "Descargar faltantes", sin servicios nuevos.

- **Función pura** en `Core` (junto a `EpisodiosOrganizador`): `pendientes = {1..ultimoEmitido} − (descargados ∪ en descarga ∪ vistos)`. Los vistos se excluyen igual que en `CalcularFaltantes`: ver y liberar el archivo es lo normal, y con "Eliminar tras ver" activo el botón reofrecería lo recién borrado. Si `ultimoEmitido <= 0` (total desconocido, series muy largas) no hay pendientes y el botón no aparece.
- **Comando** `DescargarTemporadaCommand` en `EpisodiosFichaViewModel`: calcula los pendientes con `UltimoEmitido`, confirma (siempre por encima de 3 episodios) y encola en orden ascendente reutilizando `DescargarEpisodioAsync`, que respeta las 3 descargas simultáneas.
- **Un solo botón que se adapta**: carpeta vacía → "Descargar temporada completa (N)"; con algunos episodios → "Descargar los que faltan (N)". Va en una fila sobre la lista de episodios, junto a los avisos, y solo aparece cuando hay pendientes. (La idea inicial era el estado vacío de la ficha, pero la lista ya dibuja las filas 1..N aunque no haya archivos, así que ese estado casi nunca se muestra.) El aviso de huecos y su botón actual no cambian.
- **Estimación de espacio** en el diálogo: tamaño medio de los episodios ya descargados × pendientes (si no hay ninguno, solo la cantidad). Si no cabe en el disco, el diálogo lo advierte, pero **no bloquea**.
- **Sin carpeta asignada**: reutiliza el título `Det_AutoDescargaSinCarpetaTitulo` con un mensaje propio (el existente habla de "volver a activarlo" y no encaja). Hoy el botón de descarga suelta no valida la carpeta; el nuevo sí.
- **Errores**: igual que hoy, los fallos de un episodio aparecen en la pestaña Descargas.
- **Interfaz** (reglas de `ui-wpf-vistas.md`): colores de `Brush.*`, `ToolTip` + `AutomationProperties.Name` si el botón lleva solo icono, textos con `{loc:T …}` y claves nuevas en ES y EN.

## 4. Parte 2: Eliminar tras ver

**Enfoque:** un servicio pequeño, `LimpiadorDeEpisodios` (con interfaz), que concentra el borrado. El borrado manual (`LiberarEspacioAsync`, `EliminarEpisodio`) pasa a usar el mismo camino, así que se elimina la duplicación. Se descarta un barrido periódico en segundo plano (podría borrar algo mientras se reproduce).

**Ajustes** (`AppSettings`, archivo de ajustes; sin migración de base de datos): `ModoEliminarTrasVer` (Apagado / Automatico / AlCompletarSerie / ConsumoLigero) y `EpisodiosAConservar` (N, predeterminado 3). Selector en Configuración; al elegir un modo que borra, aviso de que es definitivo. Los `ComboBox` con textos se recargan con `IdiomaCambiadoMensaje`.

| Modo | Cuándo actúa | Qué borra |
|---|---|---|
| Automático | Al terminar de ver el episodio | Ese episodio |
| Consumo ligero (N) | Igual | Los vistos con archivo, salvo los N de número más alto. Nunca toca los no vistos. |
| Al completar la serie | Al ver el último episodio | Pregunta una sola vez ("¿Liberar X GB de esta serie?") y borra los vistos con archivo si se acepta |

Ejemplo con N = 3: vistos del 1 al 8 → al cerrar el 8 se borran del 1 al 5 y quedan el 6, 7 y 8.

**Disparo:** al terminar de ver, no al cruzar el umbral (el archivo está abierto). Dos enganches de una línea en `ReproductorViewModel` que delegan en el servicio:
- `Dispose()`: el reproductor es idempotente y se libera también al navegar a otra pestaña, no solo al pulsar salir (la idea inicial de enganchar `SalirDelReproductor` se descartó por eso).
- `AsignarMetadatosDeEpisodio`: todo cambio de episodio pasa por ahí (autoplay, siguiente, anterior, el cajón).

Solo se dispara si el episodio quedó marcado como visto **reproduciéndolo** en esa sesión (`_fueMarcadoComoVisto`). Un marcado manual no borra nada. Además, el servicio **comprueba en la base de datos** que el episodio figure como visto (esperando hasta 10 s, porque el guardado local es asíncrono) y, si no llega, no borra. Empieza con una pausa de 1 s y el borrado reintenta 15 × 200 ms (`BorradoDeArchivos`) para dar tiempo al reproductor a soltar el archivo.

**Protecciones (todos los modos):**
- Solo archivos dentro de `RutaCarpeta` del anime.
- Nunca el episodio abierto ni uno sin ver; si el archivo está en uso tras los reintentos, se deja.
- Conserva el registro (visto, progreso, favorito, ficha, puntuaciones), borra la miniatura y envía `ArchivoEpisodioEliminadoMensaje` (regla de persistencia: el historial es permanente).
- Una línea en el registro por cada archivo borrado y un aviso con lo liberado.
- Las pruebas nunca escriben bajo `AppDataPaths`: carpetas temporales.

## 5. Fuera de alcance

"Preguntar tras cada episodio", deshacer y Papelera. Las excepciones por anime se resolvieron con el interruptor "Conservar los videos" de la ficha (`docs/plan-conservar-videos.md`): con él activo ningún modo borra ni pregunta por ese anime. Sin el interruptor, "Al completar la serie" no recuerda un "Cancelar": volverá a preguntar la próxima vez que se cierre el último episodio.

## 6. Pruebas

- **Parte 1**: función pura (terminada, en emisión, con huecos, total desconocido, ya en descarga) y comando (encola en orden; si se cancela el diálogo, no encola nada).
- **Parte 2**: el servicio con carpetas temporales (cada modo, cálculo de N, archivo fuera de la carpeta, episodio sin ver, archivo en uso, registro conservado) y guardado y carga del ajuste en `ConfiguracionViewModel`.
- **En la app real**: solo con un perfil aislado y un video de prueba copiado. Nunca con series reales ni cruzando el 85 % de un episodio real (ver memoria "Pruebas en la app real").
- Verificación según `repo-build-test`: pruebas afectadas mientras se trabaja y suite completa una vez al cerrar cada parte.

## 7. Puntos abiertos para el plan

- **Resuelto:** el selector va en Configuración → Descargas, con el aviso de borrado definitivo al elegir un modo que borra; "consumo ligero" admite de 1 a 10 episodios.
- **Resuelto:** con `TotalEpisodios` desconocido, "Al completar la serie" no actúa.
- **Sin resolver por lectura de código:** si Flyleaf suelta el archivo a tiempo al cerrar. Se comprueba en la app real con un perfil aislado y un video de prueba (plan de implementación, Tarea 5).
- Plan de implementación: `docs/plan-eliminar-tras-ver.md`.
