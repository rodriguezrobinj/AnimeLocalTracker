# Plan de implementación: subtítulos ASS/SSA con su estilo original (2026-10-04)

> **Ejecución:** tarea a tarea en la sesión principal (`superpowers:executing-plans`). Por las reglas del repo: **sin subagentes, sin ramas ni worktrees, y sin commits** salvo orden del usuario; por eso ninguna tarea termina en "commit", sino en compilación y pruebas. Los pasos usan casillas (`- [ ]`).

**Objetivo:** que una pista ASS/SSA incrustada se vea con el estilo del fansub (posición, colores, fuentes, movimiento, karaoke), con un interruptor "Usar mi estilo" para volver al texto plano.

**Arquitectura:** el `avfilter-12.dll` que la app ya distribuye incluye libass. Un servicio nuevo (`SubtitleAssRenderer`) monta el grafo `buffer → subtitles(alpha=1) → buffersink` y devuelve, para un instante dado, un fotograma BGRA transparente. `ReproductorView` lo pinta en una `Image` sobre el video al ritmo de `CompositionTarget.Rendering`; el cálculo va en un hilo de fondo. El ViewModel decide cuándo se abre y se cierra; la extracción a SRT actual se mantiene como arranque, respaldo e interruptor.

**Tecnología:** .NET 8, WPF, `Flyleaf.FFmpeg` 9.0.0 (`using static Flyleaf.FFmpeg.Raw`), FlyleafLib 3.11.3, CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-subtitulos-ass.md` (el ejecutor lee ambos).

## Restricciones globales

- Sin commit ni push sin orden del usuario. Sin ramas, worktrees ni subagentes.
- Todo mensaje al usuario en español. Texto de interfaz por `LocalizationService` (XAML: `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}, Mode=OneWay}`); nunca literales.
- Las pruebas y scripts **nunca escriben ni borran bajo `AppDataPaths`**; los archivos de prueba van a `Path.GetTempPath()`.
- `Config.Subtitles.Enabled` de Flyleaf sigue en `false` para las pistas de texto. No se toca `ParcheSubtitulosFlyleaf`.
- `async void` solo en manejadores de eventos de la vista, con `try/catch`. Trabajo lento fuera del hilo de interfaz. Lo que toque controles, en el `Dispatcher`.
- Colores: solo literales que ya existen en `ReproductorView.xaml` (`#E5E5E5`, `#818CF8`) o la paleta `Brush.*`.
- Verificación con el skill `repo-build-test`: **0 errores, 0 advertencias** y toda la suite en verde. Si aparece `CS2001` con archivos `.g.cs`, es el flake conocido: reintentar el mismo comando.
- Pruebas en la app real con el skill `wpf-visual-verification`: solo se cierra el proceso que lanzó el agente (por PID), copia de BD/ajustes, nunca pasar del 85 % de un episodio ni reproducir el último.
- No se añaden paquetes ni binarios nuevos.

## Foco de revisión

Entradas que el diseño no menciona y que más probablemente fallen a un usuario. Cada una tiene su prueba en la tarea indicada.

1. **Nombre de archivo de fansub** (corchetes, apóstrofo, coma, tildes): debe abrir igual que un nombre simple → prueba en la tarea 2.
2. **Retroceder en el video:** pedir un instante anterior debe dar el mismo fotograma que la primera vez → prueba en la tarea 2.
3. **Cambiar de episodio o de pista mientras el dibujante se abre:** el resultado tardío no debe activar el modo ASS → prueba en la tarea 3.
4. **La pista elegida no es la primera, o hay una pista de imagen antes:** debe dibujarse la elegida, no la primera → prueba en la tarea 1 y comprobación real en la fase 0.
5. **Tamaño de video desconocido (0×0) o búfer más pequeño que el fotograma:** no se abre el modo ASS, sin excepción → pruebas en las tareas 1 y 2.

## Archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `AnimeLocalTracker/Core/SubtitulosAss.cs` | crear | Tres decisiones puras: si se dibuja con ASS, índice de pista para el filtro, tamaño de dibujo |
| `AnimeLocalTracker/Services/SubtitleAssRenderer.cs` | crear | Interfaz + grafo de FFmpeg que devuelve fotogramas |
| `AnimeLocalTracker/Models/AppSettings.cs` | modificar | Ajuste `UsarMiEstiloEnAss` |
| `AnimeLocalTracker/App.xaml.cs` (~287) | modificar | Registro en DI |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` | modificar | Estado ASS, abrir/cerrar, interruptor |
| `AnimeLocalTracker/Views/ReproductorView.xaml` (~313 y ~755) | modificar | Capa de imagen y entrada de menú |
| `AnimeLocalTracker/Views/ReproductorView.xaml.cs` | modificar | Ciclo de dibujo |
| `AnimeLocalTracker/Services/LocalizationService.cs` (~722 y ~2044) | modificar | Clave `Player_UsarMiEstilo` (es/en) |
| `AnimeLocalTracker.Tests/Services/SubtitulosAssTests.cs` | crear | Pruebas de las decisiones puras |
| `AnimeLocalTracker.Tests/Services/SubtitleAssRendererTests.cs` | crear | Dibujo real con un `.ass` temporal |
| `AnimeLocalTracker.Tests/ViewModels/ReproductorViewModelSubtitulosAssTests.cs` | crear | Estado del ViewModel con dibujante simulado |

**Aviso sobre el código de este plan:** las firmas de `Flyleaf.FFmpeg` y FlyleafLib están comprobadas por reflexión sobre los ensamblados del repo (`Raw.avfilter_*`, `AVFrame.data[i]` devuelve `IntPtr`, `AVPixelFormat.Bgra`, `OptSearchFlags.Children`, `LoadProfile.Filters`, `AVCodecID.Ass/Ssa`, `Player.Video.Width/Height/FramesDropped`, `Config.Player.UICurTime`). El código en sí **no se ha compilado**: la fase 0 es la primera vez que se ejecuta.

---

## Fase 0: prueba desechable

Mide los cuatro riesgos de la sección 7 del diseño. Nada de esta fase se conserva: la parte A vive en la carpeta temporal de la sesión y la parte B se revierte al terminar (`git status` debe quedar limpio salvo `docs/`).

### Parte A: libass fuera de la app (transparencia, apertura, coste de dibujo, índice de pista)

- [ ] **Paso 1: localizar un episodio con pista ASS**

Leer (solo lectura) `RutaBaseAnimes` de `%LocalAppData%\AnimeLocalTrackerData\settings.json` y buscar un `.mkv` con pista `ass`. Para cada candidato:

```
AnimeLocalTracker/FFmpeg/ffprobe.exe -v error -select_streams s -show_entries stream=index,codec_name:stream_tags=language,title -of csv=p=0 "<archivo.mkv>"
```

Sirve uno con al menos dos pistas de subtítulos (para comprobar el índice) y con carteles o karaoke. Referencia conocida: Re:Zero S4 ep 19 (cartel a 4:13). Si no aparece ninguno, pedir una ruta al usuario.

- [ ] **Paso 2: crear el proyecto de prueba en la carpeta temporal de la sesión**

`<scratchpad>/spike-ass/spike-ass.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Flyleaf.FFmpeg" Version="9.0.0" />
  </ItemGroup>
