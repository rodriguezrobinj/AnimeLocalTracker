# Plan de implementación: motor libmpv, etapa 3 (paridad con el reproductor clásico)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que, con `"MotorVideo": "Mpv"`, el reproductor haga lo mismo que con Flyleaf en todo lo que hoy falta: subtítulos dibujados por libmpv (con su estilo original o con el de Configuración), ecualizador y modo noche, captura de fotograma, mini reproductor, y el nombre de la tarjeta gráfica en el informe de video. Sin ese ajuste, nada cambia.

**Arquitectura:** el contrato `IMotorVideo` gana tres miembros (si el motor dibuja los subtítulos de texto, aplicar el estilo de subtítulos y recordar la cadena de audio para el siguiente archivo); el reproductor decide por esa capacidad, sin preguntar qué motor es. `MotorMpv` traduce cada función a propiedades y órdenes de libmpv. La superficie de video pasa a ser **una por motor** y se mueve de la vista de la ventana principal a la del mini reproductor, en vez de crearse otra.

**Tecnologías:** C# .NET 8, WPF, libmpv 0.41 (`20261011-git-accb7b2821`), OpenTK.GLWpfControl 4.3.6, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md` (secciones 4.4, 5 y 7). Etapa anterior: `docs/plan-motor-libmpv-etapa-2.md`, con sus notas de ejecución.

## Lo que ya está comprobado (ensayo desechable del 2026-10-11, fuera del repo)

El diseño dejaba cuatro incógnitas para el principio de esta etapa. Se resolvieron con el arnés del prototipo (libmpv real, ventana WPF, Youjo Senki 02, Intel HD 620) antes de escribir el plan:

| Incógnita | Resultado |
|---|---|
| Subtítulos con el estilo del usuario | `sub-font`, `sub-font-size`, `sub-bold`, `sub-italic`, `sub-color`, `sub-outline-color`, `sub-outline-size`, `sub-back-color`, `sub-border-style=background-box` y `sub-ass-style-overrides=Underline=-1` funcionan y se ven en la imagen. **«Usar mi estilo» es `sub-ass-override=strip`** (texto plano con el estilo de Configuración); con `force` se quedan las cursivas y posiciones del subtítulo original. No existe equivalente al **borde de la caja**. |
| Captura | `screenshot-to-file <png> video\|subtitles` responde bien desde el hilo de la interfaz (no se bloquea). Con `screenshot-png-compression=0` tarda **35 ms** (500 ms con la compresión por defecto). Funciona también sin pantalla (`vo=null`), así que se puede probar. |
| Ecualizador en vivo | Un solo grafo: `af=@filtros:lavfi=[volume@eq_pre=…,equalizer@eq_b0=…,acompressor@modonoche=…]`. El cambio en vivo es `af-command filtros <clave> <valor> <nombre>@<id>` (p. ej. `equalizer@eq_b0`): devuelve error si el filtro no existe. Con el destino por defecto («todos») devuelve error aunque el cambio se aplique: **hay que nombrar el filtro**. |
| Mini reproductor | **Mover el mismo lienzo** de una ventana a otra mantiene 24 fotogramas/s y la pista de video activa, sin corte. Soltar el pintor y crear otro apaga el video y tarda **1 s** en volver. Un segundo lienzo tiene un contexto OpenGL distinto: no se usa. |
| Tarjeta gráfica | `GL_RENDERER`/`GL_VENDOR` dan «Intel(R) HD Graphics 620 / Intel». El lienzo usa la tarjeta que Windows asigna a la app: no se puede elegir otra desde aquí. |

## Diferencias con el reproductor clásico que esta etapa acepta

Para que el propietario las conozca antes de aprobar:

1. **Borde de la caja del subtítulo** («Grosor del borde» y su color en Configuración): libmpv no lo dibuja. La caja de fondo, sí.
2. **Tamaño de los subtítulos proporcional a la ventana.** Hoy es fijo en píxeles; con libmpv crece y encoge con la ventana (en el mini reproductor se ven pequeños pero proporcionados). Se calibra para que a pantalla completa en 1366×768 coincida con la vista previa de Configuración.
3. **La captura de fotograma incluye los subtítulos** que se estén viendo (hoy salen sin ellos, porque los dibuja una capa aparte).
4. **El ajuste «Tarjeta gráfica» no se aplica** con el motor nuevo. No se toca Configuración ahora: el ajuste del motor sigue oculto; se ocultará esa opción en la etapa 5, cuando el motor nuevo tenga su selector.
5. **No hay precarga del episodio siguiente**: el cambio de episodio ya tarda 0,4–0,5 s con libmpv.

