using System;
using System.Collections.Generic;
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
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Audio de referencia de OP/ED sin pasos manuales: Ficha → caché propia → descarga de AnimeThemes (carpeta temporal, nunca AppDataPaths).</summary>
public class ReferenciasAudioServiceTests : IDisposable
{
    private static readonly byte[] Ogg = [0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0, 1, 2, 3, 4];

    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly List<string> _urls = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeTracker_Referencias_" + Guid.NewGuid().ToString("N"));
    private readonly ReferenciasAudioService _sut;

    private Func<HttpResponseMessage> _responder = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Ogg) };

    public ReferenciasAudioServiceTests()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((peticion, _) =>
            {
                _urls.Add(peticion.RequestUri!.ToString());
                return Task.FromResult(_responder());
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler.Object));

        _descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());

        _sut = new ReferenciasAudioService(_themes.Object, _descargas.Object, factory.Object, _carpeta);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private static AnimeThemeInfo Tema(string tipo, string slug, string? rango = null, string url = "https://a.animethemes.moe/x.ogg") =>
        new() { Slug = slug, Tipo = tipo, Version = 1, RangoEpisodios = rango, AudioUrlOgg = url };

    private void Catalogo(params AnimeThemeInfo[] temas) =>
        _themes.Setup(t => t.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(temas.ToList());

    [Fact]
    public async Task BajaElAudioDeLosTemasQueAplicanAlEpisodio_ALaCacheDeReferencias()
    {
        Catalogo(Tema("OP", "OP1", "1-12", "https://a.animethemes.moe/op1.ogg"), Tema("ED", "ED1", "1-12", "https://a.animethemes.moe/ed1.ogg"));

        var r = await _sut.ObtenerAsync(7, 5);

        r.Completa.Should().BeTrue();
        r.Temas.Should().HaveCount(2);
        r.Temas.Should().OnlyContain(t => File.Exists(t.RutaArchivo) && t.RutaArchivo.EndsWith(".ogg") && t.RutaArchivo.StartsWith(_carpeta));
        File.ReadAllBytes(r.Temas[0].RutaArchivo).Should().Equal(Ogg);
        Directory.GetFiles(_carpeta, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task NoBajaLosTemasQueNoAplicanAlEpisodio()
    {
        Catalogo(Tema("OP", "OP1", "1-12", "https://a.animethemes.moe/op1.ogg"), Tema("OP", "OP2", "13-24", "https://a.animethemes.moe/op2.ogg"));

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().ContainSingle().Which.Slug.Should().Be("OP1");
        _urls.Should().ContainSingle().Which.Should().EndWith("op1.ogg");
        r.Completa.Should().BeTrue("el OP2 no aplica a este episodio, no cuenta como faltante");
    }

    [Fact]
    public async Task ReutilizaLoQueYaSeDescargoDesdeLaFicha_SinBajarNada()
    {
        var delaFicha = new TemaLocalDisponible("OP", "OP1", 1, "1-12", @"C:\Music\7\OP_OP1_v1_ep1-12.mp3");
        _descargas.Setup(d => d.ListarDescargasLocales(7)).Returns(new List<TemaLocalDisponible> { delaFicha });
        Catalogo(Tema("OP", "OP1", "1-12"));

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().ContainSingle().Which.Should().Be(delaFicha);
        _urls.Should().BeEmpty();
        r.Completa.Should().BeTrue();
    }

    [Fact]
    public async Task UnArchivoDeLaFichaConElRangoViejo_NoSeVuelveABajarYUsaElRangoActual()
    {
        // Descargado mientras se emitía ("1-"); al terminar, AnimeThemes cerró el rango en "1-12". Es la misma canción.
        var delaFicha = new TemaLocalDisponible("OP", "OP1", 1, "1-", @"C:\Music\7\OP_OP1_v1_ep1-.mp3");
        _descargas.Setup(d => d.ListarDescargasLocales(7)).Returns(new List<TemaLocalDisponible> { delaFicha });
        Catalogo(Tema("OP", "OP1", "1-12"));

        var r = await _sut.ObtenerAsync(7, 5);

        _urls.Should().BeEmpty();
        var tema = r.Temas.Should().ContainSingle().Subject;
        tema.RutaArchivo.Should().Be(delaFicha.RutaArchivo);
        tema.RangoEpisodios.Should().Be("1-12");
        tema.AplicaAlEpisodio(13).Should().BeFalse();
    }

    [Fact]
    public async Task UnAudioQueLlegaCortado_NoSeGuardaEnLaCache()
    {
        _responder = () =>
        {
            var contenido = new StreamContent(new MemoryStream(Ogg));
            contenido.Headers.ContentLength = Ogg.Length * 10L;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
        };
        Catalogo(Tema("OP", "OP1", "1-12"));

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().BeEmpty();
        r.Completa.Should().BeFalse();
        (Directory.Exists(_carpeta) ? Directory.GetFiles(_carpeta, "*", SearchOption.AllDirectories) : []).Should().BeEmpty();
    }

    [Fact]
    public async Task ReutilizaLaCacheDeUnaVezAnterior_SinVolverAPedirElAudio()
    {
        Catalogo(Tema("OP", "OP1", "1-12", "https://a.animethemes.moe/op1.ogg"));
        await _sut.ObtenerAsync(7, 5);
        _urls.Clear();

        var r = await _sut.ObtenerAsync(7, 6);

        r.Temas.Should().ContainSingle();
        _urls.Should().BeEmpty("el audio ya está en la caché");
    }

    [Fact]
    public async Task SinRed_DevuelveLoQueYaHayEnDiscoYMarcaIncompleta()
    {
        Catalogo(Tema("OP", "OP1", "1-12", "https://a.animethemes.moe/op1.ogg"));
        await _sut.ObtenerAsync(7, 5);
        _themes.Setup(t => t.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("sin red"));

        var r = await _sut.ObtenerAsync(7, 6);

        r.Temas.Should().ContainSingle("lo que ya estaba en la caché sigue sirviendo sin conexión");
        r.Completa.Should().BeFalse();
    }

    [Fact]
    public async Task AnimeSinTemasEnAnimeThemes_NoEsCompleta()
    {
        Catalogo();

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().BeEmpty();
        r.Completa.Should().BeFalse("sin catálogo no se sabe si faltan temas: se reintentará más adelante");
    }

    [Fact]
    public async Task SiLaDescargaFalla_LaReferenciaNoSeAgregaYQuedaIncompleta()
    {
        Catalogo(Tema("OP", "OP1", null, "https://a.animethemes.moe/op1.ogg"));
        _responder = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().BeEmpty();
        r.Completa.Should().BeFalse();
        Directory.Exists(_carpeta).Should().BeTrue();
        Directory.GetFiles(_carpeta, "*", SearchOption.AllDirectories).Should().BeEmpty("no queda ni un archivo a medias");
    }

    [Theory]
    [InlineData("https://evil.example.com/op.ogg")]
    [InlineData("http://a.animethemes.moe/op.ogg")]
    [InlineData("https://animethemes.moe.evil.com/op.ogg")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("")]
    public async Task RechazaUrlsQueNoSonHttpsDeAnimeThemes(string url)
    {
        Catalogo(Tema("OP", "OP1", null, url));

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().BeEmpty();
        _urls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://a.animethemes.moe/x.ogg", true)]
    [InlineData("https://animethemes.moe/x.ogg", true)]
    [InlineData("https://ANIMETHEMES.MOE/x.ogg", true)]
    [InlineData("https://notanimethemes.moe/x.ogg", false)]
    public void EsUrlPermitida_SoloElDominioDeAnimeThemes(string url, bool esperado) =>
        ReferenciasAudioService.EsUrlPermitida(url).Should().Be(esperado);

    [Fact]
    public async Task RechazaAudiosDemasiadoGrandes()
    {
        Catalogo(Tema("OP", "OP1", null, "https://a.animethemes.moe/op1.ogg"));
        _responder = () =>
        {
            var contenido = new ByteArrayContent(Ogg);
            contenido.Headers.ContentLength = ReferenciasAudioService.MaximoBytesAudio + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
        };

        var r = await _sut.ObtenerAsync(7, 5);

        r.Temas.Should().BeEmpty();
    }

    [Fact]
    public async Task LaCancelacionSePropaga()
    {
        Catalogo(Tema("OP", "OP1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> f = () => _sut.ObtenerAsync(7, 5, cts.Token);

        await f.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConLaCacheLlena_BorraLosAudiosMenosUsadosYConservaElNuevo()
    {
        _sut.MaximoBytesCache = Ogg.Length * 3;
        Directory.CreateDirectory(Path.Combine(_carpeta, "1"));
        for (int i = 1; i <= 4; i++)
        {
            string vieja = Path.Combine(_carpeta, "1", $"OP_OP{i}_v1_eptodos.ogg");
            File.WriteAllBytes(vieja, Ogg);
            File.SetLastWriteTimeUtc(vieja, DateTime.UtcNow.AddDays(-10 + i));
            File.SetLastAccessTimeUtc(vieja, DateTime.UtcNow.AddDays(-10 + i));
        }
        Catalogo(Tema("OP", "OP9", null, "https://a.animethemes.moe/op9.ogg"));

        var r = await _sut.ObtenerAsync(7, 5);

        File.Exists(r.Temas.Single(t => t.Slug == "OP9").RutaArchivo).Should().BeTrue("el recién bajado nunca se poda");
        long total = Directory.GetFiles(_carpeta, "*.ogg", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        total.Should().BeLessThanOrEqualTo(Ogg.Length * 3);
        File.Exists(Path.Combine(_carpeta, "1", "OP_OP1_v1_eptodos.ogg")).Should().BeFalse("el más antiguo se borra primero");
    }

    [Fact]
    public async Task UsaLaCacheAunqueElNombreTengaRangosConEspacios()
    {
        Catalogo(Tema("OP", "OP1", "1-14, 16", "https://a.animethemes.moe/op1.ogg"));
        await _sut.ObtenerAsync(7, 5);
        _urls.Clear();

        var r = await _sut.ObtenerAsync(7, 16);

        r.Temas.Should().ContainSingle();
        _urls.Should().BeEmpty("el rango '1-14, 16' y el del archivo ('1-14,16') son el mismo tema");
    }
}