</Project>
```

`<scratchpad>/spike-ass/Program.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flyleaf.FFmpeg;
using static Flyleaf.FFmpeg.Raw;

// Uso: spike-ass <carpetaFFmpeg> <archivo> <indiceEntreSubtitulos> <ancho> <alto> <carpetaSalida> <segundos,separados,por,comas>
internal static unsafe class Program
{
    static AVFilterContext* origen, destino;
    static AVFrame* entrada, fotograma;
    static int ancho, alto;
    static byte[] bufer;

    static int Main(string[] args)
    {
        string archivo = args[1], salida = args[5];
        int indice = int.Parse(args[2]);
        ancho = int.Parse(args[3]); alto = int.Parse(args[4]);
        double[] instantes = args[6].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Directory.CreateDirectory(salida);
        bufer = new byte[ancho * alto * 4];

        LoadLibraries(args[0], LoadProfile.Filters, false);

        var reloj = Stopwatch.StartNew();
        AVFilterGraph* grafo = avfilter_graph_alloc();
        AVFilterContext* o = null, d = null;
        Comprobar(avfilter_graph_create_filter(&o, avfilter_get_by_name("buffer"), "entrada",
            $"video_size={ancho}x{alto}:pix_fmt={(int)AVPixelFormat.Bgra}:time_base=1/1000:pixel_aspect=1/1", null, grafo), "buffer");
        AVFilterContext* subs = avfilter_graph_alloc_filter(grafo, avfilter_get_by_name("subtitles"), "subtitulos");
        Comprobar(av_opt_set(subs, "filename", archivo, OptSearchFlags.Children), "filename");
        if (indice >= 0) Comprobar(av_opt_set_int(subs, "stream_index", indice, OptSearchFlags.Children), "stream_index");
        Comprobar(av_opt_set_int(subs, "alpha", 1, OptSearchFlags.Children), "alpha");
        Comprobar(avfilter_init_str(subs, null), "init subtitles");
        Comprobar(avfilter_graph_create_filter(&d, avfilter_get_by_name("buffersink"), "salida", null, null, grafo), "buffersink");
        Comprobar(avfilter_link(o, 0, subs, 0), "link 1");
        Comprobar(avfilter_link(subs, 0, d, 0), "link 2");
        Comprobar(avfilter_graph_config(grafo, null), "config");
        Console.WriteLine($"APERTURA: {reloj.ElapsedMilliseconds} ms");

        origen = o; destino = d;
        entrada = av_frame_alloc(); fotograma = av_frame_alloc();

        foreach (double t in instantes)
        {
            double ms = Dibujar(t);
            int opacos = 0, parciales = 0, noPremultiplicados = 0;
            for (int i = 0; i < bufer.Length; i += 4)
            {
                byte a = bufer[i + 3];
                if (a == 0) continue;
                if (a == 255) { opacos++; continue; }
                parciales++;
                if (Math.Max(bufer[i], Math.Max(bufer[i + 1], bufer[i + 2])) > a) noPremultiplicados++;
            }
            Console.WriteLine($"t={t:F2}s dibujo={ms:F2} ms opacos={opacos} parciales={parciales} noPremultiplicados={noPremultiplicados}");

            var imagen = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Pbgra32, null, bufer, ancho * 4);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(imagen));
            using var archivoPng = File.Create(Path.Combine(salida, $"t{t:F2}.png".Replace(',', '.')));
            png.Save(archivoPng);
        }

        // Coste sostenido: 10 s seguidos a 24 fps desde el primer instante, y luego un salto atrás.
        double suma = 0, maximo = 0;
        for (int i = 0; i < 240; i++)
        {
            double ms = Dibujar(instantes[0] + i / 24.0);
            suma += ms; maximo = Math.Max(maximo, ms);
        }
        Console.WriteLine($"SOSTENIDO 240 fotogramas: media={suma / 240:F2} ms max={maximo:F2} ms");
        Console.WriteLine($"SALTO ATRAS: {Dibujar(instantes[0]):F2} ms");
        return 0;
    }

    static double Dibujar(double segundos)
    {
        var reloj = Stopwatch.StartNew();
        entrada->format = (int)AVPixelFormat.Bgra;
        entrada->width = ancho;
        entrada->height = alto;
        Comprobar(av_frame_get_buffer(entrada, 0), "get_buffer");
        new Span<byte>((void*)entrada->data[0], entrada->linesize[0] * alto).Clear();
        entrada->pts = (long)(segundos * 1000);
        Comprobar(av_buffersrc_add_frame(origen, entrada), "add_frame");
        Comprobar(av_buffersink_get_frame(destino, fotograma), "get_frame");
        byte* p = (byte*)fotograma->data[0];
        int paso = fotograma->linesize[0], fila = ancho * 4;
        for (int y = 0; y < alto; y++) new ReadOnlySpan<byte>(p + y * paso, fila).CopyTo(bufer.AsSpan(y * fila, fila));
        av_frame_unref(fotograma);
        return reloj.Elapsed.TotalMilliseconds;
    }

    static void Comprobar(int resultado, string paso)
    {
        if (resultado < 0) throw new InvalidOperationException($"{paso}: error {resultado}");
    }
}
```

- [ ] **Paso 3: compilar y ejecutar**

```
dotnet build <scratchpad>/spike-ass -c Release
dotnet <scratchpad>/spike-ass/bin/Release/net8.0-windows/spike-ass.dll "C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker\FFmpeg" "<archivo.mkv>" 0 1364 768 "<scratchpad>/spike-ass/out" 253,90,600
```

Los instantes: un cartel (253 s en Re:Zero S4 ep 19), el opening con karaoke y un diálogo normal. Si `LoadLibraries` falla, probar `LoadProfile.All` y anotar la llamada que funciona: la tarea 2 la reutiliza en las pruebas.

Repetir con el índice de la segunda pista (`1`) y confirmar en el PNG que el idioma o el contenido cambia. Repetir la primera ejecución una segunda vez para medir la apertura con el archivo ya en la caché de Windows.

- [ ] **Paso 4: leer los resultados**

Abrir los PNG con la herramienta Read y anotar:

| Dato | Qué se espera |
|---|---|
| `APERTURA` (primera vez y segunda) | Se informa; no bloquea |
| `noPremultiplicados` con texto claro | `0` → el filtro entrega alfa premultiplicado y la vista usa `PixelFormats.Pbgra32` (lo que asume la tarea 4). Mayor que 0 → alfa recto: en la tarea 4 se cambia a `PixelFormats.Bgra32` |
| PNG | Fondo transparente, cartel en su sitio, bordes limpios, sin halo oscuro |
| `SOSTENIDO` media y máximo | Media más copia por debajo de ~10 ms |
| `SALTO ATRAS` | Sin error |
| Índice `1` | Muestra la segunda pista de subtítulos |

### Parte B: dentro de la app (coste de pintar sobre el video y precisión del reloj)

- [ ] **Paso 5: añadir el medidor temporal**

Crear `AnimeLocalTracker/Views/SpikeAss.cs`:

```csharp
// DESECHABLE: fase 0 de docs/plan-subtitulos-ass.md. Se borra al terminar la medición.
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Views;

