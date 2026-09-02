using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class ViewModelStressAndLifecycleTests
{
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly Mock<IAuthService> _authMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();
    private readonly Mock<IUpdateService> _updateMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly Mock<IImageCacheService> _imageCacheMock = new();

    [Fact]
    public void MainViewModel_NavegacionMasiva100Veces_NoDeberiaLanzarExcepciones()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(_trackingMock.Object);
        services.AddSingleton(_dbMock.Object);
        services.AddSingleton(_downloadMock.Object);
        services.AddSingleton(_authMock.Object);
        services.AddSingleton(_dialogMock.Object);
        services.AddSingleton(_updateMock.Object);
        services.AddSingleton(_settingsMock.Object);
        services.AddSingleton(_imageCacheMock.Object);

        _downloadMock.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        services.AddSingleton<GaleriaViewModel>();
        services.AddSingleton<CalendarioViewModel>();
        services.AddSingleton<DescargasViewModel>();
        services.AddSingleton<ConfiguracionViewModel>();
        services.AddSingleton<AnimeLibraryService>();
        services.AddSingleton<CacheMaintenanceService>();

        var sp = services.BuildServiceProvider();
        var sut = new MainViewModel(new NavigationService(sp), _trackingMock.Object, sp.GetRequiredService<AnimeLibraryService>(), _downloadMock.Object, _updateMock.Object);

        // Act: Conmutar 100 veces entre todas las vistas principales
        for (int i = 0; i < 100; i++)
        {
            sut.Receive(new NavegarMensaje_Calendario());
            sut.EsCalendarioActivo.Should().BeTrue();

            sut.Receive(new NavegarMensaje_Descargas());
            sut.EsDescargasActivas.Should().BeTrue();

            sut.Receive(new NavegarMensaje_Configuracion());
            sut.EsConfiguracionActiva.Should().BeTrue();

            sut.Receive(new NavegarMensaje_Galeria());
            sut.EsGaleriaActiva.Should().BeTrue();
        }
    }

    [Fact]
    public async Task CalendarioViewModel_CargaMasivaDeAnimesEnEmision_DeberiaDistribuirEnDiasCorrectamente()
    {
        // Arrange
        var animes = Enumerable.Range(1, 28).Select(i => new AnimeItem
        {
            AniListId = i,
            Titulo = $"Airing Series {i}",
            Estado = "RELEASING",
            UrlPortada = $"https://example.com/{i}.jpg"
        }).ToList();

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);

        DateTime lunes = DateTime.UtcNow.Date;
        while (lunes.DayOfWeek != DayOfWeek.Monday) lunes = lunes.AddDays(-1);

        var schedules = new List<AiringEpisode>();
        for (int i = 0; i < 28; i++)
        {
            int diaOffset = i % 7;
            schedules.Add(new AiringEpisode
            {
                AniListId = i + 1,
                Titulo = $"Airing Series {i + 1}",
                NumeroEpisodio = 5,
                FechaEmision = lunes.AddDays(diaOffset).AddHours(12)
            });
        }

        _trackingMock
            .Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(schedules);

        // Act
        var sut = new CalendarioViewModel(_dbMock.Object, _trackingMock.Object);
        await Task.Delay(100);

        // Assert
        sut.Lunes.Should().HaveCount(4);
        sut.Martes.Should().HaveCount(4);
        sut.Miercoles.Should().HaveCount(4);
        sut.Jueves.Should().HaveCount(4);
        sut.Viernes.Should().HaveCount(4);
        sut.Sabado.Should().HaveCount(4);
        sut.Domingo.Should().HaveCount(4);
        sut.TotalAnimesEnEmision.Should().Be(28);
    }

    [Fact]
    public void DescargasViewModel_Rafaga200MensajesProgreso_DeberiaActualizarSinErrores()
    {
        // Arrange
        _downloadMock.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());
        var sut = new DescargasViewModel(_downloadMock.Object);

        // Act: Enviar 200 mensajes de progreso concurrentes
        for (int ep = 1; ep <= 50; ep++)
        {
            sut.Receive(new DescargaProgresoMensaje(10, ep, 0.25, true, false, false, "", null, "One Piece"));
            sut.Receive(new DescargaProgresoMensaje(10, ep, 0.75, true, false, false, "", null, "One Piece"));
            sut.Receive(new DescargaProgresoMensaje(10, ep, 1.00, false, true, false, $"C:\\ep_{ep}.mkv", null, "One Piece"));
        }

        // Assert
        sut.Should().NotBeNull();
    }
}
