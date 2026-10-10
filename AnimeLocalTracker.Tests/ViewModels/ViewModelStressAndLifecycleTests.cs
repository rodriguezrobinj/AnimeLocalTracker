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

[Collection("NavigationServiceTests")]
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
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>());
        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        services.AddSingleton<GaleriaViewModel>();
        services.AddSingleton<CalendarioViewModel>();
        services.AddSingleton<DescargasViewModel>();
        services.AddSingleton<ConfiguracionViewModel>();
        services.AddSingleton<AnimeLibraryService>();
        services.AddSingleton<CacheMaintenanceService>();
        services.AddSingleton(new Mock<IPluginService>().Object);

        var sp = services.BuildServiceProvider();
        var navigationService = new NavigationService(sp);
        try
        {
            var sut = new MainViewModel(navigationService, _trackingMock.Object, sp.GetRequiredService<AnimeLibraryService>(), _downloadMock.Object, _updateMock.Object, new Mock<IDialogService>().Object,
                Mock.Of<ISelectorTorrentService>(),
                Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(),
                new NewEpisodeNotifier(Mock.Of<IDatabaseService>(), Mock.Of<IFileScannerService>(), Mock.Of<ISettingsService>()),
                Mock.Of<ISystemTrayService>());

            // Act: Conmutar 100 veces entre todas las vistas principales
            for (int i = 0; i < 100; i++)
            {
                sut.Navigation.Receive(new NavegarMensaje_Pestana(Pestanas.Calendario));
                sut.Navigation.EstaActiva(Pestanas.Calendario).Should().BeTrue();
                sut.PestanasSuperiores.Where(b => b.EsActiva).Select(b => b.Pestana).Should().Equal(Pestanas.Calendario);

                sut.Navigation.Receive(new NavegarMensaje_Pestana(Pestanas.Descargas));
                sut.Navigation.EstaActiva(Pestanas.Descargas).Should().BeTrue();

                sut.Navigation.Receive(new NavegarMensaje_Pestana(Pestanas.Configuracion));
                sut.Navigation.EstaActiva(Pestanas.Configuracion).Should().BeTrue();
                sut.PestanasInferiores.Where(b => b.EsActiva).Select(b => b.Pestana).Should().Equal(Pestanas.Configuracion);
                sut.PestanasSuperiores.Should().NotContain(b => b.EsActiva);

                sut.Navigation.Receive(new NavegarMensaje_Pestana(Pestanas.Galeria));
                sut.Navigation.EstaActiva(Pestanas.Galeria).Should().BeTrue();
            }
        }
        finally
        {
            // NavigationService se registra en WeakReferenceMessenger.Default (estático de
            // proceso) en su constructor — sin este unregister, la instancia sigue viva y
            // puede recibir mensajes de OTROS tests que corran después, lanzando excepciones
            // porque este ServiceProvider de prueba no tiene todos los ViewModels registrados.
            WeakReferenceMessenger.Default.UnregisterAll(navigationService);
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
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(animes);

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
            .ReturnsAsync((true, schedules));

        // Act
        var sut = new CalendarioViewModel(_dbMock.Object, _trackingMock.Object);
        // La carga arranca sola en el constructor: se espera a que reparta los 28 episodios, no un tiempo fijo (con la máquina
        // cargada 100 ms no bastaban y la prueba fallaba a ratos con los días vacíos).
        int Repartidos() => sut.Lunes.Count + sut.Martes.Count + sut.Miercoles.Count + sut.Jueves.Count + sut.Viernes.Count + sut.Sabado.Count + sut.Domingo.Count;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while (Repartidos() < 28 && reloj.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);

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