internal static class SpikeAss
{
    /// <summary>ALT_SPIKE_ASS=medir solo mide el reloj y los fotogramas perdidos; =pintar además pinta una imagen completa por fotograma.
    /// ALT_SPIKE_RELOJ=1 pide a Flyleaf el tiempo fotograma a fotograma.</summary>
    public static void Iniciar(Grid raiz, Func<Player?> obtenerPlayer)
    {
        bool pintar = Environment.GetEnvironmentVariable("ALT_SPIKE_ASS") == "pintar";
        bool relojPorFotograma = Environment.GetEnvironmentVariable("ALT_SPIKE_RELOJ") == "1";
        const int W = 1364, H = 768;
        var bitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Pbgra32, null);
        var bufer = new byte[W * H * 4];
        if (pintar) raiz.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, IsHitTestVisible = false });

        long ultimoTick = -1, marcaUltimo = 0, marcaInforme = Stopwatch.GetTimestamp();
        int pintados = 0, cambios = 0, x = 0;
        double msPintar = 0, msPintarMax = 0, saltoMax = 0, intervaloMax = 0;

        CompositionTarget.Rendering += (_, _) =>
        {
            var p = obtenerPlayer();
            if (p == null || p.Status != Status.Playing) return;
            p.Config.Player.Stats = true;
            if (relojPorFotograma) p.Config.Player.UICurTime = FlyleafLib.UIRefreshType.PerFrame;

            long tick = p.CurTime;
            if (tick != ultimoTick)
            {
                long ahora = Stopwatch.GetTimestamp();
                if (ultimoTick >= 0)
                {
                    saltoMax = Math.Max(saltoMax, TimeSpan.FromTicks(tick - ultimoTick).TotalMilliseconds);
                    intervaloMax = Math.Max(intervaloMax, Stopwatch.GetElapsedTime(marcaUltimo, ahora).TotalMilliseconds);
                }
                ultimoTick = tick; marcaUltimo = ahora; cambios++;

                if (pintar)
                {
                    var reloj = Stopwatch.StartNew();
                    Array.Clear(bufer);
                    x = (x + 8) % (W - 400);
                    for (int y = H - 140; y < H - 80; y++) bufer.AsSpan((y * W + x) * 4, 400 * 4).Fill(255); // caja blanca que se desplaza
                    bitmap.WritePixels(new Int32Rect(0, 0, W, H), bufer, W * 4, 0);
                    double ms = reloj.Elapsed.TotalMilliseconds;
                    msPintar += ms; msPintarMax = Math.Max(msPintarMax, ms); pintados++;
                }
            }

            if (Stopwatch.GetElapsedTime(marcaInforme).TotalSeconds < 5) return;
            AppLogger.Info("SpikeAss", $"pintar={pintar} relojPorFotograma={relojPorFotograma} cambiosDeReloj={cambios} saltoMaxReloj={saltoMax:F0} ms intervaloMax={intervaloMax:F0} ms pintados={pintados} msPintarMedia={(pintados > 0 ? msPintar / pintados : 0):F2} msPintarMax={msPintarMax:F2} mostrados={p.Video.FramesDisplayed} perdidos={p.Video.FramesDropped} fps={p.Video.FPS:F2}");
            marcaInforme = Stopwatch.GetTimestamp();
            pintados = 0; cambios = 0; msPintar = 0; msPintarMax = 0; saltoMax = 0; intervaloMax = 0;
        };
    }
}
```

En `ReproductorView.xaml.cs`, al final de `ReproductorView_Loaded`:

```csharp
            if (Environment.GetEnvironmentVariable("ALT_SPIKE_ASS") is { Length: > 0 })
                SpikeAss.Iniciar((Grid)((FrameworkElement)SubtitulosVista.Parent).Parent, () => (DataContext as ReproductorViewModel)?.Player);
```

- [ ] **Paso 6: compilar y medir tres veces**

Compilar con `repo-build-test`. Lanzar la app con `wpf-visual-verification` (copia de BD/ajustes; si la instancia del usuario está abierta, pedirle que la cierre, no cerrarla). En cada pasada, reproducir en pantalla completa unos 60 s de un episodio 1080p y leer las líneas `SpikeAss` del registro de la sesión:

| Pasada | Variables | Qué da |
|---|---|---|
| 1 | `ALT_SPIKE_ASS=medir` | Fotogramas perdidos de base y saltos del reloj por defecto |
| 2 | `ALT_SPIKE_ASS=pintar` | Fotogramas perdidos pintando, y `msPintarMedia`/`msPintarMax`. Mirar la caja blanca: ¿se mueve suave? |
| 3 | `ALT_SPIKE_ASS=medir` + `ALT_SPIKE_RELOJ=1` | Saltos del reloj pidiendo el tiempo por fotograma |

- [ ] **Paso 7: retirar el medidor**

```
rm AnimeLocalTracker/Views/SpikeAss.cs
git restore AnimeLocalTracker/Views/ReproductorView.xaml.cs
git status --short
```

Esperado: solo aparecen los dos documentos de `docs/`.

- [ ] **Paso 8: informe y decisión del usuario (PARADA)**

Añadir al diseño (`docs/investigacion-subtitulos-ass.md`) una sección "Resultados de la fase 0" con los números y estas decisiones:

| Decisión | Regla |
|---|---|
| Formato de píxel | `noPremultiplicados == 0` → `Pbgra32`; si no → `Bgra32` |
| Reloj | `saltoMaxReloj` ≤ 45 ms en la pasada 1 → se usa `Player.CurTime` tal cual. Si no, y la pasada 3 sí lo cumple → se añade `config.Player.UICurTime = UIRefreshType.PerFrame` en la configuración del Player. Si tampoco → reloj interpolado (tarea 4, paso 5) |
| Fluidez | Sigue si (dibujo sostenido medio + `msPintarMedia`) < ~10 ms y los fotogramas perdidos de la pasada 2 no superan claramente a los de la 1. Si no: repetir la parte A a 960×540 y, si aun así falla, parar y replantear el diseño |
| Apertura | Se informa. Si supera ~15 s con el archivo en caché, proponer al usuario la alternativa del diseño (extraer `.ass` y fuentes a una carpeta temporal) antes de seguir |
| Índice de pista y `LoadLibraries` | Anotar la semántica confirmada y la llamada exacta que funcionó |

**Presentar los números al usuario y esperar su decisión antes de la tarea 1.**

---

## Tarea 1: decisiones puras (`SubtitulosAss`)

**Archivos:**
- Crear: `AnimeLocalTracker/Core/SubtitulosAss.cs`
- Prueba: `AnimeLocalTracker.Tests/Services/SubtitulosAssTests.cs`

**Interfaces:**
- Produce: `SubtitulosAss.DebeDibujarse(bool esAss, bool esImagen, bool esExterna, bool usarMiEstilo) : bool`, `SubtitulosAss.IndiceEntreSubtitulos(IEnumerable<int> indicesDePistasDeSubtitulos, int indiceEnContenedor) : int`, `SubtitulosAss.TamanoDibujo(int anchoVideo, int altoVideo, int anchoPantalla, int altoPantalla) : (int Ancho, int Alto)`.

- [ ] **Paso 1: escribir las pruebas**

`AnimeLocalTracker.Tests/Services/SubtitulosAssTests.cs`:

```csharp
using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class SubtitulosAssTests
{
    [Theory]
    [InlineData(true, false, false, false, true)]   // ASS incrustada, estilo original
    [InlineData(true, false, false, true, false)]   // el usuario pidió su estilo
    [InlineData(false, false, false, false, false)] // SRT
    [InlineData(true, true, false, false, false)]   // pista de imagen
    [InlineData(true, false, true, false, false)]   // archivo externo (fuera de alcance)
    public void DebeDibujarse_SoloAssIncrustadaSinEstiloPropio(bool esAss, bool esImagen, bool esExterna, bool usarMiEstilo, bool esperado)
        => SubtitulosAss.DebeDibujarse(esAss, esImagen, esExterna, usarMiEstilo).Should().Be(esperado);

    [Fact]
    public void IndiceEntreSubtitulos_CuentaLasPistasDeSubtitulosAnteriores()
    {
        // Contenedor: 0 video, 1 audio, 2 y 3 subtítulos, 4 audio, 5 subtítulos.
        int[] subtitulos = { 2, 3, 5 };

        SubtitulosAss.IndiceEntreSubtitulos(subtitulos, 2).Should().Be(0);
        SubtitulosAss.IndiceEntreSubtitulos(subtitulos, 3).Should().Be(1);
        SubtitulosAss.IndiceEntreSubtitulos(subtitulos, 5).Should().Be(2);
    }

    [Fact]
    public void IndiceEntreSubtitulos_NoDependeDelOrdenDeLaLista()
        => SubtitulosAss.IndiceEntreSubtitulos(new[] { 5, 2, 3 }, 5).Should().Be(2);

    [Fact]
    public void IndiceEntreSubtitulos_PistaQueNoEstaEnLaLista_DevuelveMenosUno()
        => SubtitulosAss.IndiceEntreSubtitulos(new[] { 2, 3 }, 4).Should().Be(-1);

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]  // cabe: tamaño del video
    [InlineData(1920, 1080, 1366, 768, 1364, 768)]  // no cabe: se reduce sin deformar, dimensiones pares
    [InlineData(1440, 1080, 1366, 768, 1024, 768)]  // 4:3
    [InlineData(0, 0, 1366, 768, 0, 0)]             // tamaño de video desconocido
    [InlineData(1920, 1080, 0, 0, 0, 0)]            // pantalla desconocida
    public void TamanoDibujo_AjustaALaPantallaSinDeformar(int anchoVideo, int altoVideo, int anchoPantalla, int altoPantalla, int ancho, int alto)
        => SubtitulosAss.TamanoDibujo(anchoVideo, altoVideo, anchoPantalla, altoPantalla).Should().Be((ancho, alto));
}
```

- [ ] **Paso 2: comprobar que falla**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~SubtitulosAssTests"
```