## Restricciones globales

- **Sin commit ni push** hasta que el usuario lo ordene. Sin ramas, worktrees ni subagentes.
- **Con el ajuste por defecto (`"Flyleaf"`) no cambia nada visible.** `ReproductorView` (la vista clásica) no se modifica. Los cambios del reproductor se deciden por la capacidad del motor (`DibujaSubtitulosDeTexto`), nunca por `UsaMotorMpv` ni por un `as MotorFlyleaf`.
- **Frontera nativa:** `[LibraryImport]`, UTF-8 explícito, `LibMpv.Disponible` antes de cualquier llamada.
- **Números hacia libmpv siempre con `CultureInfo.InvariantCulture`** (la app corre en español: `44,5` rompería la opción).
- **Las pruebas nunca escriben bajo `AppDataPaths`**; lo temporal va a la carpeta temporal del sistema y se borra.
- **Sin textos visibles nuevos.** Si apareciera alguno, claves ES y EN (skill `localizacion`).
- **Verificación de C#:** skill `repo-build-test`. **En la app:** skill `perfil-aislado`; nunca más del 85 % de un episodio ni el último de un anime; **no se pulsa «Capturar fotograma»** (escribe en Imágenes y pisa el portapapeles): la captura se cubre con la prueba sin pantalla.
- Mensajes, comentarios y nombres en español.

## Foco de revisión

1. **Estilo con decimales y cultura española** (tamaño 44,5; grosor 2,5): las opciones salen con punto. → Tarea 2, `OpcionesDeEstilo_UsaPuntoDecimalAunqueLaCulturaSeaEspanola`.
2. **Cambiar una banda sin audio que cambiar** (ecualizador apagado, motor recién creado, id desconocido): devuelve `false` sin lanzar, y el reproductor remonta la cadena. → Tarea 4, `CambiarUnFiltroQueNoEstaMontado_DevuelveFalse`.
3. **Capturar sin archivo abierto o con el motor desechado:** `null`, sin dejar archivos temporales. → Tarea 5, `CapturarSinArchivo_DevuelveNullYNoDejaTemporales`.
4. **Pista de subtítulos que no es de este archivo** (se guardó del episodio anterior): no lanza y no cambia lo que se ve. → Tarea 2, `MostrarUnaPistaQueNoEsDeEsteArchivo_NoHaceNada`.
5. **Cerrar el episodio desde el mini reproductor** (el motor se desecha con la superficie alojada en otra ventana) y **volver del mini a la ventana principal** varias veces: sin excepción y sin dejar de pintar. → Tarea 8, pasos de la app (no hay lienzo OpenGL en las pruebas).

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `AnimeLocalTracker/Services/IMotorVideo.cs` | Modificar | Tres miembros nuevos del contrato |
| `AnimeLocalTracker/Services/MotorFlyleaf.cs` | Modificar | Implementarlos (dos no hacen nada; el tercero ya existía con otro nombre) |
| `AnimeLocalTracker.Tests/ViewModels/MotorDePrueba.cs` | Modificar | Registrar las llamadas nuevas |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.Subtitulos.cs`, `.Audio.cs`, `.cs` | Modificar | Subtítulos por capacidad del motor; sin `as MotorFlyleaf`; fuera las restricciones de la etapa 2 |
| `AnimeLocalTracker/ViewModels/NavigationService.cs` | Modificar | Navegar con libmpv vuelve a pasar al mini reproductor |
| `AnimeLocalTracker/Services/MotorMpv.cs` | Modificar | Subtítulos, filtros de audio, captura, tarjeta |
| `AnimeLocalTracker/Services/MotorVideo.cs` | Modificar | `FabricantePorNombre` |
| `AnimeLocalTracker/Controls/SuperficieMpv.cs` | Modificar | Una superficie por motor, que se mueve; lee el nombre de la tarjeta |
| `AnimeLocalTracker/Views/ReproductorMpvView.xaml` y `.xaml.cs` | Modificar | Fuera las capas de subtítulos de la app; hueco para la superficie |
| `AnimeLocalTracker.Tests/Services/MotorMpvTests.cs`, `ViewModels/ReproductorConMotorTests.cs`, `Views/…` | Modificar | Pruebas nuevas |

---

## Tarea 1: el contrato y el reproductor

**Archivos:** `IMotorVideo.cs`, `MotorFlyleaf.cs`, `MotorMpv.cs` (solo para que compile), `MotorDePrueba.cs`, `ReproductorViewModel.Subtitulos.cs`, `ReproductorViewModel.Audio.cs`, `ReproductorViewModel.cs`, `ReproductorConMotorTests.cs`.

**Interfaces — produce:**

```csharp
// IMotorVideo
/// <summary>El motor dibuja él mismo, dentro del video, también los subtítulos de texto. Si es false, los de texto los lee y los pinta la app.</summary>
bool DibujaSubtitulosDeTexto { get; }

