# Mapa de calor de visionado (Estadísticas)

Diseño aprobado el 2026-10-16. Plan de implementación: `docs/plan-mapa-calor-visionado.md`.

## Objetivo

En la pestaña Estadísticas hay totales, donas y barras, pero nada que muestre la **constancia**: qué semanas fueron
activas, dónde hubo huecos, cuánto duran las rachas. Se añade una cuadrícula de días al estilo de GitHub, cuyo color
indica cuántos episodios se vieron cada día.

Criterio de éxito: al abrir Estadísticas, de un vistazo se distinguen los días de maratón, los días sueltos y los
huecos de los últimos 12 meses, y el número exacto de cada día está en su tooltip.

## Decisiones tomadas

| Tema | Decisión | Descartado |
|---|---|---|
| Fuente de datos | `RegistroEpisodio.UltimaReproduccion` tal cual, sin tocar el esquema | Tabla nueva de sesiones (empezaría vacía), tabla nueva rellenada con lo existente (migración y dos fuentes para la racha) |
| Período | Últimas 53 semanas, terminando hoy | Año natural con selector; ambos |
| Interacción | Solo tooltip | Tooltip con la lista de animes; clic que abre Historial en ese día |
| Dibujo | `ItemsControl` de `Border` en un `UniformGrid` 7×53 | Control propio con `OnRender` (tooltips por posición, invisible al lector de pantalla); rectángulos creados en código como `DonutChart` |

### Limitación conocida de los datos

`RegistroEpisodio` tiene una fila por episodio y `UltimaReproduccion` guarda solo la **última** reproducción. Por eso:

- Un repaso mueve el episodio al día del repaso: el día original queda vacío.
- Los episodios marcados como vistos a mano no tienen fecha (`persistence.md` #5) y no aparecen.

Es el mismo criterio que ya usan la racha y la actividad de 7 días. Si algún día hace falta exactitud con repasos,
habrá que crear una tabla de sesiones (opción descartada arriba).

## Qué cuenta

Un episodio cuenta en un día si `VistoLocal` y `UltimaReproduccion` no es nula, agrupado por el **día local** de esa
fecha (`MotorLogros.ALocal`: Unspecified = UTC, `persistence.md` #4). Los episodios a medio ver no cuentan.

### Arreglo incluido: día local en el resto de Estadísticas

Hoy `EstadisticasViewModel.CalcularEstadisticas` agrupa por `UltimaReproduccion.Date` **en UTC**. Si ves un episodio
a las 21:00 en Colombia (UTC−5), cuenta para el día siguiente. Pasan a día local, con la misma lista de días que usa
el mapa: actividad de 7 días, promedio diario, racha actual y máxima, "Por año", "Año más activo" y "Horas por mes".
Así el mapa, la racha y las barras cuadran entre sí (Logros ya usaba el día local).

## Aspecto

Tarjeta a todo el ancho, justo después de la fila de indicadores (Género favorito, Anime más visto, Año más activo,
Ritmo), con los mismos fondo, borde, radio y relleno que las demás tarjetas de la pestaña.

```
 Constancia                                         Menos ▢ ▤ ▥ ▦ ■ Más
 214 episodios en los últimos 12 meses · 96 días con actividad
        nov    dic    ene    feb    mar    abr  …  sep    oct
 lun  ▢ ▤ ▢ ▢ ■ ▥ ▢ ▢ ▤ ▢ ▢ ▢ ▦ ▤ ▢ ▢ …
 mar  …
 mié  ▢ ▢ ▥ ▢ ▢ ▢ ▤ ▢ ▢ ▢ ▥ ▢ ▢ ▢ ▢ ▢ …
 jue  …
 vie  ▤ ▢ ▢ ▢ ▦ ▢ ▢ ▢ ▢ ▤ ▢ ▢ ▢ ■ ▢ ▢ …
 sáb  …
 dom  …
```

- **Columnas = semanas, filas = días.** La semana empieza en
  `LocalizationService.Cultura.DateTimeFormat.FirstDayOfWeek` (lunes en `es-ES`, domingo en `en-US`). Etiquetas de
  día solo en las filas de lunes, miércoles y viernes (como GitHub), abreviadas con la cultura.
- **Rango:** la última columna es la semana de hoy; la primera empieza en el primer día de semana anterior o igual a
  `hoy − 52 semanas`. Siempre 53 columnas. Los días posteriores a hoy no se pintan (transparentes).
- **Etiquetas de mes:** en la columna cuya semana contiene el día 1 de ese mes (si ese día no es futuro), con el
  nombre abreviado de la cultura. La primera columna lleva etiqueta solo si en ella empieza un mes.
- **Escala:** la cuadrícula y sus etiquetas van dentro de un `Viewbox` (`Stretch=Uniform`) que ocupa el ancho de la
  tarjeta. Con la ventana mínima (800 px) se encoge sin cortarse.
- **Color:** `Brush.Success` (el verde de las barras de "Actividad 7 días") con opacidad por nivel; los días sin
  actividad, `Brush.SurfaceChip`. Sin colores nuevos (`ui-wpf-vistas.md` #4).

| Nivel | Episodios | Relleno |
|---|---|---|
| 0 | 0 | `Brush.SurfaceChip` |
| 1 | 1 | `Brush.Success`, opacidad 0,30 |
| 2 | 2–3 | `Brush.Success`, opacidad 0,55 |
| 3 | 4–6 | `Brush.Success`, opacidad 0,80 |
| 4 | 7 o más | `Brush.Success`, opacidad 1 |

Niveles fijos y no relativos al máximo (GitHub usa cuartiles): "7 o más" significa siempre una maratón de unas 3 h,
aunque un día excepcional tenga 30 episodios.

- **Tooltip** y nombre accesible de cada celda (el lector de pantalla lo toma de `DiaCalor.ToString()`, que devuelve el
  mismo texto): "3 episodios · jueves, 12 de marzo de 2026" (fecha larga
  con `LocalizationService.Cultura`); "1 episodio · …" en singular y "Sin episodios · …" para los días vacíos.
- **Leyenda:** "Menos" + las cinco muestras + "Más", arriba a la derecha.
- **Sin actividad en el rango:** la cuadrícula vacía y, debajo del título, "Aún no hay reproducciones en los últimos
  12 meses" en lugar del resumen.

## Piezas

### `Services/CalculadorMapaCalor.cs` (nuevo)

Estático y puro, como `Services/Franquicias/CalculadorTop.cs`: ni base de datos ni WPF.

```csharp
public static MapaCalor Calcular(IEnumerable<DateTime> diasLocales, DateTime hoy, CultureInfo cultura);
```

- `diasLocales`: un elemento por episodio contado, ya en día local (solo se usa `.Date`).
- `hoy`: día local actual (inyectado para las pruebas).
- `cultura`: da el primer día de la semana, los nombres de mes y día y el formato del tooltip.

Devuelve `MapaCalor`:

- `Celdas`: 371 `DiaCalor` **fila a fila** (las 53 semanas del primer día de la semana, luego las del segundo…), el
  orden en que las coloca un `UniformGrid Rows=7 Columns=53`.
- `Meses`: 53 textos, vacíos salvo donde empieza un mes.
- `Dias`: 7 textos, vacíos salvo en las filas de lunes, miércoles y viernes.
- `TotalEpisodios` y `DiasActivos` dentro del rango.

`DiaCalor(DateTime Fecha, int Episodios, int Nivel, string Texto)`; `Nivel` vale −1 para los días fuera del rango
(futuros), y en ese caso `Texto` queda vacío y la celda no muestra tooltip.

Los textos del tooltip usan `LocalizationService.T` con las claves de abajo.

### `EstadisticasViewModel`

- En `CalcularEstadisticas` (ya corre en segundo plano), una sola lista de días locales:
  `vistos.Where(fecha).Select(r => MotorLogros.ALocal(r.UltimaReproduccion!.Value).Date)`. La usan el mapa y todas las
  cifras del arreglo anterior.
- `[ObservableProperty] MapaCalor? _mapaCalor` y `string _mapaCalorResumen` (en singular o plural según los números). Si al
  recargar las celdas son iguales, se conserva el mismo `MapaCalor` para que la vista no rehaga sus elementos.
- `internal TimeProvider Reloj`: da "hoy" y la zona horaria del día local; las pruebas lo fijan (el CI va en UTC).
- Las fechas posteriores a hoy (reloj adelantado) no cuentan en ninguna cifra por día.
- Al cambiar de idioma ya se fuerza el recálculo en la siguiente visita (`Receive(IdiomaCambiadoMensaje)`).

### `EstadisticasView.xaml`

Tarjeta nueva tras la fila 2 (indicadores). `ItemsControl` de celdas con `ItemsPanel` = `UniformGrid Rows=7
Columns=53`. Celda de 11×11 con margen de 1,5 y radio 2. El relleno sale de un estilo con `DataTrigger` sobre `Nivel`.
Las etiquetas de mes van en otro `ItemsControl` con `UniformGrid Rows=1 Columns=53`, sin recortar el texto. No es una
lista desplazable (371 celdas fijas), así que no aplica la regla de `ListBox` virtualizado de `ui-wpf-vistas.md` #2.

### Textos (`LocalizationService`, ES y EN)

`Stats_Constancia`, `Stats_ConstanciaSubFormato` ("{0} en los últimos 12 meses · {1}", con `Stats_ConstanciaEpisodioUno`/
`Stats_ConstanciaEpisodiosFormato` y `Stats_ConstanciaDiaUno`/`Stats_ConstanciaDiasFormato`), `Stats_ConstanciaVacia`,
`Stats_Menos`, `Stats_Mas` y tres formatos de tooltip con la fecha larga en `{1}` (o `{0}` cuando no hay número):
`Stats_MapaDiaUnoFormato` ("1 episodio · {0}"), `Stats_MapaDiaVariosFormato` ("{0} episodios · {1}") y
`Stats_MapaDiaNingunoFormato` ("Sin episodios · {0}"). Nombres de meses y días desde la cultura, sin claves.

## Pruebas

- `CalculadorMapaCalorTests`:
  - Límites de nivel: 1→1, 2→2, 3→2, 4→3, 6→3, 7→4.
  - Semana que empieza en lunes (`es-ES`) y en domingo (`en-US`): la celda de hoy cae en la fila de su día y en la
    última columna.
  - Los días posteriores a hoy tienen nivel −1; no hay más de 53 columnas ni menos.
  - Orden fila a fila: la celda `fila * 53 + columna` es el día `inicio + columna * 7 + fila`.
  - Fechas fuera del rango (anteriores al inicio) no cuentan en los totales.
  - Etiquetas de mes en la columna correcta.
- `EstadisticasViewModelTests`: una reproducción guardada a las 02:00 UTC cuenta en su día local (calculado con
  `ToLocalTime()` en la propia prueba) en el mapa, en la actividad de 7 días y en la racha.
- `EstadisticasViewTests`: la vista se sigue construyendo con la tarjeta nueva.

## Verificación

Compilar y correr las pruebas de lo tocado mientras se trabaja; la suite completa una vez al final
(`repo-build-test`). Luego, la app con `perfil-aislado` (copia de la biblioteca real, con historial) y capturas de la
tarjeta con la ventana normal y al mínimo (`wpf-visual-verification`).

## Fuera de alcance

Clic en un día, selector de años y tabla de sesiones de reproducción.