Esperado: error de compilación, `SubtitulosAss` no existe.

- [ ] **Paso 3: implementar**

`AnimeLocalTracker/Core/SubtitulosAss.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Decisiones puras (sin Flyleaf ni WPF) del dibujo de subtítulos ASS/SSA con su estilo original.
/// Ver docs/investigacion-subtitulos-ass.md.
/// </summary>
public static class SubtitulosAss
{
    /// <summary>Solo las pistas ASS/SSA de texto incrustadas se dibujan con su estilo, y solo si el usuario no pidió el suyo.</summary>
    public static bool DebeDibujarse(bool esAss, bool esImagen, bool esExterna, bool usarMiEstilo)
        => esAss && !esImagen && !esExterna && !usarMiEstilo;

    /// <summary>
    /// El filtro de ffmpeg numera la pista entre las de subtítulos (0 = la primera), no con el índice del contenedor que da
    /// Flyleaf. Devuelve -1 si la pista no está en la lista.
    /// </summary>
    public static int IndiceEntreSubtitulos(IEnumerable<int> indicesDePistasDeSubtitulos, int indiceEnContenedor)
    {
        int anteriores = 0;
        bool esta = false;
        foreach (int indice in indicesDePistasDeSubtitulos)
        {
            if (indice == indiceEnContenedor) esta = true;
            else if (indice < indiceEnContenedor) anteriores++;
        }
        return esta ? anteriores : -1;
    }

    /// <summary>Tamaño al que se dibuja: el del video, reducido sin deformar si no cabe en la pantalla. (0, 0) si no se conoce.</summary>
    public static (int Ancho, int Alto) TamanoDibujo(int anchoVideo, int altoVideo, int anchoPantalla, int altoPantalla)
    {
        if (anchoVideo <= 0 || altoVideo <= 0 || anchoPantalla <= 0 || altoPantalla <= 0) return (0, 0);

        double escala = Math.Min(1.0, Math.Min((double)anchoPantalla / anchoVideo, (double)altoPantalla / altoVideo));
        return (Par(anchoVideo * escala), Par(altoVideo * escala));

        static int Par(double valor) => Math.Max(2, (int)Math.Round(valor) & ~1);
    }
}
```

- [ ] **Paso 4: comprobar que pasa**

Mismos dos comandos del paso 2. Esperado: 0 errores, 0 advertencias; 13 pruebas en verde (5 + 3 + 5, contando cada caso de las teorías).

---

## Tarea 2: el dibujante (`SubtitleAssRenderer`)

> **Reescrita tras la fase 0** (ver sección 7.1 del diseño). Cambios respecto a la versión original: doble pasada en vez de `alpha=1`, corrección del rango de color, solo se tocan las filas con subtítulo, y se usa el ensamblado `Flyleaf.FFmpeg.Bindings`. El código definitivo está en el propio archivo; aquí queda el contrato, el algoritmo y las pruebas.

**Archivos:**
- Modificar: `AnimeLocalTracker/AnimeLocalTracker.csproj:46`: `Aliases="FFmpegExtendido"` en el paquete `Flyleaf.FFmpeg`. FlyleafLib usa `Flyleaf.FFmpeg.Bindings` y es ese el que tiene las bibliotecas cargadas; con los dos visibles, `Flyleaf.FFmpeg.Raw` es ambiguo (CS0433). El código de la app no usa el paquete suelto.
- Modificar: `AnimeLocalTracker/Core/SubtitulosAss.cs`: función pura `RangoComprimido`.
- Crear: `AnimeLocalTracker/Services/SubtitleAssRenderer.cs`
- Pruebas: `AnimeLocalTracker.Tests/Services/SubtitleAssRendererTests.cs` y casos nuevos en `SubtitulosAssTests.cs`

**Interfaces (produce):**

```csharp
public interface ISubtitleAssRenderer
{
    int Ancho { get; }   // 0 si no hay pista abierta
    int Alto { get; }
    Task<bool> AbrirAsync(string rutaArchivo, int indiceEntreSubtitulos, int ancho, int alto, CancellationToken ct); // índice < 0 = primera pista
    /// destinoBgra: Ancho × Alto × 4, BGRA premultiplicado (Pbgra32). filaInicial/filas: franja que cambió y hay que repintar
    /// (incluye lo que había antes y ya no está); filas = 0 si no cambió nada.
    bool Renderizar(TimeSpan instante, byte[] destinoBgra, out int filaInicial, out int filas);
    void Cerrar();
}
```