/// <summary>Aspecto de los subtítulos que dibuja el motor. <paramref name="tambienEnAss"/>: usarlo también en las pistas ASS, en vez de su estilo original.</summary>
void AplicarEstiloSubtitulos(EstiloSubtitulos estilo, bool tambienEnAss);

/// <summary>Deja apuntada la cadena para el siguiente archivo sin remontar la que suena (después de un cambio en vivo).</summary>
void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros);
```

- `MotorFlyleaf`: `DibujaSubtitulosDeTexto => false`; `AplicarEstiloSubtitulos` no hace nada; `RecordarFiltrosParaElSiguienteArchivo` pasa a llamarse `RecordarFiltrosAudio` (mismo cuerpo).
- `MotorMpv` (provisional hasta las tareas 2 y 4): `DibujaSubtitulosDeTexto => false`, los otros dos vacíos.
- `MotorDePrueba`: `public bool DibujaSubtitulosDeTexto { get; set; }`, `public (EstiloSubtitulos Estilo, bool TambienEnAss)? EstiloPuesto { get; private set; }`, `public IReadOnlyList<FiltroAudio>? FiltrosRecordados { get; private set; }`.

- [ ] **Paso 1: pruebas que fallan** (en `ReproductorConMotorTests`, con `motor.DibujaSubtitulosDeTexto = true` y una pista `new PistaSubtitulosMotor(3, "es", null, EsImagen: false, EsAss: true, RutaExterna: null)`):
  - `ConMotorQueDibujaLosSubtitulos_UnaPistaDeTextoSeLeDaAlMotorYLaAppNoLaExtrae`: tras `SelectSubtitleStream(pista)`, `motor.SubtitulosDelMotor` es la pista, el extractor de la app no se llamó (`Times.Never`), `PistaActualEsAss` es true y `SubtitulosAssActivo` false.
  - `ConMotorQueDibujaLosSubtitulos_UsarMiEstiloSeLoPideAlMotor`: `UsarMiEstiloEnAss = true` → `motor.EstiloPuesto!.Value.TambienEnAss` true; al volver a false, false.
  - `ConMotorQueDibujaLosSubtitulos_ApagarYEncenderVuelveAMostrarLaMismaPista`: `DeshabilitarSubtitulos()` → `VecesSubtitulosApagados == 1`; `HabilitarSubtitulos()` → `SubtitulosDelMotor` vuelve a ser la pista.
  - `AlCrearElMotor_SeLeDaElEstiloDeSubtitulosDeConfiguracion`: con un estilo de tamaño 60 en los ajustes, tras abrir, `motor.EstiloPuesto!.Value.Estilo.Tamano == 60`.
  - `CambiarUnaBandaEnVivo_RecuerdaLaCadenaParaElSiguienteArchivo`: tras mover una banda, `motor.FiltrosRecordados` contiene la ganancia nueva.
  - Las pruebas existentes con el motor clásico (`DibujaSubtitulosDeTexto = false`) siguen pasando sin tocarlas.
- [ ] **Paso 2:** correrlas y verlas fallar (no compilan o fallan por comportamiento).
- [ ] **Paso 3: implementar.** En `ReproductorViewModel.Subtitulos.cs`:

```csharp
private void AbrirPistaSubtitulos(PistaSubtitulosMotor pista)
{
    ReiniciarCuesSubtitulos();

    if (Motor?.DibujaSubtitulosDeTexto == true)
    {
        // Este motor dibuja todas las pistas dentro del video: la app ni extrae las líneas ni prepara su dibujo ASS.
        _pistaTextoActual = pista;
        PistaActualEsAss = pista.EsAss;
        _subtitleCoordinator.SeleccionarPista(Motor, pista);
        return;
    }
    // … lo de hoy, sin cambios
}
```

  - `UsarMiEstiloEnAss` (setter): después de guardar la preferencia, `if (Motor?.DibujaSubtitulosDeTexto == true) { AplicarEstiloSubtitulosAlMotor(); return; }`.
  - `IniciarDibujoAssSiCorresponde`: sale si `motor.DibujaSubtitulosDeTexto`.
  - Nuevo `private void AplicarEstiloSubtitulosAlMotor() => Motor?.AplicarEstiloSubtitulos(EstiloSubtitulos, UsarMiEstiloEnAss);`, llamado al crear el motor (`AsegurarMotorInicializado`), al cambiar `EstiloSubtitulos` (`partial void OnEstiloSubtitulosChanged`) y en el setter anterior.
  - `HabilitarSubtitulos`: `if (Motor?.DibujaSubtitulosDeTexto == true && _pistaTextoActual != null) _subtitleCoordinator.SeleccionarPista(Motor, _pistaTextoActual);`
  - En `ReproductorViewModel.Audio.cs`: `(Motor as MotorFlyleaf)?.RecordarFiltrosParaElSiguienteArchivo(…)` → `Motor.RecordarFiltrosAudio(…)`.
- [ ] **Paso 4:** compilar la app (0 advertencias) y `--filter "FullyQualifiedName~ReproductorConMotor|FullyQualifiedName~CoordinadoresConMotor"`. Esperado: todas pasan.

## Tarea 2: subtítulos en `MotorMpv`

**Archivos:** `MotorMpv.cs`, `MotorMpvTests.cs`.

**Interfaces — consume:** los miembros de la tarea 1. **Produce:** `internal static IReadOnlyList<(string Nombre, string Valor)> OpcionesDeEstilo(EstiloSubtitulos estilo, bool forzar)`; `internal string LeerPropiedad(string nombre)` (el `Leer` de hoy, visible para las pruebas); `Fijar` y `Ordenar` pasan a devolver `bool`.

```csharp
// Calibradas a ojo contra la vista previa de Configuración (tarea 8): 1 = lo que libmpv entiende por ese número.
internal const double EscalaTamano = 1.0, EscalaContorno = 1.0;

