using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// La lista de capítulos de la ficha debe añadir sola la fila de un episodio recién emitido (sin pulsar "Actualizar"),
/// reutilizando el mismo dato que ya usa la cuenta atrás (<see cref="IEmisionMonitorService.UltimoEmitido"/>): así queda
/// en línea con lo que ya muestran Actualizaciones/Galería, sin ninguna consulta nueva a AniList.
/// </summary>
public class DetalleUltimoEmitidoTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IProximaEmisionService> _proxima = new();
    private readonly Mock<IEmisionMonitorService> _monitor = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"ficha_ultimo_emitido_{Guid.NewGuid():N}");

    public DetalleUltimoEmitidoTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private DetalleViewModel CrearSut(IEmisionMonitorService? monitor) => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        proximaEmision: _proxima.Object, monitorEmision: monitor);

    private AnimeItem Anime(int totalEpisodios) =>
        new() { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "RELEASING", TotalEpisodios = totalEpisodios };

    /// <summary>Abre la ficha de un anime en emisión con <paramref name="episodiosLocales"/> capítulos ya conocidos (sin archivos).</summary>
    private async Task<DetalleViewModel> AbrirFichaAsync(int episodiosLocales, ProximaEmision? proxima, int ultimoEmitido)
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _proxima.Setup(s => s.ObtenerAsync(7, "RELEASING", It.IsAny<bool>())).ReturnsAsync(proxima);
        _monitor.Setup(m => m.UltimoEmitido(proxima, It.Is<AnimeItem>(a => a.AniListId == 7), It.IsAny<DateTime>())).Returns(ultimoEmitido);

        var sut = CrearSut(_monitor.Object);
        await sut.InicializarAsync(Anime(episodiosLocales));
        await EsperarAsync(() => sut.Episodios.EpisodiosDelAnime.Count >= Math.Max(episodiosLocales, ultimoEmitido));
        return sut;
    }

    /// <summary>El aviso del episodio nuevo llega por un fire-and-forget dentro de InicializarAsync: se sondea la condición
    /// hasta 1 s en vez de asumir un tiempo fijo.</summary>
    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (int i = 0; i < 50 && !condicion(); i++) await Task.Delay(20);
    }

    private static ProximaEmision YaEmitido(int episodio) => new(episodio, DateTime.UtcNow.AddMinutes(-5));
    private static ProximaEmision AunNoSale(int episodio) => new(episodio, DateTime.UtcNow.AddDays(2));

    [Fact]
    public async Task AlAbrirLaFicha_SiYaSalioUnEpisodioQueNoEstabaEnLaLista_LoAñade()
    {
        var sut = await AbrirFichaAsync(episodiosLocales: 12, proxima: YaEmitido(13), ultimoEmitido: 13);

        sut.Episodios.EpisodiosDelAnime.Should().Contain(e => e.NumeroEpisodio == 13);
        sut.AnimeSeleccionado!.TotalEpisodios.Should().Be(13);
        _db.Verify(d => d.ActualizarAnimeAsync(It.Is<AnimeItem>(a => a.TotalEpisodios == 13)), Times.Once);
    }

    [Fact]
    public async Task ElEpisodioNuevo_SeAñadeSinDescargarYSinMarcarComoVisto()
    {
        var sut = await AbrirFichaAsync(episodiosLocales: 12, proxima: YaEmitido(13), ultimoEmitido: 13);

        var nuevo = sut.Episodios.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 13);
        nuevo.Descargado.Should().BeFalse();
        nuevo.Visto.Should().BeFalse();
    }

    [Fact]
    public async Task SiElEpisodioTodaviaNoSale_NoAñadeNadaDeMas()
    {
        var sut = await AbrirFichaAsync(episodiosLocales: 12, proxima: AunNoSale(13), ultimoEmitido: 12);

        sut.Episodios.EpisodiosDelAnime.Should().NotContain(e => e.NumeroEpisodio == 13);
        _db.Verify(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
    }

    [Fact]
    public async Task SiSaltaMasDeUnEpisodio_AñadeTodosLosQueFaltan()
    {
        var sut = await AbrirFichaAsync(episodiosLocales: 10, proxima: YaEmitido(13), ultimoEmitido: 13);

        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().Contain([11, 12, 13]);
    }

    [Fact]
    public async Task SinMonitorDeEmision_NoFallaYNoAñadeNada()
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        _proxima.Setup(s => s.ObtenerAsync(7, "RELEASING", It.IsAny<bool>())).ReturnsAsync(YaEmitido(13));
        var sut = CrearSut(monitor: null);

        await sut.InicializarAsync(Anime(12));
        await Task.Delay(200); // sin monitor la sincronización nunca hace nada: no hay condición que esperar

        sut.Episodios.EpisodiosDelAnime.Should().HaveCount(12);
    }

    [Fact]
    public async Task SiGuardarFalla_LaFilaQuedaVisibleIgual()
    {
        _db.Setup(d => d.ActualizarAnimeAsync(It.IsAny<AnimeItem>())).ThrowsAsync(new InvalidOperationException("disco lleno"));

        var sut = await AbrirFichaAsync(episodiosLocales: 12, proxima: YaEmitido(13), ultimoEmitido: 13);

        sut.Episodios.EpisodiosDelAnime.Should().Contain(e => e.NumeroEpisodio == 13, "el usuario ya la ve aunque el guardado en BD falle");
    }

    [Fact]
    public async Task AlLlegarLaHoraDeEmisionMientrasLaFichaEstaAbierta_LaCuentaAtrasAñadeLaFilaSola()
    {
        var sut = await AbrirFichaAsync(episodiosLocales: 12, proxima: AunNoSale(13), ultimoEmitido: 12);
        sut.Episodios.EpisodiosDelAnime.Should().NotContain(e => e.NumeroEpisodio == 13);

        // La cuenta atrás vuelve a preguntar (esto es lo que hace su temporizador interno tras la hora de emisión).
        _proxima.Setup(s => s.ObtenerAsync(7, "RELEASING", It.IsAny<bool>())).ReturnsAsync(YaEmitido(13));
        _monitor.Setup(m => m.UltimoEmitido(It.IsAny<ProximaEmision?>(), It.Is<AnimeItem>(a => a.AniListId == 7), It.IsAny<DateTime>())).Returns(13);
        await InvocarCargarProximaEmisionAsync(sut);

        sut.Episodios.EpisodiosDelAnime.Should().Contain(e => e.NumeroEpisodio == 13);
    }

    /// <summary>El refresco periódico lo dispara un DispatcherTimer privado; para probar el mismo camino sin depender del
    /// temporizador se invoca por reflexión el método interno que ese temporizador llama.</summary>
    private static async Task InvocarCargarProximaEmisionAsync(DetalleViewModel sut)
    {
        var metodo = typeof(DetalleViewModel).GetMethod("CargarProximaEmisionAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var tarea = (Task)metodo!.Invoke(sut, [CancellationToken.None])!;
        await tarea;
    }
}
