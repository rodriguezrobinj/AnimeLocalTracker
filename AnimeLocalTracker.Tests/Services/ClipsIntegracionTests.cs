using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Clips;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Clips con el ffmpeg embebido de verdad: un video negro de 20 s con un subtítulo ASS interno ("HOLA", de 10 a 12 s), guardado
/// con un nombre de fansub lleno de caracteres que el filtro de subtítulos interpreta. Todo en una carpeta temporal.
/// </summary>
public sealed class ClipsIntegracionTests : IAsyncLifetime
{
    private const string NombreDificil = "[SubsPlease] Prueba - 14 (1080p) [AB'C,D;E].mkv";
    private static readonly TimeSpan Limite = TimeSpan.FromMinutes(1);

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "clips-pruebas-" + Guid.NewGuid().ToString("N"));
    private string _video = string.Empty;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_carpeta);
        string ass = Path.Combine(_carpeta, "subs.ass");
        await File.WriteAllTextAsync(ass, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Arial,120,&H00FFFFFF,&H00FFFFFF,&H00FFFFFF,&H00FFFFFF,1,0,0,0,100,100,0,0,1,4,0,5,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,HOLA
            """);

        _video = Path.Combine(_carpeta, NombreDificil);
        var r = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, new[]
        {
            "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=24:d=20",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-i", ass,
            "-map", "0:v", "-map", "1:a", "-map", "2:s", "-t", "20",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "ass", _video,
        }, Limite, CancellationToken.None);
        r!.Codigo.Should().Be(0, r.Error);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private ClipService Servicio() => new(Path.Combine(_carpeta, "salida"));

    private SolicitudClip Clip(FormatoClip formato, double inicio, double duracion, string nombre)
        => new(_video, inicio, duracion, formato, IndiceAudio: 1, new SubtitulosClip(_video, 0), Path.Combine(_carpeta, "salida", nombre));

    [Fact]
    public async Task Mp4_ConSubtituloIncrustadoEnSuMomento_YPorDebajoDe10MB()
    {
        // Ojo con los nombres: "con.mp4" sería el dispositivo de consola de Windows (CON), no un archivo.
        string? conSub = await Servicio().CrearAsync(Clip(FormatoClip.Mp4, 10, 2, "con-subtitulo.mp4"), CancellationToken.None);
        string? sinSub = await Servicio().CrearAsync(Clip(FormatoClip.Mp4, 0, 2, "sin-subtitulo.mp4"), CancellationToken.None);

        conSub.Should().NotBeNull();
        sinSub.Should().NotBeNull();
        new FileInfo(conSub!).Length.Should().BeLessThan(10L * 1024 * 1024);
        (await Duracion(conSub!)).Should().BeApproximately(2, 0.3);
        (await Brillo(conSub!)).Should().BeGreaterThan(await Brillo(sinSub!), "el clip de 10 a 12 s debe llevar \"HOLA\" incrustado");
    }

    [Fact]
    public async Task Gif_480DeAncho()
    {
        string? gif = await Servicio().CrearAsync(Clip(FormatoClip.Gif, 10, 2, "clip.gif"), CancellationToken.None);

        gif.Should().NotBeNull();
        (await Sonda(gif!, "stream=width")).Should().Be("480");
    }

    [Fact]
    public async Task NombreOcupado_SeCreaConSufijo()
    {
        var servicio = Servicio();
        string? primero = await servicio.CrearAsync(Clip(FormatoClip.Mp4, 0, 1, "repetido.mp4"), CancellationToken.None);
        string? segundo = await servicio.CrearAsync(Clip(FormatoClip.Mp4, 0, 1, "repetido.mp4"), CancellationToken.None);

        Path.GetFileName(primero).Should().Be("repetido.mp4");
        Path.GetFileName(segundo).Should().Be("repetido (2).mp4");
    }

    [Fact]
    public async Task VideoQueNoExiste_DevuelveNull()
    {
        var solicitud = Clip(FormatoClip.Mp4, 0, 1, "nada.mp4") with { RutaVideo = Path.Combine(_carpeta, "no-existe.mkv"), Subtitulos = null };

        (await Servicio().CrearAsync(solicitud, CancellationToken.None)).Should().BeNull();
    }

    private static async Task<string> Sonda(string archivo, string entradas)
    {
        var r = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe,
            new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", entradas, "-of", "csv=p=0", archivo }, Limite, CancellationToken.None);
        return r!.Salida.Trim();
    }

    private static async Task<double> Duracion(string archivo)
    {
        var r = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe,
            new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", archivo }, Limite, CancellationToken.None);
        return double.Parse(r!.Salida.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Brillo medio (0–255) del fotograma del medio segundo: el video es negro, solo el subtítulo aporta luz.</summary>
    private static async Task<int> Brillo(string archivo)
    {
        var r = await ProcesoExterno.EjecutarBinarioAsync(FfmpegLocator.Ffmpeg, new[]
        {
            "-hide_banner", "-nostdin", "-ss", "0.5", "-i", archivo, "-frames:v", "1", "-vf", "scale=1:1:flags=area", "-f", "rawvideo", "-pix_fmt", "gray", "-",
        }, Limite, CancellationToken.None);
        r!.Salida.Should().NotBeEmpty();
        return r.Salida[0];
    }
}
