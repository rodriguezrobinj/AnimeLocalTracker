using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Descargas de openings/endings que no se rompen: mp3 que se quedaron sin pareja cuando AnimeThemes cerró el rango de
/// episodios, descargas cortadas por el servidor, conversiones fallidas y dos descargas del mismo tema a la vez.
/// Carpetas temporales propias: nunca se toca la carpeta de datos del usuario.
/// </summary>
public class AnimeThemesDescargaRobustaTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"AnimeTracker_DescargaRobusta_{Guid.NewGuid():N}");
    private readonly string _musica;
    private readonly string _previas;
    private readonly Mock<IHttpClientFactory> _http = new();

    public AnimeThemesDescargaRobustaTests()
    {
        _musica = Path.Combine(_raiz, "Music");
        _previas = Path.Combine(_raiz, "MusicPreviews");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { /* ignore */ }
    }

    private AnimeThemesDownloadService CrearSut() => new(_http.Object, _musica, _previas) { EsperaEntreIntentos = TimeSpan.Zero };

    private static AnimeThemeInfo Tema(string tipo, string slug, int version, string? rango) =>
        new() { Slug = slug, Tipo = tipo, Version = version, RangoEpisodios = rango, AudioUrlOgg = "https://a.animethemes.moe/x.ogg" };

    private string CrearMp3(int id, string nombre, string contenido = "mp3")
    {
        string ruta = Path.Combine(_musica, id.ToString(), nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    private string[] ArchivosDe(int id) =>
        Directory.Exists(Path.Combine(_musica, id.ToString()))
            ? Directory.GetFiles(Path.Combine(_musica, id.ToString())).Select(f => Path.GetFileName(f)).ToArray()
            : [];

    /// <summary>Responde con el cuerpo dado; si <paramref name="longitudAnunciada"/> es mayor, simula que el servidor corta antes.</summary>
    private sealed class Manejador(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int _llamadas;
        public int Llamadas => _llamadas;
        public TaskCompletionSource? Retener { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _llamadas);
            if (Retener != null) await Retener.Task.WaitAsync(cancellationToken);
            return responder();
        }
    }

    private static HttpResponseMessage Cuerpo(byte[] datos, long? longitudAnunciada = null)
    {
        var contenido = new StreamContent(new MemoryStream(datos));
        contenido.Headers.ContentLength = longitudAnunciada ?? datos.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
    }

    private Manejador UsarManejador(Func<HttpResponseMessage> responder, TaskCompletionSource? retener = null)
    {
        var manejador = new Manejador(responder) { Retener = retener };
        _http.Setup(f => f.CreateClient("Downloader")).Returns(() => new HttpClient(manejador, disposeHandler: false));
        return manejador;
    }

    // === Rango de episodios que cambia (anime que termina su temporada) ===

    [Fact]
    public void Reconciliar_UnMp3ConElRangoViejo_DeberiaRenombrarseYContarComoDescargado()
    {
        // Caso real: Hanaori-san. Descargado en emisión como "ep1-"; AnimeThemes ahora dice "1-10, 12" (v2 en el 11).
        CrearMp3(199066, "OP_OP1_v1_ep1-.mp3", "opening");
        CrearMp3(199066, "ED_ED1_v1_ep1-.mp3", "ending");
        var catalogo = new[] { Tema("OP", "OP1", 1, "1-10, 12"), Tema("OP", "OP1", 2, "11"), Tema("ED", "ED1", 1, "1-12") };
        var sut = CrearSut();

        int renombrados = sut.ReconciliarDescargasLocales(199066, catalogo);

        renombrados.Should().Be(2);
        ArchivosDe(199066).Should().BeEquivalentTo(["OP1.mp3", "ED1.mp3"], "pasan al nombre legible (sin título en este catálogo de prueba)");
        sut.EstaDescargado(199066, catalogo[0]).Should().BeTrue();
        sut.EstaDescargado(199066, catalogo[2]).Should().BeTrue();
        sut.EstaDescargado(199066, catalogo[1]).Should().BeFalse("la versión 2 es otra canción distinta");
        File.ReadAllText(sut.ObtenerRutaLocalEsperada(199066, catalogo[0])).Should().Be("opening", "se renombra, no se vuelve a bajar");
    }

    [Fact]
    public void Reconciliar_SiYaExisteElArchivoConElNombreNuevo_NoDeberiaPisarlo()
    {
        CrearMp3(7, "OP_OP1_v1_ep1-.mp3", "viejo");
        CrearMp3(7, "OP1.mp3", "nuevo");

        CrearSut().ReconciliarDescargasLocales(7, [Tema("OP", "OP1", 1, "1-12")]).Should().Be(0);

        File.ReadAllText(Path.Combine(_musica, "7", "OP1.mp3")).Should().Be("nuevo");
        ArchivosDe(7).Should().Contain("OP_OP1_v1_ep1-.mp3", "no se borra nada del usuario");
    }

    [Fact]
    public void Reconciliar_ArchivosDeTemasQueYaNoEstanEnElCatalogo_NoDeberiaTocarlos()
    {
        CrearMp3(7, "OP_OP9_v1_ep1-.mp3");
        CrearMp3(7, "notas.mp3");

        CrearSut().ReconciliarDescargasLocales(7, [Tema("OP", "OP1", 1, "1-12")]).Should().Be(0);

        ArchivosDe(7).Should().BeEquivalentTo("OP_OP9_v1_ep1-.mp3", "notas.mp3");
    }

    [Fact]
    public void Reconciliar_SinCarpetaOSinCatalogo_NoDeberiaFallar()
    {
        var sut = CrearSut();
        sut.ReconciliarDescargasLocales(7, [Tema("OP", "OP1", 1, "1-12")]).Should().Be(0);
        CrearMp3(7, "OP_OP1_v1_ep1-.mp3");
        sut.ReconciliarDescargasLocales(7, []).Should().Be(0);
        ArchivosDe(7).Should().ContainSingle();
    }

    // === Descargas cortadas y fallos ===

    [Fact]
    public async Task Descargar_SiElServidorCortaAntesDeTiempo_DeberiaReintentarUnaVezYNoDejarNadaComoDescargado()
    {
        var manejador = UsarManejador(() => Cuerpo(new byte[100], longitudAnunciada: 5000));
        var tema = Tema("OP", "OP1", 1, "1-12");
        var sut = CrearSut();

        var ruta = await sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);

        ruta.Should().BeNull();
        manejador.Llamadas.Should().Be(2, "un corte de red se reintenta una vez");
        sut.EstaDescargado(7, tema).Should().BeFalse();
        ArchivosDe(7).Should().BeEmpty("ni el .ogg.tmp ni un mp3 a medias deben quedar");
    }

    [Fact]
    public async Task Descargar_ConUnErrorQueNoVaACambiar_NoDeberiaReintentar()
    {
        var manejador = UsarManejador(() => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ruta = await CrearSut().DescargarYConvertirAsync(7, Tema("OP", "OP1", 1, null), null, CancellationToken.None);

        ruta.Should().BeNull();
        manejador.Llamadas.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void EsFalloDeRedPasajero_SoloReintentaLoQuePuedeArreglarseSolo(HttpStatusCode codigo, bool esperado)
    {
        AnimeThemesDownloadService.EsFalloDeRedPasajero(new HttpRequestException("x", null, codigo)).Should().Be(esperado);
        AnimeThemesDownloadService.EsFalloDeRedPasajero(new IOException("corte")).Should().BeTrue();
        AnimeThemesDownloadService.EsFalloDeRedPasajero(new HttpRequestException("sin conexión")).Should().BeTrue();
    }

    [Fact]
    public async Task Descargar_SiFfmpegNoPuedeConvertir_NoDeberiaDejarUnMp3AMedias()
    {
        UsarManejador(() => Cuerpo(Enumerable.Repeat((byte)0x42, 4096).ToArray()));
        var tema = Tema("OP", "OP1", 1, "1-12");
        var sut = CrearSut();

        var ruta = await sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);

        ruta.Should().BeNull();
        sut.EstaDescargado(7, tema).Should().BeFalse();
        ArchivosDe(7).Should().BeEmpty("ni .mp3 ni .mp3.part ni .ogg.tmp");
    }

    [Fact]
    public async Task Descargar_UnOggValido_DeberiaDejarSoloElMp3Final()
    {
        byte[] ogg = GenerarOggDePrueba();
        UsarManejador(() => Cuerpo(ogg));
        var tema = Tema("OP", "OP1", 1, "1-12");
        var sut = CrearSut();
        var avances = new System.Collections.Concurrent.ConcurrentBag<double>();

        var ruta = await sut.DescargarYConvertirAsync(7, tema, new Progress<double>(avances.Add), CancellationToken.None);

        ruta.Should().Be(sut.ObtenerRutaLocalEsperada(7, tema));
        new FileInfo(ruta!).Length.Should().BeGreaterThan(0);
        ArchivosDe(7).Should().BeEquivalentTo(tema.NombreArchivoLegible());
    }

    // === La misma descarga pedida dos veces (salir y volver a la ficha) ===

    [Fact]
    public async Task Descargar_DosVecesALaVez_DeberiaBajarUnaSolaVezYAmbosRecibenElResultado()
    {
        var retener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manejador = UsarManejador(() => new HttpResponseMessage(HttpStatusCode.NotFound), retener);
        var tema = Tema("OP", "OP1", 1, "1-12");
        var sut = CrearSut();

        var primera = sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);
        sut.EstaDescargando(7, tema).Should().BeTrue();
        var segunda = sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);
        retener.SetResult();

        var resultados = await Task.WhenAll(primera, segunda);

        resultados.Should().AllSatisfy(r => r.Should().BeNull());
        manejador.Llamadas.Should().Be(1, "la segunda petición se une a la que ya estaba en marcha");
        sut.EstaDescargando(7, tema).Should().BeFalse();
    }

    [Fact]
    public async Task Descargar_QuienSeUneTarde_DeberiaVerElAvanceActual()
    {
        var retener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UsarManejador(() => new HttpResponseMessage(HttpStatusCode.NotFound), retener);
        var tema = Tema("OP", "OP1", 1, null);
        var sut = CrearSut();
        var primera = sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);

        var avisos = new System.Collections.Concurrent.ConcurrentBag<double>();
        var segunda = sut.DescargarYConvertirAsync(7, tema, new SincronoProgress(avisos.Add), CancellationToken.None);
        avisos.Should().Contain(0, "al unirse recibe enseguida por dónde va");

        retener.SetResult();
        await Task.WhenAll(primera, segunda);
    }

    [Fact]
    public void EstaDescargando_SinNadaEnMarcha_DeberiaSerFalso()
    {
        CrearSut().EstaDescargando(7, Tema("OP", "OP1", 1, null)).Should().BeFalse();
    }

    // === El .ogg que ya bajó el salto de openings/endings ===

    [Fact]
    public async Task Descargar_SiElSaltoDeOpeningsYaTieneElOgg_DeberiaConvertirloSinUsarLaRed()
    {
        // La caché de referencias lo guardó con el rango de entonces ("1-"); hoy AnimeThemes dice "1-12". Es la misma canción.
        string referencias = Path.Combine(_raiz, "SkipReferences");
        string ogg = Path.Combine(referencias, "7", "OP_OP1_v1_ep1-.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(ogg)!);
        File.WriteAllBytes(ogg, GenerarOggDePrueba());
        var sut = new AnimeThemesDownloadService(_http.Object, _musica, _previas, referencias);
        var tema = Tema("OP", "OP1", 1, "1-12");

        var ruta = await sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);

        ruta.Should().Be(sut.ObtenerRutaLocalEsperada(7, tema));
        new FileInfo(ruta!).Length.Should().BeGreaterThan(0);
        _http.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
        File.Exists(ogg).Should().BeTrue("la caché del salto de openings no se toca");
    }

    [Fact]
    public async Task VistaPrevia_SiElSaltoDeOpeningsYaTieneElOgg_TampocoDeberiaUsarLaRed()
    {
        string referencias = Path.Combine(_raiz, "SkipReferences");
        string ogg = Path.Combine(referencias, "7", "ED_ED1_v1_eptodos.ogg");
        Directory.CreateDirectory(Path.GetDirectoryName(ogg)!);
        File.WriteAllBytes(ogg, GenerarOggDePrueba());
        var sut = new AnimeThemesDownloadService(_http.Object, _musica, _previas, referencias);

        var ruta = await sut.PrepararVistaPreviaAsync(7, Tema("ED", "ED1", 1, null), null, CancellationToken.None);

        ruta.Should().NotBeNull().And.StartWith(_previas);
        _http.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void BuscarOggEnReferencias_NoDeberiaConfundirOtraVersionNiOtroAnime()
    {
        string referencias = Path.Combine(_raiz, "SkipReferences");
        Directory.CreateDirectory(Path.Combine(referencias, "7"));
        Directory.CreateDirectory(Path.Combine(referencias, "8"));
        File.WriteAllText(Path.Combine(referencias, "7", "OP_OP1_v2_ep11.ogg"), "x");
        File.WriteAllText(Path.Combine(referencias, "8", "OP_OP1_v1_eptodos.ogg"), "x");
        var sut = new AnimeThemesDownloadService(_http.Object, _musica, _previas, referencias);

        sut.BuscarOggEnReferencias(7, Tema("OP", "OP1", 1, null)).Should().BeNull();
        sut.BuscarOggEnReferencias(7, Tema("OP", "OP1", 2, "11")).Should().EndWith("OP_OP1_v2_ep11.ogg");
    }

    [Fact]
    public void BuscarOggEnReferencias_ConCarpetaDeMusicaDePruebaYSinReferencias_NoDeberiaMirarLaCarpetaReal()
    {
        CrearSut().BuscarOggEnReferencias(7, Tema("OP", "OP1", 1, null)).Should().BeNull();
    }

    // === Etiquetas y portada del mp3 ===

    [Fact]
    public void ArgumentosConversion_ConPortada_IncrustaLaImagenYLasEtiquetas()
    {
        var tema = new AnimeThemeInfo
        {
            Slug = "ED20", Tipo = "ED", Version = 1, RangoEpisodios = "492", TituloCancion = "Memories", Artistas = "Maki Otsuki",
            NombreAnime = "One Piece", Notas = "OP as ED", AudioUrlOgg = "https://a.animethemes.moe/x.ogg"
        };

        var args = AnimeThemesDownloadService.ArgumentosConversion("in.ogg", "out.mp3.part", tema, "portada.jpg");

        string.Join(" ", args).Should().Contain("-i in.ogg -i portada.jpg -map 0:a:0 -map 1:v:0")
            .And.Contain("attached_pic").And.Contain("-id3v2_version 3").And.EndWith("-f mp3 out.mp3.part");
        args.Should().Contain(["title=Memories", "artist=Maki Otsuki", "album=One Piece", "genre=Anime"]);
        args.Should().Contain(a => a.StartsWith("comment=ED20 v1") && a.Contains("492") && a.Contains("OP as ED"));
    }

    [Fact]
    public void ArgumentosConversion_SinPortadaNiDatos_SoloAudioYElSlugComoTitulo()
    {
        var args = AnimeThemesDownloadService.ArgumentosConversion("in.ogg", "out.part", Tema("OP", "OP1", 1, null), null);

        args.Should().NotContain("1:v:0").And.NotContain("attached_pic");
        args.Count(a => a == "-i").Should().Be(1);
        args.Should().Contain("title=OP1").And.NotContain(a => a.StartsWith("artist=") || a.StartsWith("album="));
    }

    [Fact]
    public async Task Descargar_ConPortada_ElMp3LlevaEtiquetasEImagen()
    {
        string portadas = Path.Combine(_raiz, "Covers");
        Directory.CreateDirectory(portadas);
        GenerarConFfmpeg(Path.Combine(portadas, "7.jpg"), "-f", "lavfi", "-i", "color=c=purple:s=120x180", "-frames:v", "1");
        UsarManejador(() => Cuerpo(GenerarOggDePrueba()));
        var tema = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", TituloCancion = "Yuusha", Artistas = "YOASOBI", NombreAnime = "Frieren", AudioUrlOgg = "https://a.animethemes.moe/x.ogg" };
        var sut = new AnimeThemesDownloadService(_http.Object, _musica, _previas, carpetaPortadas: portadas) { EsperaEntreIntentos = TimeSpan.Zero };

        var ruta = await sut.DescargarYConvertirAsync(7, tema, null, CancellationToken.None);

        ruta.Should().NotBeNull();
        string info = Sondear(ruta!);
        info.Should().Contain("title=Yuusha").And.Contain("artist=YOASOBI").And.Contain("album=Frieren");
        info.Should().Contain("codec_type=video", "la portada va incrustada");
    }

    [Fact]
    public async Task Descargar_ConUnaPortadaRota_SeGuardaIgualSinImagen()
    {
        string portadas = Path.Combine(_raiz, "Covers");
        Directory.CreateDirectory(portadas);
        File.WriteAllText(Path.Combine(portadas, "7.jpg"), "esto no es una imagen");
        UsarManejador(() => Cuerpo(GenerarOggDePrueba()));
        var sut = new AnimeThemesDownloadService(_http.Object, _musica, _previas, carpetaPortadas: portadas) { EsperaEntreIntentos = TimeSpan.Zero };

        var ruta = await sut.DescargarYConvertirAsync(7, Tema("OP", "OP1", 1, null), null, CancellationToken.None);

        ruta.Should().NotBeNull("la canción no se pierde por la imagen");
        Sondear(ruta!).Should().NotContain("codec_type=video");
        ArchivosDe(7).Should().ContainSingle();
    }

    private static string Sondear(string ruta)
    {
        var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffprobe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type:format_tags=title,artist,album", "-of", "compact", ruta })
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        string salida = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return salida.Replace("tag:", "");
    }

    private static readonly string[] ArgumentosSilencio = ["-y", "-hide_banner", "-loglevel", "error"];

    private static void GenerarConFfmpeg(string salida, params string[] entrada)
    {
        var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in ArgumentosSilencio.Concat(entrada).Append(salida)) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
    }

    private sealed class SincronoProgress(Action<double> accion) : IProgress<double>
    {
        public void Report(double value) => accion(value);
    }

    /// <summary>Un .ogg Opus de 1 s de silencio generado con el ffmpeg embebido (el mismo que usa la conversión).</summary>
    private byte[] GenerarOggDePrueba()
    {
        string ruta = Path.Combine(_raiz, "fuente.ogg");
        Directory.CreateDirectory(_raiz);
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", "-c:a", "libopus", ruta })
            psi.ArgumentList.Add(arg);

        using var proceso = Process.Start(psi)!;
        proceso.StandardOutput.ReadToEnd();
        proceso.StandardError.ReadToEnd();
        proceso.WaitForExit(30_000);
        File.Exists(ruta).Should().BeTrue("hace falta el ffmpeg embebido para esta prueba");
        return File.ReadAllBytes(ruta);
    }
}