- `SubtitulosAss.RangoComprimido(string? cabeceraDelGuion) : bool`: `false` si la cabecera declara `YCbCr Matrix: None` o `PC.*`; `true` en cualquier otro caso (TV.*, ausente, desconocida). Replica `ass_get_color_range` de `vf_subtitles.c`.
- `SubtitleAssRenderer.CargarBibliotecas(string carpetaFFmpeg)` (`internal static`): solo para las pruebas, donde Flyleaf no arranca. Vive en el proyecto principal para que el de pruebas no tenga que nombrar tipos de `Flyleaf.FFmpeg` (ambiguos allí también).

**Algoritmo de `Renderizar`** (validado en la fase 0):

1. Limpiar en el destino las filas que quedaron pintadas en la llamada anterior (o el búfer entero si es otro búfer).
2. Pasada sobre **blanco**: fotograma BGRA a 255 con `pts` = instante en ms. Buscar la primera y la última fila que no sean todo 255. Si no hay ninguna, no hay subtítulo: terminar.
3. Guardar esas filas y hacer la pasada sobre **negro** (fotograma a 0).
4. Por cada píxel de esas filas: color = lo dibujado sobre negro (ya premultiplicado); alfa = 255 − (verde sobre blanco − verde sobre negro).
5. Si el guion tiene rango comprimido: `c = clamp((c·255 − 16·alfa) / 219, 0, alfa)` en cada canal.
6. Devolver la franja = unión de las filas anteriores y las nuevas.

**Apertura:** sin candado (leer el archivo tarda), con contador de generación para descartar el resultado si entretanto se cerró o se pidió otra pista. Las opciones del filtro se fijan con `av_opt_set` (sin `alpha`). La cabecera del guion se lee de `codecpar->extradata` de la pista con `avformat_open_input` (solo cabecera del contenedor).

- [ ] **Paso 1: pruebas** (`SubtitleAssRendererTests`, con `.ass` en `Path.GetTempPath()`):
  - línea roja en `\pos(10,10)` → rojo arriba a la izquierda, mitad inferior transparente, y franja devuelta dentro de la mitad superior;
  - instante sin líneas → `filas == 0` y destino transparente aunque trajera basura;
  - tras una línea, el instante vacío devuelve la franja anterior y la deja limpia;
  - retroceder en el tiempo da el mismo fotograma;
  - caja blanca al 50 % con `YCbCr Matrix: None` → B, G, R y A ≈ 127 (transparencia exacta);
  - caja blanca opaca **sin** cabecera de matriz → 255 (rango corregido); con `None` → 255;
  - nombre de archivo de fansub (corchetes, apóstrofo, coma, tilde) abre y dibuja;
  - archivo inexistente, tamaño cero, búfer pequeño y uso tras `Cerrar` → `false` sin excepción.
  - `SubtitulosAssTests`: `RangoComprimido` con cabecera ausente, `TV.601`, `TV.709`, `None`, `PC.709`, mayúsculas/minúsculas y espacios.
- [ ] **Paso 2:** compilar y ver que fallan por compilación (el tipo no existe).
- [ ] **Paso 3:** implementar (`RangoComprimido`, alias en el `.csproj`, `SubtitleAssRenderer`).
- [ ] **Paso 4:** las pruebas nuevas pasan; 0 errores y 0 advertencias en el build.
- [ ] **Paso 5:** suite completa en verde (cargar FFmpeg en el proceso de pruebas no debe romper otras pruebas). Si las DLL no cargan en el proceso de pruebas, dejar solo las pruebas que no tocan FFmpeg y decírselo al usuario.

---

## Tarea 3: estado y ciclo de vida en el ViewModel

**Archivos:**
- Modificar: `AnimeLocalTracker/Models/AppSettings.cs` (junto a `SubtitulosPorDefecto`, línea 11)
- Modificar: `AnimeLocalTracker/App.xaml.cs:287`
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` (constructor ~676-745, `ReiniciarCuesSubtitulos` ~310, `AbrirPistaSubtitulos` ~328, `Dispose` ~2643)
- Prueba: `AnimeLocalTracker.Tests/ViewModels/ReproductorViewModelSubtitulosAssTests.cs`

**Interfaces:**
- Consume: `ISubtitleAssRenderer` (tarea 2), `SubtitulosAss.*` (tarea 1).
- Produce (lo usa la tarea 4):
  - `bool SubtitulosAssActivo` (observable): el dibujante está listo y manda sobre el texto plano.
  - `bool PistaActualEsAss` (observable): el menú muestra el interruptor.
  - `bool UsarMiEstiloEnAss` + `ToggleUsarMiEstiloEnAssCommand`.
  - `ISubtitleAssRenderer DibujanteAss`.
  - `void NotificarFalloDibujoAss()`.
  - `internal void IniciarDibujoAss(string ruta, int indice, int ancho, int alto)`, `internal void CerrarDibujoAss()` (visibles para las pruebas).

Nota: el diseño decía "singleton en DI". Se registra como **transient** (un dibujante por `ReproductorViewModel`): con un singleton, el `Dispose` del ViewModel anterior podría cerrar la pista que acaba de abrir el nuevo.

- [ ] **Paso 1: escribir las pruebas**

`AnimeLocalTracker.Tests/ViewModels/ReproductorViewModelSubtitulosAssTests.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ReproductorViewModelSubtitulosAssTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly Mock<ISubtitleAssRenderer> _dibujante = new();
    private readonly AppSettings _config = new();

    private ReproductorViewModel Crear()
    {
        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        return new ReproductorViewModel(_db.Object, _tracking.Object, _auth.Object, null, _ajustes.Object, subtitleAssRenderer: _dibujante.Object);
    }

    private void AbrirDevuelve(Task<bool> resultado) =>
        _dibujante.Setup(d => d.AbrirAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns(resultado);

    [Fact]
    public void IniciarDibujoAss_CuandoElDibujanteAbre_ActivaElModoAss()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_SiElDibujanteFalla_SeQuedaEnTextoPlano()
    {
        AbrirDevuelve(Task.FromResult(false));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_SiSeCambiaDePistaMientrasAbre_ElResultadoTardioNoActivaElModo()
    {
        var apertura = new TaskCompletionSource<bool>();
        AbrirDevuelve(apertura.Task);
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.CerrarDibujoAss(); // cambio de episodio o de pista
        apertura.SetResult(true);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void UsarMiEstiloEnAss_AlEncenderlo_VuelveAlTextoPlanoSinCerrarElDibujante_YSeGuarda()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);
        _dibujante.Invocations.Clear();

        sut.UsarMiEstiloEnAss = true;

        sut.SubtitulosAssActivo.Should().BeFalse();
        _dibujante.Verify(d => d.Cerrar(), Times.Never, "volver al estilo original debe ser inmediato");
        _config.UsarMiEstiloEnAss.Should().BeTrue();
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(_config), Times.Once);

        sut.UsarMiEstiloEnAss = false;

        sut.SubtitulosAssActivo.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void Constructor_LeeElAjusteGuardado()
    {
        _config.UsarMiEstiloEnAss = true;

        var sut = Crear();

        sut.UsarMiEstiloEnAss.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_ConUsarMiEstiloEncendido_AbreElDibujantePeroNoActivaElModo()
    {
        _config.UsarMiEstiloEnAss = true;
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void NotificarFalloDibujoAss_ApagaElModoYCierraElDibujante()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);
        _dibujante.Invocations.Clear();

        sut.NotificarFalloDibujoAss();

        sut.SubtitulosAssActivo.Should().BeFalse();
        _dibujante.Verify(d => d.Cerrar(), Times.Once);
        sut.Dispose();
    }

    [Fact]
    public void Dispose_CierraElDibujante()
    {
        var sut = Crear();
        _dibujante.Invocations.Clear();

        sut.Dispose();

        _dibujante.Verify(d => d.Cerrar(), Times.AtLeastOnce);
    }
}
```

- [ ] **Paso 2: comprobar que falla**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ReproductorViewModelSubtitulosAssTests"
```

