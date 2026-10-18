using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Clips;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class ClipsTests
{
    private static SolicitudClip Solicitud(FormatoClip formato = FormatoClip.Mp4, int audio = 1, SubtitulosClip? subs = null,
        double inicio = 725, double duracion = 26)
        => new(@"C:\Anime\Frieren\Episodio 14.mkv", inicio, duracion, formato, audio, subs, @"C:\Salida\clip.mp4");

    private static string Valor(IReadOnlyList<string> args, string opcion) => args[args.ToList().IndexOf(opcion) + 1];

    // ---- Tasa de bits ----

    [Fact]
    public void TasaVideoKbps_30Segundos_CabeEn10MB()
        => ArgumentosClip.TasaVideoKbps(30).Should().Be(2528);

    [Fact]
    public void TasaVideoKbps_ClipCorto_SeLimitaA5000()
        => ArgumentosClip.TasaVideoKbps(2).Should().Be(5000);

    // ---- MP4 ----

    [Fact]
    public void Construir_Mp4_RecortaAntesDeAbrirYLimitaLaDuracion()
    {
        var args = ArgumentosClip.Construir(Solicitud());

        args.ToList().IndexOf("-ss").Should().BeLessThan(args.ToList().IndexOf("-i"));
        args.ToList().IndexOf("-t").Should().BeLessThan(args.ToList().IndexOf("-i"), "la duración limita la LECTURA del episodio");
        Valor(args, "-ss").Should().Be("725");
        Valor(args, "-i").Should().Be(@"C:\Anime\Frieren\Episodio 14.mkv");
        Valor(args, "-t").Should().Be("26");
        args[^1].Should().Be(@"C:\Salida\clip.mp4");
    }

    [Fact]
    public void Construir_Mp4_UsaLaPistaDeAudioQueSeOye()
    {
        var args = ArgumentosClip.Construir(Solicitud(audio: 3));

        args.Should().ContainInConsecutiveOrder("-map", "0:3");
        args.Should().ContainInConsecutiveOrder("-map", "0:v:0");
    }

    [Fact]
    public void Construir_Mp4_SinPistaDeAudioConocida_UsaLaPrimeraSiHay()
        => ArgumentosClip.Construir(Solicitud(audio: -1)).Should().ContainInConsecutiveOrder("-map", "0:a:0?");

    [Fact]
    public void Construir_Mp4_TasaCalculadaYCompatible()
    {
        var args = ArgumentosClip.Construir(Solicitud(duracion: 30));

        Valor(args, "-c:v").Should().Be("libx264");
        Valor(args, "-b:v").Should().Be("2528k");
        Valor(args, "-maxrate").Should().Be("2528k");
        Valor(args, "-bufsize").Should().Be("5056k");
        Valor(args, "-pix_fmt").Should().Be("yuv420p");
        Valor(args, "-c:a").Should().Be("aac");
        args.Should().Contain("-sn");
        Valor(args, "-movflags").Should().Be("+faststart");
    }

    [Fact]
    public void Construir_Mp4_SinSubtitulos_SoloEscala()
        => Valor(ArgumentosClip.Construir(Solicitud()), "-vf").Should().Be("scale=-2:'min(720,ih)'");

    [Fact]
    public void Construir_Mp4_ConSubtitulosInternos_LosDibujaEnSuMomentoYLuegoEscala()
    {
        var args = ArgumentosClip.Construir(Solicitud(subs: new SubtitulosClip(@"C:\Anime\Frieren\Episodio 14.mkv", 1)));

        Valor(args, "-vf").Should().Be(
            @"setpts=PTS+725/TB,subtitles=C\\:/Anime/Frieren/Episodio 14.mkv:si=1,setpts=PTS-STARTPTS,scale=-2:'min(720,ih)'");
    }

    [Fact]
    public void Construir_Mp4_ConSubtitulosExternos_SinIndice()
    {
        var args = ArgumentosClip.Construir(Solicitud(subs: new SubtitulosClip(@"C:\Subs\ep14.ass", null)));

        Valor(args, "-vf").Should().Contain(@"subtitles=C\\:/Subs/ep14.ass,").And.NotContain(":si=");
    }

    [Fact]
    public void Construir_InicioConDecimales_UsaPuntoDecimal()
        => Valor(ArgumentosClip.Construir(Solicitud(inicio: 725.5, duracion: 12.25)), "-ss").Should().Be("725.5");

    // ---- GIF ----

    [Fact]
    public void Construir_Gif_PaletaEnDosPasadasSinAudio()
    {
        var args = ArgumentosClip.Construir(Solicitud(FormatoClip.Gif, duracion: 8));

        Valor(args, "-filter_complex").Should().Be(
            "[0:v]fps=15,scale=480:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];"
            + "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
        args.Should().Contain("-an");
        args.Should().ContainInConsecutiveOrder("-loop", "0");
        args.Should().NotContain("-b:v");
    }

    [Fact]
    public void Construir_Gif_LaDuracionCortaLaLecturaDelEpisodio()
    {
        // palettegen entrega la paleta al acabar su entrada: con -t como opción de SALIDA ffmpeg leía el resto del episodio
        // (24 min) antes de escribir un GIF de 4 s, y llegaba al límite de 2 minutos.
        var args = ArgumentosClip.Construir(Solicitud(FormatoClip.Gif, duracion: 4)).ToList();

        args.IndexOf("-t").Should().BeLessThan(args.IndexOf("-i"));
        args.Should().ContainInConsecutiveOrder("-t", "4", "-i");
    }

    [Fact]
    public void Construir_Gif_ConSubtitulos_LosDibujaAntesDeReducir()
        => Valor(ArgumentosClip.Construir(Solicitud(FormatoClip.Gif, subs: new SubtitulosClip(@"C:\v.mkv", 0), duracion: 8)), "-filter_complex")
            .Should().StartWith(@"[0:v]setpts=PTS+725/TB,subtitles=C\\:/v.mkv:si=0,setpts=PTS-STARTPTS,fps=15,");

    // ---- Escape de rutas en el filtro ----

    [Fact]
    public void EscaparRutaFiltro_NombreDeFansubDificil()
        => ArgumentosClip.EscaparRutaFiltro(@"C:\a\[x] b'c,d;e.mkv").Should().Be(@"C\\:/a/\[x\] b\\\'c\,d\;e.mkv");

    // ---- Rango ----

    [Fact]
    public void RangoValido_FinAntesDelInicio_SeIntercambian()
        => ArgumentosClip.RangoValido(30, 20, FormatoClip.Mp4).Should().Be(new RangoClip(20, 10, Recortado: false));

    [Fact]
    public void RangoValido_Mp4MasLargoQue30_SeRecorta()
        => ArgumentosClip.RangoValido(100, 145, FormatoClip.Mp4).Should().Be(new RangoClip(100, 30, Recortado: true));

    [Fact]
    public void RangoValido_GifMasLargoQue10_NoVale()
        => ArgumentosClip.RangoValido(100, 111, FormatoClip.Gif).Should().BeNull();

    [Fact]
    public void RangoValido_Gif10Segundos_Vale()
        => ArgumentosClip.RangoValido(100, 110, FormatoClip.Gif).Should().Be(new RangoClip(100, 10, Recortado: false));

    [Fact]
    public void RangoValido_MenosDeUnSegundo_NoVale()
        => ArgumentosClip.RangoValido(100, 100.5, FormatoClip.Mp4).Should().BeNull();

    // ---- Índice de subtítulos para el filtro ----

    [Fact]
    public void IndiceRelativoSubtitulos_CuentaSoloLasInternas()
    {
        var pistas = new List<PistaSubtitulosMotor>
        {
            new(2, "es", null, false, true, null),
            new(3, "en", null, false, true, null),
            new(9, "es", null, false, true, @"C:\Subs\externo.ass"),
            new(5, "pt", null, false, false, null),
        };

        ArgumentosClip.IndiceRelativoSubtitulos(pistas, pistas[3]).Should().Be(2);
        ArgumentosClip.IndiceRelativoSubtitulos(pistas, pistas[0]).Should().Be(0);
    }

    [Fact]
    public void IndiceRelativoSubtitulos_Externa_SinIndice()
    {
        var externa = new PistaSubtitulosMotor(9, "es", null, false, true, @"C:\Subs\externo.ass");

        ArgumentosClip.IndiceRelativoSubtitulos(new[] { externa }, externa).Should().BeNull();
    }

    // ---- Carpeta de los clips ----

    [Fact]
    public void CarpetaPorDefecto_ConCarpetaVideos_LaUsa()
        => ClipService.CarpetaPorDefecto(@"C:\Users\Ana\Videos", @"C:\Users\Ana", @"D:\Datos")
            .Should().Be(@"C:\Users\Ana\Videos\AnimeLocalTracker");

    [Fact]
    public void CarpetaPorDefecto_SinCarpetaVideos_UsaLaDelPerfil()
        // Windows puede no dar la carpeta Vídeos (perfil redirigido): una ruta vacía escribía el clip junto al programa.
        => ClipService.CarpetaPorDefecto("", @"C:\Users\Ana", @"D:\Datos").Should().Be(@"C:\Users\Ana\Videos\AnimeLocalTracker");

    [Fact]
    public void CarpetaPorDefecto_SinVideosNiPerfil_UsaLosDatosDeLaApp()
        => ClipService.CarpetaPorDefecto("", "", @"D:\Datos").Should().Be(@"D:\Datos\Clips");

    // ---- Nombre del archivo ----

    [Fact]
    public void NombreArchivo_TituloEpisodioYMinuto()
        => ArgumentosClip.NombreArchivo("Frieren: Beyond Journey's End", 14, 725, FormatoClip.Mp4)
            .Should().Be("Frieren_ Beyond Journey's End - Ep 14 - 12-05.mp4");

    [Fact]
    public void NombreArchivo_ConHoras_YGif()
        => ArgumentosClip.NombreArchivo("One Piece", 1100, 3725, FormatoClip.Gif).Should().Be("One Piece - Ep 1100 - 1-02-05.gif");

    [Fact]
    public void NombreArchivo_CaracteresProhibidos()
        => ArgumentosClip.NombreArchivo("Re:Zero? <Kara>/\"Hajimeru\"*|", 1, 0, FormatoClip.Mp4)
            .Should().Be("Re_Zero_ _Kara___Hajimeru___ - Ep 1 - 00-00.mp4");
}
