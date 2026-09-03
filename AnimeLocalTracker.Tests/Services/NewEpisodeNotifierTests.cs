using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Cobertura de notificaciones de episodios nuevos (TST-01): flag desactivado,
/// deduplicación persistente y límite de 20 por pasada.
/// </summary>
public class NewEpisodeNotifierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _notificadosPath;
    private readonly Mock<IDatabaseService> _dbMock;
    private readonly Mock<IFileScannerService> _scannerMock;
    private readonly Mock<ISettingsService> _settingsMock;

    public NewEpisodeNotifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AnimeTracker_Notifier_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _notificadosPath = Path.Combine(_tempDir, "episodios_notificados.json");

        _dbMock = new Mock<IDatabaseService>();
        _scannerMock = new Mock<IFileScannerService>();
        _settingsMock = new Mock<ISettingsService>();
        _settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private NewEpisodeNotifier CrearNotifier() =>
        new(_dbMock.Object, _scannerMock.Object, _settingsMock.Object, _notificadosPath);

    private void ConfigurarBibliotecaConEps(int cantidadEpisodios)
    {
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Anime", RutaCarpeta = _tempDir }
        });
        // PERF-02: el notificador usa la proyección ligera (sin Sinopsis)
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            new() { AniListId = 1, Titulo = "Anime", RutaCarpeta = _tempDir }
        });
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _scannerMock.Setup(s => s.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(
            Enumerable.Range(1, cantidadEpisodios).Select(i => new EpisodioItem { NumeroEpisodio = i }).ToList());
    }

    [Fact]
    public async Task BuscarYNotificar_NotificacionDesactivada_NoDeberiaEscanear()
    {
        // Arrange
        _settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { NotificarNuevosEpisodios = false });
        ConfigurarBibliotecaConEps(3);
        var notifier = CrearNotifier();

        // Act
        int resultado = await notifier.BuscarYNotificarNuevosAsync();

        // Assert
        resultado.Should().Be(0);
        _scannerMock.Verify(s => s.EscanearEpisodiosAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BuscarYNotificar_PrimeraVez_DeberiaNotificarYPersistirDedup()
    {
        // Arrange
        ConfigurarBibliotecaConEps(3);
        var notifier = CrearNotifier();

        // Act: dos pasadas seguidas
        int primera = await notifier.BuscarYNotificarNuevosAsync();
        int segunda = await notifier.BuscarYNotificarNuevosAsync();

        // Assert: la primera notifica 3; la deduplicación persistida evita re-notificar
        primera.Should().Be(3);
        segunda.Should().Be(0);
        File.Exists(_notificadosPath).Should().BeTrue("el historial de notificados debe persistirse");
    }

    [Fact]
    public async Task BuscarYNotificar_MasDe20Nuevos_DeberiaLimitarLaPasada()
    {
        // Arrange: 30 episodios nuevos
        ConfigurarBibliotecaConEps(30);
        var notifier = CrearNotifier();

        // Act
        int resultado = await notifier.BuscarYNotificarNuevosAsync();

        // Assert
        resultado.Should().Be(20, "el límite por pasada es 20");
    }
}