Esperado: error de compilación (`subtitleAssRenderer`, `IniciarDibujoAss`, `UsarMiEstiloEnAss` no existen).

- [ ] **Paso 3: ajuste y registro en DI**

`AnimeLocalTracker/Models/AppSettings.cs`, debajo de `SubtitulosPorDefecto`:

```csharp
    /// <summary>Con una pista ASS/SSA, mostrar el texto plano con el estilo de Configuración en vez del estilo original
    /// del subtítulo (interruptor "Usar mi estilo" del menú de subtítulos del reproductor).</summary>
    public bool UsarMiEstiloEnAss { get; set; }
```

`AnimeLocalTracker/App.xaml.cs`, debajo de la línea 287 (`AddSingleton<ISubtitleCuesExtractorService, …>`):

```csharp
        services.AddTransient<ISubtitleAssRenderer, SubtitleAssRenderer>(); // uno por reproductor: cada ViewModel cierra el suyo
```

- [ ] **Paso 4: ViewModel**

En `ReproductorViewModel.cs`:

(a) Campo, junto a `_subtitleCuesExtractor` (línea 32):

```csharp
    private readonly ISubtitleAssRenderer _assRenderer;
```

(b) Constructor: nuevo parámetro opcional al final de la lista (tras `IFotogramasClaveService? fotogramasClaveService = null`):

```csharp
        IFotogramasClaveService? fotogramasClaveService = null,
        ISubtitleAssRenderer? subtitleAssRenderer = null)
```

y, junto a `_subtitleCuesExtractor = …` (línea 710):

```csharp
        _assRenderer = subtitleAssRenderer ?? new SubtitleAssRenderer();
```

y, en la carga de ajustes, junto a `_estiloSubtitulos = …` (línea 741):

```csharp
                _usarMiEstiloEnAss = config.UsarMiEstiloEnAss;
```

(c) Bloque nuevo, justo después de `ActualizarLineasSubtitulosSolapados` (~línea 419):

```csharp
    // ── Subtítulos ASS/SSA con su estilo original (ver docs/investigacion-subtitulos-ass.md) ──

    /// <summary>La vista le pide los fotogramas; el ViewModel decide cuándo se abre y se cierra.</summary>
    public ISubtitleAssRenderer DibujanteAss => _assRenderer;

    /// <summary>El dibujante está listo y manda sobre el texto plano: la vista muestra la capa de imagen solo con esto en true.</summary>
    [ObservableProperty] private bool _subtitulosAssActivo;

    /// <summary>La pista elegida es ASS/SSA: el menú de subtítulos ofrece "Usar mi estilo".</summary>
    [ObservableProperty] private bool _pistaActualEsAss;

    private CancellationTokenSource? _assCts;
    private bool _assListo;
    private FlyleafLib.MediaFramework.MediaStream.SubtitlesStream? _pistaTextoActual;

    private bool _usarMiEstiloEnAss;
    /// <summary>Con una pista ASS, ver el texto plano con el estilo de Configuración en vez del original. Se guarda como
    /// preferencia. El dibujante no se cierra al encenderlo, así volver al original es inmediato.</summary>
    public bool UsarMiEstiloEnAss
    {
        get => _usarMiEstiloEnAss;
        set
        {
            if (!SetProperty(ref _usarMiEstiloEnAss, value)) return;

            GuardarUsarMiEstiloEnAss(value);
            if (value) SubtitulosAssActivo = false;
            else if (_assListo) SubtitulosAssActivo = true;
            else IniciarDibujoAssSiCorresponde();
        }
    }

    [RelayCommand]
    private void ToggleUsarMiEstiloEnAss() => UsarMiEstiloEnAss = !UsarMiEstiloEnAss;

    private void GuardarUsarMiEstiloEnAss(bool valor)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.UsarMiEstiloEnAss == valor) return;
        config.UsarMiEstiloEnAss = valor;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    /// <summary>Abre el dibujante para la pista de texto actual si es ASS/SSA incrustada y el usuario no pidió su estilo.</summary>
    private void IniciarDibujoAssSiCorresponde()
    {
        var pista = _pistaTextoActual;
        var player = Player;
        if (pista == null || player == null) return;

        bool esAss = pista.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
        if (!Core.SubtitulosAss.DebeDibujarse(esAss, pista.IsBitmap, pista.ExternalStream != null, UsarMiEstiloEnAss)) return;

        // ponytail: pantalla principal en unidades de WPF. Con la escala de Windows por encima del 100 % se dibuja algo por
        // debajo de los píxeles reales; si se nota borroso, usar los píxeles del monitor donde está la ventana.
        var (ancho, alto) = Core.SubtitulosAss.TamanoDibujo(
            player.Video?.Width ?? 0, player.Video?.Height ?? 0,
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        int indice = Core.SubtitulosAss.IndiceEntreSubtitulos(
            player.Subtitles.Streams.Where(s => s.ExternalStream == null).Select(s => s.StreamIndex), pista.StreamIndex);
        if (ancho == 0 || indice < 0)
        {
            AppLogger.Debug("ReproductorViewModel", $"Dibujo ASS no disponible (tamaño {ancho}x{alto}, pista {indice}): se queda el texto plano.");
            return;
        }

        IniciarDibujoAss(_rutaVideo, indice, ancho, alto);
    }

    internal void IniciarDibujoAss(string ruta, int indice, int ancho, int alto)
    {
        CerrarDibujoAss();

        var cts = new CancellationTokenSource();
        _assCts = cts;
        _ = AbrirDibujoAssAsync(ruta, indice, ancho, alto, cts);
    }

    private async Task AbrirDibujoAssAsync(string ruta, int indice, int ancho, int alto, CancellationTokenSource cts)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            bool listo = await _assRenderer.AbrirAsync(ruta, indice, ancho, alto, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_assCts, cts)) return; // se abrió otra pista/video mientras tanto

            AppLogger.Debug("ReproductorViewModel", $"[Perf] Dibujo ASS {(listo ? "listo" : "no disponible")} en {reloj.ElapsedMilliseconds} ms ({ancho}x{alto}, pista {indice}).");
            _assListo = listo;
            SubtitulosAssActivo = listo && !UsarMiEstiloEnAss;
        }
        catch (OperationCanceledException)
        {
            // Se abrió otra pista/video: el nuevo pedido ya está en curso.
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ReproductorViewModel", $"No se pudo preparar el dibujo ASS: {ex.Message}");
        }
    }

    internal void CerrarDibujoAss()
    {
        _assCts?.Cancel();
        _assCts?.Dispose();
        _assCts = null;

        _assListo = false;
        SubtitulosAssActivo = false;
        _assRenderer.Cerrar();
    }

    /// <summary>La vista avisa de que un fotograma falló: ese episodio sigue en texto plano, sin reintentos.</summary>
    public void NotificarFalloDibujoAss()
    {
        AppLogger.Warn("ReproductorViewModel", "El dibujo ASS falló a mitad de episodio: se vuelve al texto plano.");
        CerrarDibujoAss();
    }
```