internal static IReadOnlyList<(string Nombre, string Valor)> OpcionesDeEstilo(EstiloSubtitulos e, bool forzar)
{
    static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    bool caja = e.OpacidadFondo > 0;
    return
    [
        ("sub-font", e.Fuente),
        ("sub-font-size", N(e.Tamano * EscalaTamano)),
        ("sub-bold", e.Negrita ? "yes" : "no"),
        ("sub-italic", e.Cursiva ? "yes" : "no"),
        ("sub-color", e.ColorTexto),
        ("sub-outline-color", e.ColorContorno),
        ("sub-outline-size", N(e.GrosorContorno * EscalaContorno)),
        ("sub-shadow-offset", "0"),
        ("sub-border-style", caja ? "background-box" : "outline-and-shadow"),
        ("sub-back-color", caja ? $"#{(int)Math.Round(e.OpacidadFondo * 2.55):X2}000000" : "#00000000"),
        // El subrayado solo existe como retoque del estilo ASS; sin forzar, subrayaría también los subtítulos con estilo propio.
        ("sub-ass-style-overrides", forzar && e.Subrayado ? "Underline=-1" : ""),
        ("sub-ass-override", forzar ? "strip" : "no"),
    ];
}
```

  - `forzar` = el usuario pidió «Usar mi estilo» **o** la pista que se muestra no es ASS (en texto plano el estilo siempre es el de Configuración). `MotorMpv` guarda `_estilo`, `_tambienEnAss` y si la pista mostrada es ASS, y vuelve a aplicar las opciones en `AplicarEstiloSubtitulos` y en `MostrarSubtitulosDelMotor`.
  - `DibujaSubtitulosDeTexto => true`. `MostrarSubtitulosDelMotor` sigue sin dar aviso de apertura. `ApagarSubtitulosDelMotor` sigue siendo `sid=no`.
  - El borde de la caja (`GrosorBorde`, `ColorBorde`) no se traduce (diferencia aceptada n.º 1).

- [ ] **Paso 1: pruebas que fallan.**
  - Puras: `OpcionesDeEstilo_ElEstiloPorDefecto` (Trebuchet MS, 44, negrita, blanco, contorno negro 3, sin caja, `sub-ass-override=no`), `OpcionesDeEstilo_ConCajaAl50` (`background-box`, `#80000000`), `OpcionesDeEstilo_ElSubrayadoSoloAlForzar`, `OpcionesDeEstilo_UsaPuntoDecimalAunqueLaCulturaSeaEspanola` (con `CultureInfo.CurrentCulture = es-ES`, tamaño 44,5 → `"44.5"`).
  - Con libmpv real sin pantalla (el video de prueba ya trae una pista SRT en español):
    - `MostrarLaPistaDeTexto_LaDibujaElMotorSinAvisoYConElEstiloDado`: tras abrir, `AplicarEstiloSubtitulos(new EstiloSubtitulos { Tamano = 60, ColorTexto = "#FFE14D" }, false)` y `MostrarSubtitulosDelMotor(pista)`: `IndicePistaSubtitulosDelMotor == pista.Indice`; `LeerPropiedad("sub-font-size")` empieza por `60`; `LeerPropiedad("sub-ass-override") == "strip"` (no es ASS); sigue habiendo un solo aviso de apertura; tras saltar a un instante con línea, `LeerPropiedad("sub-text")` no está vacío.
    - `ApagarLosSubtitulos_DejaElMotorSinPista`: `IndicePistaSubtitulosDelMotor == -1` y `sub-text` vacío.
    - `MostrarUnaPistaQueNoEsDeEsteArchivo_NoHaceNada`: con `new PistaSubtitulosMotor(99, "en", null, false, true, null)` no lanza y el índice sigue en -1.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** `--filter "FullyQualifiedName~MotorMpv"`; todas pasan.

