using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Extras del panel de música de la ficha: "Descargar todos" (con confirmación, 2 a la vez, resumen único y cancelación),
/// temas con spoiler tapados, tamaño antes de descargar, abrir la carpeta y el indicador "sonando ahora".
/// </summary>
public sealed class DetalleMusicaExtrasTests : IDisposable
{
    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _downloadService = new();
    private readonly FakeAudioTrackPlayer _player = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private static AnimeThemeInfo Tema(string tipo, string slug, string? rango = null, bool spoiler = false, long? tamano = 2_621_440) =>
        new() { Slug = slug, Tipo = tipo, TituloCancion = "Canción " + slug, Artistas = "Artista", RangoEpisodios = rango, EsSpoiler = spoiler,
                AudioUrlOgg = $"https://a.animethemes.moe/{slug}.ogg", TamanoBytes = tamano };

    private async Task<DetalleViewModel> AbrirFichaAsync(AnimeItem? anime = null, params AnimeThemeInfo[] temas)
    {
        anime ??= new AnimeItem { AniListId = 7, Titulo = "Frieren" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _downloadService.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _themes.Setup(s => s.ObtenerTemasAsync(anime.AniListId, It.IsAny<CancellationToken>())).ReturnsAsync(temas.ToList());

        var sut = new DetalleViewModel(
            _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _downloadService.Object,
            animeThemesService: _themes.Object, animeThemesDownload: _descargas.Object, audioTrackPlayer: _player);
        await sut.InicializarAsync(anime);
        await sut.CargarTemasMusicalesAsync();
        return sut;
    }

    private void Confirmar(bool respuesta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(respuesta);

    // === Descargar todos ===

    [Fact]
    public async Task DescargarTodos_AlConfirmar_BajaLosQueFaltanYAvisaUnaSolaVez()
    {
        var op1 = Tema("OP", "OP1"); var op2 = Tema("OP", "OP2"); var ed1 = Tema("ED", "ED1");
        _descargas.Setup(d => d.EstaDescargado(7, op2)).Returns(true);
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, AnimeThemeInfo t, IProgress<double>? _, CancellationToken _) => @"C:\Music\7\" + t.NombreArchivoLocal());
        Confirmar(true);
        var sut = await AbrirFichaAsync(null, op1, op2, ed1);

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains('2') && m.Contains('5')), true, It.IsAny<string>(), It.IsAny<string>()),
            Times.Once, "pregunta antes, con cuántos son (2) y cuánto pesan (2 × 2,5 MB = 5,0 MB)");
        _descargas.Verify(d => d.DescargarYConvertirAsync(7, op2, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.TemasMusicales.Should().OnlyContain(t => t.Descargado && !t.EnCola && !t.Descargando);
        sut.DescargandoTodos.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DescargarTodos_SiNoSeConfirma_NoBajaNada()
    {
        Confirmar(false);
        var sut = await AbrirFichaAsync(null, Tema("OP", "OP1"));

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.TemasMusicales.Single().EnCola.Should().BeFalse();
    }

    [Fact]
    public async Task DescargarTodos_RespetaElFiltro_SoloLosEndingsSiSeEstaViendoEndings()
    {
        var op = Tema("OP", "OP1"); var ed = Tema("ED", "ED1");
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync("x.mp3");
        Confirmar(true);
        var sut = await AbrirFichaAsync(null, op, ed);
        sut.CambiarFiltroTemasCommand.Execute("Endings");

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.DescargarYConvertirAsync(7, ed, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _descargas.Verify(d => d.DescargarYConvertirAsync(7, op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DescargarTodos_ConAlgunFallo_ElResumenLoDiceSinUnAvisoPorCancion()
    {
        var ok = Tema("OP", "OP1"); var mal1 = Tema("OP", "OP2"); var mal2 = Tema("ED", "ED1");
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, ok, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync("ok.mp3");
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, mal1, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, mal2, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        Confirmar(true);
        var sut = await AbrirFichaAsync(null, ok, mal1, mal2);

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.Is<string>(m => m.Contains('1') && m.Contains('3') && m.Contains('2')), It.IsAny<string>(), "#F59E0B"), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DescargarTodos_NuncaBajaMasDeDosALaVez()
    {
        int enCurso = 0, maximo = 0;
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                int ahora = Interlocked.Increment(ref enCurso);
                lock (this) maximo = Math.Max(maximo, ahora);
                await Task.Delay(30);
                Interlocked.Decrement(ref enCurso);
                return "x.mp3";
            });
        Confirmar(true);
        var sut = await AbrirFichaAsync(null, Enumerable.Range(1, 6).Select(i => Tema("OP", "OP" + i)).ToArray());

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        maximo.Should().Be(DetalleViewModel.DescargasSimultaneasTodos);
        sut.TemasMusicales.Should().OnlyContain(t => t.Descargado);
    }

    [Fact]
    public async Task DescargarTodos_PulsarloOtraVez_CancelaLoQueQueda()
    {
        var puerta = new TaskCompletionSource();
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, AnimeThemeInfo _, IProgress<double>? _, CancellationToken ct) =>
            {
                try { await puerta.Task.WaitAsync(ct); return "x.mp3"; }
                catch (OperationCanceledException) { return null; }
            });
        Confirmar(true);
        var sut = await AbrirFichaAsync(null, Enumerable.Range(1, 5).Select(i => Tema("OP", "OP" + i)).ToArray());

        var descarga = sut.DescargarTodosCommand.ExecuteAsync(null);
        await Task.Delay(50);
        sut.DescargandoTodos.Should().BeTrue();
        sut.MostrarProgresoDescargaTodos.Should().BeTrue();
        sut.TemasMusicales.Count(t => t.EsperandoEnCola).Should().Be(3, "2 bajando y 3 esperando turno");

        await sut.DescargarTodosCommand.ExecuteAsync(null); // mismo botón: cancelar
        await descarga;

        sut.DescargandoTodos.Should().BeFalse();
        sut.TemasMusicales.Should().OnlyContain(t => !t.Descargado && !t.EnCola && !t.Descargando);
        _descargas.Verify(d => d.DescargarYConvertirAsync(7, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), "CloseCircleOutline", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DescargarTodos_SinNadaPendiente_SoloAvisa()
    {
        var op = Tema("OP", "OP1");
        _descargas.Setup(d => d.EstaDescargado(7, op)).Returns(true);
        var sut = await AbrirFichaAsync(null, op);

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "#10B981"), Times.Once);
    }

    [Fact]
    public async Task DescargarTodos_SinTamanoConocido_PreguntaSoloPorCuantos()
    {
        Confirmar(false);
        var sut = await AbrirFichaAsync(null, Tema("OP", "OP1", tamano: null), Tema("OP", "OP2"));

        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.Is<string>(m => !m.Contains("MB")), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // === Spoilers ===

    [Theory]
    [InlineData(false, "13-24", 5, "CURRENT", false)] // no es spoiler
    [InlineData(true, "13-24", 5, "CURRENT", true)]   // aún no llega
    [InlineData(true, "13-24", 12, "CURRENT", true)]
    [InlineData(true, "13-24", 13, "CURRENT", false)] // ya lo vio
    [InlineData(true, "13-24", 5, "COMPLETED", false)]
    [InlineData(true, null, 5, "CURRENT", true)]      // sin rango: mejor taparlo
    [InlineData(true, "1163", 1200, "CURRENT", false)]
    public void DebeOcultarSpoiler_SegunLoQueHaVistoElUsuario(bool spoiler, string? rango, int vistos, string estado, bool esperado)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "x", EpisodiosVistos = vistos, TotalEpisodios = 24, EstadoUsuario = estado };

        DetalleViewModel.DebeOcultarSpoiler(Tema("ED", "ED9", rango, spoiler), anime).Should().Be(esperado);
    }

    [Fact]
    public void DebeOcultarSpoiler_CuentaElEpisodioMasAvanzado_NoCuantosLleva()
    {
        // Empezó en el 1000: lleva 180 vistos, pero ya pasó por el 1163.
        var anime = new AnimeItem { AniListId = 21, Titulo = "One Piece", EpisodiosVistos = 180, TotalEpisodios = 1200, EstadoUsuario = "CURRENT" };
        var ed27 = Tema("ED", "ED27", "1163", spoiler: true);

        DetalleViewModel.DebeOcultarSpoiler(ed27, anime).Should().BeTrue();
        DetalleViewModel.DebeOcultarSpoiler(ed27, anime, episodioMasAltoVisto: 1180).Should().BeFalse();
    }

    [Fact]
    public async Task UnTemaConSpoiler_SeTapaHastaQueElUsuarioLoPide()
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", EpisodiosVistos = 3, TotalEpisodios = 28, EstadoUsuario = "CURRENT" };
        var sut = await AbrirFichaAsync(anime, Tema("ED", "ED2", "28", spoiler: true), Tema("OP", "OP1", "1-28"));
        var ed = sut.TemasMusicales.Single(t => t.Slug == "ED2");

        ed.OcultoPorSpoiler.Should().BeTrue();
        ed.TituloVisible.Should().NotContain("ED2").And.NotBe(ed.TituloCancion);
        ed.ArtistasVisible.Should().NotBe("Artista");
        sut.TemasMusicales.Single(t => t.Slug == "OP1").OcultoPorSpoiler.Should().BeFalse();

        sut.RevelarSpoilerTemaCommand.Execute(ed);

        ed.TituloVisible.Should().Be("Canción ED2");
        ed.ArtistasVisible.Should().Be("Artista");
    }

    // === Tamaño, carpeta y "sonando ahora" ===

    [Fact]
    public async Task ElTamano_SoloSeMuestraMientrasNoEstaGuardado()
    {
        var sut = await AbrirFichaAsync(null, Tema("OP", "OP1", tamano: 2_621_440), Tema("OP", "OP2", tamano: null));
        var conTamano = sut.TemasMusicales[0];

        conTamano.MostrarTamano.Should().BeTrue();
        conTamano.TamanoTexto.Should().MatchRegex(@"^2[.,]5 MB$");
        sut.TemasMusicales[1].MostrarTamano.Should().BeFalse("AnimeThemes no dio el tamaño");

        conTamano.Descargado = true;
        conTamano.MostrarTamano.Should().BeFalse();
    }

    [Fact]
    public async Task AbrirCarpetaMusica_AbreLaDelAnimeSiExisteYSiNoLaDeMusica()
    {
        string raiz = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AnimeTracker_CarpetaMusica_" + Guid.NewGuid().ToString("N"));
        try
        {
            string carpetaAnime = System.IO.Path.Combine(raiz, "7");
            _descargas.Setup(d => d.CarpetaDescargas(7)).Returns(carpetaAnime);
            var sut = await AbrirFichaAsync(null, Tema("OP", "OP1"));
            var abiertas = new List<string>();
            sut.AbrirCarpetaEnExplorador = abiertas.Add;

            sut.AbrirCarpetaMusicaCommand.Execute(null);
            System.IO.Directory.CreateDirectory(carpetaAnime);
            sut.AbrirCarpetaMusicaCommand.Execute(null);

            abiertas.Should().Equal(raiz, carpetaAnime);
        }
        finally
        {
            try { System.IO.Directory.Delete(raiz, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task MusicaSonando_SigueAlReproductor()
    {
        var op = Tema("OP", "OP1");
        _descargas.Setup(d => d.EstaDescargado(7, op)).Returns(true);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(7, op)).Returns(@"C:\Music\7\OP_OP1_v1_eptodos.mp3");
        var sut = await AbrirFichaAsync(null, op);
        var item = sut.TemasMusicales.Single();

        sut.ReproducirTemaCommand.Execute(item);
        sut.MusicaSonando.Should().BeTrue();

        sut.ReproducirTemaCommand.Execute(item); // pausa
        sut.MusicaSonando.Should().BeFalse();

        sut.ReproducirTemaCommand.Execute(item); // reanuda
        sut.DetenerMusica();
        sut.MusicaSonando.Should().BeFalse();
    }
}