(d) `ReiniciarCuesSubtitulos` (línea 310): añadir al final del método:

```csharp
        _pistaTextoActual = null;
        PistaActualEsAss = false;
        CerrarDibujoAss();
```

(e) `AbrirPistaSubtitulos` (línea 328): dentro del `if (pista is … texto)`, entre `IniciarCargaCuesSubtitulos(…)` y `return;`:

```csharp
            _pistaTextoActual = texto;
            PistaActualEsAss = texto.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
            IniciarDibujoAssSiCorresponde();
```

(f) `Dispose` (línea 2643): después de `GC.SuppressFinalize(this);`:

```csharp
        CerrarDibujoAss();
```

- [ ] **Paso 5: comprobar que pasa**

Comandos del paso 2 y después la suite completa:

```
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
```

Esperado: 0 errores, 0 advertencias; las 8 pruebas nuevas y toda la suite en verde.

---

## Tarea 4: capa de imagen, ciclo de dibujo e interruptor

**Archivos:**
- Modificar: `AnimeLocalTracker/Views/ReproductorView.xaml` (raíz del `FlyleafHost` ~318; menú de subtítulos ~755)
- Modificar: `AnimeLocalTracker/Views/ReproductorView.xaml.cs` (`EnlazarSubtitulos` ~68, `VmSubtitulos_PropertyChanged` ~183, `ActualizarTextoSubtitulos` ~254, `ReproductorView_IsVisibleChanged` ~391)
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (~722 español, ~2044 inglés)

**Interfaces:**
- Consume: `SubtitulosAssActivo`, `SubtitulosHabilitados`, `PistaActualEsAss`, `UsarMiEstiloEnAss`, `ToggleUsarMiEstiloEnAssCommand`, `DibujanteAss`, `NotificarFalloDibujoAss()` (tarea 3); `ISubtitleAssRenderer.Ancho/Alto/Renderizar` (tarea 2).

No hay pruebas automáticas de esta tarea (es código de vista sobre la ventana de Flyleaf); se verifica en la tarea 5.

- [ ] **Paso 1: textos**

`grep -n "Player_ApagarSubtitulos" AnimeLocalTracker/Services/LocalizationService.cs` debe dar dos resultados (un diccionario por idioma). Debajo de cada uno:

```csharp
        ["Player_UsarMiEstilo"] = "Usar mi estilo",
```

```csharp
        ["Player_UsarMiEstilo"] = "Use my style",
```

- [ ] **Paso 2: XAML**

(a) Capa de imagen: en la raíz del contenido de `FlyleafHost`, **después** de los dos `Viewbox` de subtítulos y **antes** del comentario de `TapaVideo`:

```xml
                <!-- Subtítulos ASS/SSA con su estilo original: una imagen del tamaño del video que dibuja libass (ver
                     docs/investigacion-subtitulos-ass.md). Se estira igual que el video para que los carteles coincidan con su
                     letrero. Como el texto de arriba, la llena y la muestra el code-behind (ActualizarCapaAss), no un Binding. -->
                <Image x:Name="SubtitulosAssImagen" Stretch="Uniform" IsHitTestVisible="False" Visibility="Collapsed"/>
```

(b) Interruptor: en `SubtitlesPopup`, después del `</ItemsControl>` de la lista de pistas (~línea 755):

```xml
                                        <!-- Usar mi estilo: solo con una pista ASS/SSA. Encendido = texto plano con el estilo de
                                             Configuración; apagado = estilo original del subtítulo. -->
                                        <Button Command="{Binding ToggleUsarMiEstiloEnAssCommand}" Click="SubtitleMenuItem_Click" Style="{StaticResource MenuItemButton}"
                                                Visibility="{Binding PistaActualEsAss, Converter={StaticResource BooleanToVisibilityConverter}}">
                                            <StackPanel Orientation="Horizontal">
                                                <materialDesign:PackIcon Kind="FormatFont" Width="18" Height="18" VerticalAlignment="Center" Margin="0,0,10,0" Foreground="#E5E5E5"/>
                                                <TextBlock Text="{Binding [Player_UsarMiEstilo], Source={x:Static loc:LocalizationService.Instance}, Mode=OneWay}" VerticalAlignment="Center"/>
                                                <materialDesign:PackIcon Kind="Check" Width="16" Height="16" Margin="10,0,0,0" VerticalAlignment="Center" Foreground="#818CF8"
                                                                          Visibility="{Binding UsarMiEstiloEnAss, Converter={StaticResource BooleanToVisibilityConverter}}"/>
                                            </StackPanel>
                                        </Button>
```

- [ ] **Paso 3: code-behind**

En `ReproductorView.xaml.cs`:

(a) Usings, si faltan: `using System.Threading.Tasks;` y `using System.Windows.Media.Imaging;`.

(b) Bloque nuevo, después de `ActualizarTextoSubtitulos` (la fase 0 confirmó `Pbgra32`):