## Tarea 3: la vista deja de dibujar subtítulos

**Archivos:** `ReproductorMpvView.xaml`, `ReproductorMpvView.xaml.cs`, una prueba en `AnimeLocalTracker.Tests/Views/`.

- [ ] **Paso 1: prueba que falla** `LaVistaDelMotorNuevo_NoTieneCapasDeSubtitulosPropias`: el texto de `ReproductorMpvView.xaml` no contiene `SubtitleTextView` ni `SubtitulosAssImagen` (se lee el archivo como hace `VistasSinTextoFijoTests`).
- [ ] **Paso 2:** quitar del XAML los dos `Viewbox` con `SubtitulosVista`/`SubtitulosVistaArriba` y la imagen `SubtitulosAssImagen`; del código, `ActualizarTextoSubtitulos`, toda la capa ASS (`_assCapaActiva`, el bitmap, `ActualizarCapaAss`, `_assDibujando`) y sus suscripciones. `SubtitleTextView`, `SubtitleAssRenderer` y la vista clásica no se tocan.
- [ ] **Paso 3:** compilar (0 advertencias) y correr la prueba.

## Tarea 4: ecualizador y modo noche en `MotorMpv`

**Archivos:** `MotorMpv.cs`, `MotorMpvTests.cs`.

```csharp
private const string EtiquetaFiltros = "filtros";
private IReadOnlyList<FiltroAudio> _filtros = [];        // la cadena montada: da el nombre de cada filtro para el cambio en vivo
private IReadOnlyList<FiltroAudio>? _filtrosPendientes;  // tras un cambio en vivo: se montan al abrir el siguiente archivo

/// <summary>Un solo grafo de FFmpeg, con cada filtro nombrado "nombre@id" para poder cambiarlo en vivo.</summary>
internal static string CadenaDeFiltros(IReadOnlyList<FiltroAudio> filtros) =>
    filtros.Count == 0 ? "" : $"@{EtiquetaFiltros}:lavfi=[{string.Join(",", filtros.Select(f => $"{f.Nombre}@{f.Id}={f.Argumentos}"))}]";

public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
{
    lock (_candado) { _filtros = filtros.ToList(); _filtrosPendientes = null; }
    return Fijar("af", CadenaDeFiltros(filtros));
}

/// <summary>Hay que nombrar el filtro: con el destino por defecto ("todos") libmpv devuelve error aunque el cambio se aplique.</summary>
public bool CambiarFiltroEnVivo(string id, string clave, string valor)
{
    string? nombre;
    lock (_candado) nombre = _filtros.FirstOrDefault(f => f.Id == id)?.Nombre;
    return nombre != null && Ordenar("af-command", EtiquetaFiltros, clave, valor, $"{nombre}@{id}");
}

public void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros) { lock (_candado) _filtrosPendientes = filtros.ToList(); }
```

  - En `Crear`: `af` inicial con `ajustes.FiltrosAudio` (opción, antes de `mpv_initialize`) y `_filtros` con esa lista.
  - En `Abrir`: si hay `_filtrosPendientes`, `AplicarFiltrosAudio` con ellos antes de cargar.
  - El fallo esperado de `af-command` no se anota en el registro como error (lo decide quien llama).

