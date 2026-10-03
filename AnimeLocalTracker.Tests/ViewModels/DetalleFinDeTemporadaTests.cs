using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
/// Cuando ya salió el último episodio y no queda ninguno programado, la ficha no debe seguir enseñando una cuenta atrás ni un
/// "En emisión" caducado: oculta el contador y revalida el estado en AniList (como mucho una vez cada 15 min).
/// </summary>
public class DetalleFinDeTemporadaTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IProximaEmisionService> _proxima = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"ficha_fin_temporada_{Guid.NewGuid():N}");

    public DetalleFinDeTemporadaTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private DetalleViewModel CrearSut() => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        proximaEmision: _proxima.Object);

    private static ProximaEmision YaEmitido(int episodio) => new(episodio, DateTime.UtcNow.AddMinutes(-5));
    private static ProximaEmision AunNoSale(int episodio) => new(episodio, DateTime.UtcNow.AddDays(2));

    private async Task<DetalleViewModel> AbrirFichaAsync(ProximaEmision? proxima)
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _proxima.Setup(s => s.ObtenerAsync(7, "RELEASING", It.IsAny<bool>())).ReturnsAsync(proxima);

        var sut = CrearSut();
        await sut.InicializarAsync(new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "RELEASING", TotalEpisodios = 12 });
        await EsperarAsync(() => _proxima.Invocations.Count > 0);
        await Task.Delay(150); // la carga de la cuenta atrás va en segundo plano: no hay condición positiva que esperar
        return sut;
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (int i = 0; i < 50 && !condicion(); i++) await Task.Delay(20);
    }

    private static Task InvocarCargarProximaEmisionAsync(DetalleViewModel sut) =>
        (Task)typeof(DetalleViewModel).GetMethod("CargarProximaEmisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(sut, [CancellationToken.None])!;

    private static void PonerProximaEmision(DetalleViewModel sut, ProximaEmision? valor) =>
        typeof(DetalleViewModel).GetField("_proximaEmisionActual", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sut, valor);

    // === El contador ===

    [Fact]
    public void PasadaLaHoraDeEmision_ElContadorSeOculta()
    {
        var sut = CrearSut();
        PonerProximaEmision(sut, AunNoSale(13));
        sut.ActualizarContadorProximo();
        sut.TieneContadorProximo.Should().BeTrue("todavía falta para el episodio");

        PonerProximaEmision(sut, YaEmitido(13));
        sut.ActualizarContadorProximo();

        sut.TieneContadorProximo.Should().BeFalse("ya salió y no hay nada que contar");
    }

    // === El estado de emisión ===

    [Fact]
    public async Task SiYaSalioElUltimoEpisodioYAniListDiceFinished_ActualizaElEstadoYOcultaElContador()
    {
        _tracking.Setup(t => t.ObtenerAnimePorIdAsync(7)).ReturnsAsync(new AniListMedia { Status = "FINISHED" });

        var sut = await AbrirFichaAsync(YaEmitido(12));
        await EsperarAsync(() => sut.AnimeSeleccionado!.Estado == "FINISHED");

        sut.AnimeSeleccionado!.Estado.Should().Be("FINISHED");
        sut.AnimeSeleccionado.EstadoVisual.Should().Be(LocalizationService.T("Media_EstadoFinalizado"));
        sut.TieneContadorProximo.Should().BeFalse();
        _db.Verify(d => d.ActualizarAnimeAsync(It.Is<AnimeItem>(a => a.Estado == "FINISHED")), Times.Once);
    }

    [Fact]
    public async Task SiAniListSigueDiciendoReleasing_NoCambiaNadaNiGuarda()
    {
        _tracking.Setup(t => t.ObtenerAnimePorIdAsync(7)).ReturnsAsync(new AniListMedia { Status = "RELEASING" });

        var sut = await AbrirFichaAsync(YaEmitido(12));

        sut.AnimeSeleccionado!.Estado.Should().Be("RELEASING");
        _db.Verify(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
    }

    [Fact]
    public async Task SiTodaviaHayUnEpisodioPorSalir_NoPreguntaPorElEstado()
    {
        await AbrirFichaAsync(AunNoSale(13));

        _tracking.Verify(t => t.ObtenerAnimePorIdAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task LaRevalidacionNoSeRepiteEnCadaTick()
    {
        _tracking.Setup(t => t.ObtenerAnimePorIdAsync(7)).ReturnsAsync(new AniListMedia { Status = "RELEASING" });
        var sut = await AbrirFichaAsync(YaEmitido(12));

        await InvocarCargarProximaEmisionAsync(sut);
        await InvocarCargarProximaEmisionAsync(sut);

        _tracking.Verify(t => t.ObtenerAnimePorIdAsync(7), Times.Once);
    }
}