```csharp
        // === Capa de subtítulos ASS (ver SubtitulosAssImagen en el XAML y docs/investigacion-subtitulos-ass.md) ===
        // El ViewModel es el dueño del dibujante; esta vista solo le pide el fotograma del instante actual y lo pinta.
        // Cada instancia de la vista (ventana principal y mini reproductor) tiene su propio bitmap y solo dibuja mientras se ve.
        private WriteableBitmap? _assBitmap;
        private byte[]? _assBufer;
        private bool _assCapaActiva;
        private bool _assDibujando;
        private long _assUltimoTick = -1;

        private void ActualizarCapaAss()
        {
            var vm = _vmSubtitulos;
            bool activa = vm is { SubtitulosAssActivo: true, SubtitulosHabilitados: true } && IsLoaded && IsVisible
                          && vm.DibujanteAss.Ancho > 0 && vm.DibujanteAss.Alto > 0;

            if (_assCapaActiva)
            {
                CompositionTarget.Rendering -= CapaAss_Rendering;
                SubtitulosAssImagen.Visibility = Visibility.Collapsed;
                SubtitulosAssImagen.Source = null;
                _assBitmap = null;
                _assBufer = null;
            }

            _assCapaActiva = activa;
            if (activa)
            {
                var dibujante = vm!.DibujanteAss;
                _assBitmap = new WriteableBitmap(dibujante.Ancho, dibujante.Alto, 96, 96, PixelFormats.Pbgra32, null);
                _assBufer = new byte[dibujante.Ancho * dibujante.Alto * 4];
                _assUltimoTick = -1;
                SubtitulosAssImagen.Source = _assBitmap;
                SubtitulosAssImagen.Visibility = Visibility.Visible;
                CompositionTarget.Rendering += CapaAss_Rendering;
            }

            ActualizarTextoSubtitulos();
        }

        /// <summary>
        /// En cada fotograma de la interfaz: si el video avanzó (o saltó), se pide el dibujo de ese instante a un hilo de fondo y
        /// al volver se copia al bitmap. Mientras hay un dibujo en curso no se pide otro: el siguiente toma el instante más
        /// reciente, así un cartel pesado salta fotogramas del subtítulo en vez de acumular retraso. En pausa el tiempo no cambia
        /// y no se dibuja nada.
        /// </summary>
        private async void CapaAss_Rendering(object? sender, EventArgs e)
        {
            var vm = _vmSubtitulos;
            var player = vm?.Player;
            var bitmap = _assBitmap;
            var bufer = _assBufer;
            if (_assDibujando || vm == null || player == null || bitmap == null || bufer == null) return;

            long tick = player.CurTime;
            if (tick == _assUltimoTick) return;
            _assUltimoTick = tick;

            _assDibujando = true;
            try
            {
                var dibujante = vm.DibujanteAss;
                var instante = TimeSpan.FromTicks(tick);
                var (ok, filaInicial, filas) = await Task.Run(() =>
                {
                    bool dibujado = dibujante.Renderizar(instante, bufer, out int desde, out int cuantas);
                    return (dibujado, desde, cuantas);
                });

                if (!ReferenceEquals(bitmap, _assBitmap)) return; // la capa se apagó o cambió de episodio mientras se dibujaba
                if (!ok)
                {
                    vm.NotificarFalloDibujoAss();
                    return;
                }
                if (filas == 0) return; // nada cambió en pantalla: no se toca el bitmap

                // Solo se copia la franja de filas que cambió (un diálogo son unas decenas de filas, no el fotograma entero).
                int paso = bitmap.PixelWidth * 4;
                bitmap.WritePixels(new Int32Rect(0, filaInicial, bitmap.PixelWidth, filas), bufer, paso, filaInicial * paso);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorView", $"No se pudo pintar la capa de subtítulos ASS: {ex.Message}");
            }
            finally
            {
                _assDibujando = false;
            }
        }
```

(c) `ActualizarTextoSubtitulos`: al principio del método, antes del `if (_vmSubtitulos?.SubtitulosDobleLineaActivo == true)`:

```csharp
            if (_assCapaActiva)
            {
                // La capa ASS ya dibuja estas líneas con su estilo: el texto plano se oculta para no verlas dos veces.
                SubtitulosVista.Texto = string.Empty;
                SubtitulosVistaArriba.Texto = string.Empty;
                return;
            }
```

(d) `VmSubtitulos_PropertyChanged`: al principio del método:

```csharp
            if (e.PropertyName is nameof(ReproductorViewModel.SubtitulosAssActivo) or nameof(ReproductorViewModel.SubtitulosHabilitados))
            {
                if (Dispatcher.CheckAccess()) ActualizarCapaAss();
                else Dispatcher.InvokeAsync(ActualizarCapaAss);
                return;
            }
```

(e) `EnlazarSubtitulos`: el `return` temprano de `ReferenceEquals` impide releer el estado cuando la vista aparece con el mismo ViewModel, así que la llamada va **antes** de ese `return` y también al final:

```csharp
        private void EnlazarSubtitulos(ReproductorViewModel? vm)
        {
            if (ReferenceEquals(_vmSubtitulos, vm))
            {
                ActualizarCapaAss(); // la vista aparece con el modo ASS ya activo (mini reproductor): se lee el estado actual
                return;
            }

            if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged -= VmSubtitulos_PropertyChanged;
            _vmSubtitulos = vm;
            if (_vmSubtitulos != null) _vmSubtitulos.PropertyChanged += VmSubtitulos_PropertyChanged;

            EnlazarSubtitulosDelPlayer(vm?.Player?.Subtitles);
            ActualizarTapaVideo();
            ActualizarCapaAss();
        }
```

(f) `ReproductorView_IsVisibleChanged`: añadir al final del método (fuera del `if`):

```csharp
            ActualizarCapaAss(); // oculta: se deja de dibujar; visible otra vez: se retoma
```

Comprobar si `AppLogger` necesita `using AnimeLocalTracker.Core;` en este archivo (`grep -n "AppLogger" AnimeLocalTracker/Views/ReproductorView.xaml.cs`).

- [ ] **Paso 4: compilar**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
```

Esperado: 0 errores, 0 advertencias.

(La fase 0 midió que `Player.CurTime` ya avanza fotograma a fotograma: no hace falta el paso de reloj interpolado que preveía la versión original.)

---

## Tarea 5: verificación en la app real y cierre

- [ ] **Paso 1: compilación y suite completa (`repo-build-test`)**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
```

Esperado: 0 errores, 0 advertencias; toda la suite en verde; el total sube respecto al inicio con las pruebas nuevas de las tareas 1 a 3.

- [ ] **Paso 2: verificación en vivo (`wpf-visual-verification`)**

Con el episodio de la fase 0, copia de BD/ajustes, sin pasar del 85 %. Una captura por caso:

| # | Caso | Se debe ver |
|---|---|---|
| 1 | Abrir el episodio | Texto plano al principio; a los pocos segundos pasa al estilo original. En el registro: `[Perf] Dibujo ASS listo en … ms` |
| 2 | Cartel posicionado | El cartel sobre su letrero, con su color y fuente; no como línea abajo |
| 3 | Opening con karaoke | El barrido de color avanza con la canción |
| 4 | Saltar adelante y atrás | El subtítulo corresponde al nuevo instante, sin quedarse el anterior |
| 5 | Pausa | El subtítulo se queda fijo y nítido |
| 6 | Menú de subtítulos → "Usar mi estilo" | Cambia al texto plano con el estilo de Configuración; marca visible. Apagarlo vuelve al original al instante |
| 7 | "Apagar" subtítulos y volver a elegir la pista | Desaparece la capa; al volver, reaparece |
| 8 | Pasar a mini reproductor y volver | Los subtítulos ASS siguen en ambas ventanas, sin espera de apertura |
| 9 | "Siguiente" justo al abrir | No aparece un subtítulo del episodio anterior; no hay error en el registro |
| 10 | Episodio con pista SRT y otro hardsub | Igual que antes; el interruptor no aparece |
| 11 | Episodio con PGS (si hay alguno) | Igual que antes |
| 12 | Cambiar el idioma de la app | El texto del interruptor cambia |

Cerrar solo el proceso lanzado, por PID.

- [ ] **Paso 3: actualizar el diseño**

En `docs/investigacion-subtitulos-ass.md`: confirmar o corregir lo que la implementación cambió (resultado de la prueba automática de dibujo, formato de píxel y reloj elegidos) y marcar qué casos del paso 2 se verificaron y cuáles no.

- [ ] **Paso 4: informe al usuario**

Decir qué se verificó con evidencia (salida de build y tests, capturas) y qué no se pudo comprobar. No hacer commit: esperar la orden.