- [ ] **Paso 1: pruebas que fallan.**
  - Puras: `CadenaDeFiltros_SinFiltrosEsVacia`; `CadenaDeFiltros_NombraCadaFiltroConSuId` (con los filtros reales de `EcualizadorAudio.ConstruirFiltros(true, ganancias, true, …)`: contiene `volume@eq_pre=`, `equalizer@eq_b0=f=31`, `acompressor@`).
  - Con libmpv real: `MontarElEcualizador_YCambiarUnaBandaEnVivo` (tras abrir y reproducir 300 ms: `AplicarFiltrosAudio` true, `LeerPropiedad("af")` contiene `equalizer@eq_b0`, `CambiarFiltroEnVivo("eq_b0", "g", "6.0")` true y `CambiarFiltroEnVivo("eq_pre", "volume", "-6.0dB")` true); `CambiarUnFiltroQueNoEstaMontado_DevuelveFalse` (sin cadena, con id desconocido y con el motor recién creado); `LosFiltrosRecordados_SeMontanAlAbrirElSiguienteArchivo` (`RecordarFiltrosAudio` con ganancia 9 → `Abrir` otro → `af` contiene `g=9.0`); `QuitarLosFiltros_VaciaLaCadena`.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** `--filter "FullyQualifiedName~MotorMpv"`.

## Tarea 5: captura de fotograma

**Archivos:** `MotorMpv.cs`, `MotorMpvTests.cs`.

```csharp
/// <summary>El fotograma que se está viendo, con sus subtítulos. libmpv lo escribe a un PNG sin comprimir (35 ms) que se lee y se borra.</summary>
public BitmapSource? CapturarFotograma()
{
    if (_desechado || !_hayArchivo) return null;
    string ruta = Path.Combine(Path.GetTempPath(), $"animelocaltracker-captura-{Guid.NewGuid():N}.png");
    try
    {
        if (!Ordenar("screenshot-to-file", ruta, "subtitles")) return null;
        var imagen = new BitmapImage();
        imagen.BeginInit();
        imagen.CacheOption = BitmapCacheOption.OnLoad; // lee el archivo entero ahora: se puede borrar enseguida
        imagen.UriSource = new Uri(ruta);
        imagen.EndInit();
        imagen.Freeze();
        return imagen;
    }
    catch (Exception ex)
    {
        AppLogger.Debug("MotorMpv", $"No se pudo capturar el fotograma: {ex.Message}");
        return null;
    }
    finally
    {
        try { File.Delete(ruta); } catch { }
    }
}
```

  - En `Crear`: `screenshot-png-compression=0` y `screenshot-high-bit-depth=no`.

- [ ] **Paso 1: pruebas que fallan** (libmpv real): `CapturarUnFotograma_DaLaImagenDelVideoYNoDejaTemporales` (320×180, congelada, y no queda ningún `animelocaltracker-captura-*.png` en la carpeta temporal); `CapturarSinArchivo_DevuelveNullYNoDejaTemporales` (motor recién creado y motor desechado).
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** `--filter "FullyQualifiedName~MotorMpv"`.

## Tarea 6: la tarjeta gráfica en el informe

**Archivos:** `MotorVideo.cs`, `MotorMpv.cs`, `SuperficieMpv.cs`, pruebas en `MotorMpvTests.cs` (la parte pura).

```csharp
// MotorVideo
/// <summary>Fabricante a partir del texto que da el controlador ("Intel", "NVIDIA Corporation", "ATI Technologies Inc.", "AMD Radeon…").</summary>
internal static FabricanteGrafica FabricantePorNombre(string? texto) =>
    string.IsNullOrWhiteSpace(texto) ? FabricanteGrafica.Desconocido
    : texto.Contains("intel", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Intel
    : texto.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || texto.Contains("geforce", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Nvidia
    : texto.Contains("amd", StringComparison.OrdinalIgnoreCase) || texto.Contains("radeon", StringComparison.OrdinalIgnoreCase)
      || texto.Contains("ati ", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Amd
    : FabricanteGrafica.Desconocido;

// MotorMpv
private volatile TarjetaGrafica? _tarjeta;
/// <summary>La tarjeta que pinta el lienzo (la que Windows asigna a la app): la dice la superficie al crear el pintor. Memoria desconocida.</summary>
internal void TarjetaDetectada(string? nombre, string? fabricante)
{
    if (!string.IsNullOrWhiteSpace(nombre)) _tarjeta = new TarjetaGrafica(nombre.Trim(), MotorVideo.FabricantePorNombre($"{fabricante} {nombre}"), 0);
}
public TarjetaGrafica? TarjetaEnUso => _tarjeta;
```

  - `SuperficieMpv.CrearPintor`, tras crear el pintor: `motor.TarjetaDetectada(GL.GetString(StringName.Renderer), GL.GetString(StringName.Vendor))` dentro de un `try` (si falla, el informe sigue con «?»).

- [ ] **Paso 1: pruebas que fallan** (puras): `FabricantePorNombre_*` (Intel, NVIDIA Corporation, ATI Technologies Inc., AMD Radeon RX, vacío y null → Desconocido); `TarjetaDetectada_SeVeEnTarjetaEnUso` y `TarjetaDetectada_SinNombreNoCambiaNada`.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~MotorMpv"`.

