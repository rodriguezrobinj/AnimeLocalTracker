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

/// <summary>Openings/endings en la Ficha (AnimeThemes.moe): catálogo, descarga/conversión a mp3 y su
/// estado en la lista. La reproducción de preview (MediaPlayer/WPF) no se cubre aquí: no hay
/// audio real en el entorno de pruebas y ReproducirTema ya valida null/no-descargado antes de tocarlo.</summary>
public class DetalleMusicaTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IAnimeThemesService> _themesService = new();
    private readonly Mock<IAnimeThemesDownloadService> _themesDownload = new();

    private DetalleViewModel CrearSut() => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        animeThemesService: _themesService.Object, animeThemesDownload: _themesDownload.Object);

    private async Task<DetalleViewModel> AbrirFichaAsync(int aniListId = 7)
    {
        var anime = new AnimeItem { AniListId = aniListId, Titulo = "Frieren" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = CrearSut();
        await sut.InicializarAsync(anime);
        return sut;
    }

    [Fact]
    public void TieneTemasMusicales_SinTemas_DeberiaSerFalso()
    {
        CrearSut().TieneTemasMusicales.Should().BeFalse();
    }

    [Fact]
    public async Task CargarTemasMusicalesAsync_DeberiaPoblarLaListaYMarcarLosYaDescargados()
    {
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", TituloCancion = "Yuusha", Artistas = "YOASOBI", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        var ed = new AnimeThemeInfo { Slug = "ED1", Tipo = "ED", TituloCancion = "Anytime Anywhere", AudioUrlOgg = "https://a.animethemes.moe/ed.ogg" };
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op, ed });
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(true);
        _themesDownload.Setup(d => d.EstaDescargado(7, ed)).Returns(false);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();

        sut.TemasMusicales.Should().HaveCount(2);
        sut.TieneTemasMusicales.Should().BeTrue();
        sut.TemasMusicales.Single(t => t.Slug == "OP1").Descargado.Should().BeTrue();
        sut.TemasMusicales.Single(t => t.Slug == "ED1").Descargado.Should().BeFalse();
    }

    [Fact]
    public async Task CargarTemasMusicalesAsync_DeberiaPonerAlDiaLosNombresAntesDeMirarQueEstaDescargado()
    {
        // Un mp3 guardado con el rango viejo ("1-") se renombra primero; si se mirara antes, saldría como no descargado.
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", RangoEpisodios = "1-12", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        var temas = new List<AnimeThemeInfo> { op };
        bool reconciliado = false;
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(temas);
        _themesDownload.Setup(d => d.ReconciliarDescargasLocales(7, temas)).Callback(() => reconciliado = true).Returns(1);
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(() => reconciliado);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();

        sut.TemasMusicales.Single().Descargado.Should().BeTrue();
    }

    [Fact]
    public async Task CargarTemasMusicalesAsync_SinListaDeAnimeThemes_DeberiaMostrarLosMp3YaGuardadosSoloParaEscuchar()
    {
        // Sin conexión y sin lista guardada: los mp3 que ya tienes siguen sonando desde la ficha.
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        _themesDownload.Setup(d => d.ListarDescargasLocales(7)).Returns(new List<TemaLocalDisponible>
        {
            new("ED", "ED1", 1, "1-12", @"C:\Music\7\ED_ED1_v1_ep1-12.mp3"),
            new("OP", "OP1", 1, null, @"C:\Music\7\OP_OP1_v1_eptodos.mp3")
        });
        _themesDownload.Setup(d => d.EstaDescargado(7, It.IsAny<AnimeThemeInfo>())).Returns(true);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();

        sut.TemasMusicales.Select(t => t.Slug).Should().Equal("OP1", "ED1");
        sut.TemasMusicales.Should().OnlyContain(t => t.Descargado && t.PuedeReproducir && !t.PuedeDescargar && !t.PuedePrevisualizar);
        sut.TemasMusicales.Single(t => t.Slug == "OP1").TituloCancion.Should().Be("OP1", "sin AnimeThemes no se sabe el título");
        _themesDownload.Verify(d => d.ReconciliarDescargasLocales(It.IsAny<int>(), It.IsAny<IReadOnlyList<AnimeThemeInfo>>()), Times.Never);
    }

    [Fact]
    public async Task UnTemaSinEnlace_NoDeberiaIntentarDescargarse()
    {
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        _themesDownload.Setup(d => d.ListarDescargasLocales(7)).Returns(new List<TemaLocalDisponible>
        {
            new("OP", "OP1", 1, null, @"C:\Music\7\OP_OP1_v1_eptodos.mp3")
        });
        _themesDownload.Setup(d => d.EstaDescargado(7, It.IsAny<AnimeThemeInfo>())).Returns(false); // lo borró desde la ficha

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();
        await sut.DescargarTemaCommand.ExecuteAsync(sut.TemasMusicales.Single());

        _themesDownload.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.TemasMusicales.Single().PuedeDescargar.Should().BeFalse();
    }

    [Fact]
    public async Task CargarTemasMusicalesAsync_ConUnaDescargaAunEnMarcha_DeberiaVolverAMostrarSuAvanceSinAvisarDosVeces()
    {
        // Se empezó a descargar, se salió de la ficha y se volvió: la fila nueva se engancha a la misma descarga.
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        var termina = new TaskCompletionSource<string?>();
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op });
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(false);
        _themesDownload.Setup(d => d.EstaDescargando(7, op)).Returns(true);
        _themesDownload.Setup(d => d.DescargarYConvertirAsync(7, op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(termina.Task);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();
        var item = sut.TemasMusicales.Single();

        item.Descargando.Should().BeTrue();
        termina.SetResult(null);
        await Task.Delay(50);

        item.Descargando.Should().BeFalse();
        item.Descargado.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "el aviso de fallo ya lo da quien empezó la descarga");
    }

    [Fact]
    public async Task DescargarTemaAsync_ConExito_DeberiaMarcarComoDescargadoYaLimpiarElIndicadorDeProgreso()
    {
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op });
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(false);
        _themesDownload.Setup(d => d.DescargarYConvertirAsync(7, op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(@"C:\Music\7\OP_OP1_v1_eptodos.mp3");

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();
        var item = sut.TemasMusicales.Single();

        await sut.DescargarTemaCommand.ExecuteAsync(item);

        item.Descargado.Should().BeTrue();
        item.Descargando.Should().BeFalse();
    }

    [Fact]
    public async Task DescargarTemaAsync_ConFallo_DeberiaAvisarPorToastYNoMarcarComoDescargado()
    {
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", TituloCancion = "Yuusha", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op });
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(false);
        _themesDownload.Setup(d => d.DescargarYConvertirAsync(7, op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();
        var item = sut.TemasMusicales.Single();

        await sut.DescargarTemaCommand.ExecuteAsync(item);

        item.Descargado.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task EliminarTema_DeberiaBorrarElArchivoYMarcarComoNoDescargado()
    {
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op });
        _themesDownload.Setup(d => d.EstaDescargado(7, op)).Returns(true);

        var sut = await AbrirFichaAsync();
        await sut.CargarTemasMusicalesAsync();
        var item = sut.TemasMusicales.Single();

        sut.EliminarTemaCommand.Execute(item);

        item.Descargado.Should().BeFalse();
        _themesDownload.Verify(d => d.Eliminar(7, op), Times.Once);
    }

    [Fact]
    public async Task InicializarAsync_AlCambiarDeAnime_DeberiaLimpiarLosTemasDeLaFichaAnterior()
    {
        var op = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };
        _themesService.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op });
        _themesDownload.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);

        var sut = await AbrirFichaAsync(aniListId: 7);
        await sut.CargarTemasMusicalesAsync();
        sut.TemasMusicales.Should().HaveCount(1);

        // Navegar a otro anime sin temas conocidos: la lista de la ficha anterior no debe seguir ahí.
        _themesService.Setup(s => s.ObtenerTemasAsync(8, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        await sut.InicializarAsync(new AnimeItem { AniListId = 8, Titulo = "Otro anime" });

        sut.TemasMusicales.Should().BeEmpty();
        sut.MostrandoPanelMusica.Should().BeFalse();
    }
}