## Tarea 7: mini reproductor

**Archivos:** `SuperficieMpv.cs`, `ReproductorMpvView.xaml` y `.xaml.cs`, `ReproductorViewModel.cs`, `NavigationService.cs`, `ReproductorConMotorTests.cs`.

```csharp
// SuperficieMpv
private static readonly ConditionalWeakTable<MotorMpv, SuperficieMpv> PorMotor = new();

/// <summary>La superficie de ese motor. Es una sola: pasa de la vista de la ventana principal a la del mini reproductor y vuelve.</summary>
public static SuperficieMpv Para(MotorMpv motor) => PorMotor.GetValue(motor, m => new SuperficieMpv { Motor = m });

/// <summary>
/// La saca de donde esté y la pone en <paramref name="hueco"/>. Se mueve el mismo lienzo en vez de crear otro porque así el pintor
/// de libmpv sigue vivo: soltarlo apaga el video y tarda 1 s en volver (medido).
/// </summary>
public void MoverA(Decorator hueco)
{
    if (ReferenceEquals(Parent, hueco)) return;
    if (Parent is Decorator anterior) anterior.Child = null;
    hueco.Child = this;
}
```

  - `ReproductorMpvView.xaml`: el `<ctl:SuperficieMpv x:Name="Superficie" Motor="{Binding Motor}"/>` pasa a ser `<Border x:Name="HuecoVideo"/>`.
  - `ReproductorMpvView.xaml.cs`: `AlojarSuperficie()` → `if (IsLoaded && DataContext is ReproductorViewModel { Motor: MotorMpv motor }) SuperficieMpv.Para(motor).MoverA(HuecoVideo);`. Se llama en `Loaded`, en `DataContextChanged` y cuando el reproductor avisa de que cambió `Motor`. En `Unloaded` **ya no se desecha la superficie** (la vista que llega se la lleva; la superficie se suelta sola cuando el motor avisa `Desechando`, como hoy).
  - `ReproductorViewModel.MinimizarAMini`: fuera el `if (UsaMotorMpv) return;`.
  - `NavigationService`: fuera el caso especial de libmpv (navegar vuelve a pasar el episodio al mini reproductor). `ReproductorViewModel.CerrarPorNavegacion` y su prueba se borran si no queda quien lo llame.

- [ ] **Paso 1:** borrar la prueba `CerrarPorNavegacion_GuardaElProgresoYDesechaElMotor` y ajustar las de la etapa 2 que fijaban «con libmpv no hay mini». No hay prueba nueva de la superficie: necesita un lienzo OpenGL real, que el CI no tiene; se verifica en la app (tarea 8, foco n.º 5).
- [ ] **Paso 2:** implementar. **Paso 3:** compilar (0 advertencias) y `--filter "FullyQualifiedName~ReproductorConMotor|FullyQualifiedName~Navigation"`.

## Tarea 8: verificación final y calibración

- [ ] **Paso 1:** compilar la app (0 advertencias).
- [ ] **Paso 2: en la app**, perfil aislado con rutas reales, `MotorVideo=Mpv`, registro de libmpv (`ANIMELOCALTRACKER_MPV_LOG`), foto de los datos reales antes:
  1. **Subtítulos ASS con su estilo** (Youjo Senki): captura de pantalla con un cartel colocado en su sitio; ya no hay capa de la app.
  2. **«Usar mi estilo»** desde el menú de subtítulos: libmpv pasa a `strip`; captura. **Calibración:** capturar la vista previa de Configuración → Reproducción y la misma línea en el reproductor a pantalla completa; medir la altura de una mayúscula y el grosor del contorno en las dos; ajustar `EscalaTamano` y `EscalaContorno` y repetir hasta que coincidan (±1 píxel). Anotar los valores en el plan.
  3. **Apagar y encender** subtítulos; cambiar de pista; episodio siguiente (conserva idioma preferido).
  4. **Subtítulos SRT:** si en la biblioteca no hay ningún episodio con SRT, decirlo en el informe (queda cubierto por la prueba sin pantalla).
  5. **Ecualizador:** encenderlo, mover una banda (libmpv: `af` montado y `af-command` correcto), modo noche, episodio siguiente (la cadena conserva la ganancia). Volumen a 0 en el perfil: no suena.
  6. **Mini reproductor:** tecla `P` → ventana flotante con el video sin corte (el registro de libmpv no dice «Video: no video»; los fotogramas siguen subiendo); cambiar su tamaño por un borde; volver a la ventana principal; repetir tres veces; navegar a otra pestaña con el episodio abierto (pasa al mini); cerrar el episodio desde el mini.
  7. **Pantalla completa** (F11), cajón de episodios (`L`), episodio anterior (`B`), menú de pistas de audio.
  8. **Informe de video:** la línea «Video: …» nombra la tarjeta.
  9. **Memoria:** diez episodios seguidos; anotar el máximo y el reposo.
  10. **Con el motor por defecto (Flyleaf):** subtítulos, mover una banda del ecualizador y pasar al episodio siguiente (conserva la ganancia), mini reproductor: igual que antes, y ningún módulo de libmpv cargado.
  - Cerrar la app por su PID, borrar el perfil, 0 diferencias en los datos reales y en las carpetas de video.
- [ ] **Paso 3:** suite completa una vez con el código final.
- [ ] **Paso 4:** informe al usuario (qué se verificó, qué no —la captura no se pulsa en la app—, los valores de calibración, la memoria) y esperar la orden para el commit.

## Lo que esta etapa deja para las siguientes

- Etapa 4: Anime4K (`EscaladoInteligente` sigue sin hacer nada en `MotorMpv`).
- Etapa 5: motor por defecto, selector «Motor de video» en Configuración, ocultar «Tarjeta gráfica» y el borde de la caja con el motor nuevo, modo de compatibilidad sin OpenGL.
- Etapa 6: retirada de Flyleaf, del dibujante ASS propio y del extractor de subtítulos (la vista previa de Configuración conserva `SubtitleTextView`).
- Pendiente de decisión del propietario desde la etapa 2: archivar el paquete de libmpv en una release del repositorio (hoy se descarga de SourceForge, que conserva el histórico, con GitHub de respaldo).

---

## Notas de ejecución (2026-10-11)

Lo que cambió respecto al plan al implementarlo y probarlo en la app:

- **Calibración del tamaño de los subtítulos** (sustituye a las constantes `EscalaTamano`/`EscalaContorno` de la tarea 2). En Configuración el tamaño es el cuerpo de la letra en píxeles; libmpv mide la celda de la fuente (ascendente + descendente) y lo escala con el alto del video desde 720. Medido con la misma línea en los dos motores: 472 px con WPF y 411 px con libmpv sin corregir (1,148; la celda de Trebuchet MS es 1,161). Ahora `sub-font-size = tamaño × celda de la fuente elegida × 720/768` (la celda se lee de la propia fuente, `MotorMpv.CeldaDeLaFuente`) y `sub-outline-size = grosor × 720/768`: a pantalla completa en 768 de alto la letra y el contorno miden lo que dice Configuración; en la ventana maximizada (728) salen un 5 % más pequeños y en el mini reproductor, proporcionados.
- **Cada archivo empieza sin subtítulos del motor** (`sid=no` en `Abrir`): libmpv conservaba la pista del episodio anterior y el reproductor, al ver que el motor ya traía una, no elegía la suya (el menú perdía «Usar mi estilo» y con otro archivo podía salir otro idioma).
- **La superficie ya no suelta el pintor al salir del árbol visual** (solo cuando el motor se desecha): era lo que habría cortado el video 1 s al pasar al mini reproductor.
- **Opacidad de la caja:** redondeo hacia fuera (50 % → `#80`).
- Las pruebas de `MotorMpv` de las tareas 2, 4, 5 y 6 se escribieron juntas y se implementó después tarea a tarea.

Verificado en la app (perfil aislado, Intel HD 620, 1366×768): subtítulos ASS con su estilo y carteles en su sitio; «Usar mi estilo»; apagar, cambiar de pista y episodio siguiente; ecualizador (cadena montada, cambio de banda en vivo, se conserva al cambiar de episodio); mini reproductor (alternar tres veces, navegar con el episodio abierto, cerrar desde el mini: un solo pintor en toda la sesión, sin cortes, 3095 fotogramas en 130 s); pantalla completa, cajón, episodio anterior, menú de audio; informe con «Intel(R) HD Graphics 620». Memoria en diez episodios seguidos: 496–534 MB reproduciendo y 340 MB en reposo (antes ~570 MB: la app ya no dibuja ni extrae subtítulos por su cuenta). Con Flyleaf: episodio siguiente, subtítulos y mini reproductor como antes, sin módulos de libmpv.

No verificado en la app: la captura de fotograma (no se pulsa: escribe en Imágenes y en el portapapeles; cubierta por la prueba sin pantalla), un episodio con subtítulos SRT (cubierto por la prueba sin pantalla), el ecualizador con Flyleaf (sus controles no se alcanzan por accesibilidad; cubierto por la prueba del reproductor) y cambiar el tamaño del mini arrastrando un borde.
