using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class DownloadServiceTests : IDisposable
{
    // Carpeta destino de las pruebas que solo comprueban la cola de descargas: el servicio la crea y nadie más la borraba.
    private readonly string _carpetaCola = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Cola_{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.CancelarTodas();  // las descargas en cola recrean su carpeta destino si siguen vivas
        ArchivosTemporales.BorrarCarpeta(_carpetaCola);
    }

    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<IVideoSourceResolver> _sourceResolverMock = new();
    private readonly Mock<ISettingsService> _settingsServiceMock = new();
    private readonly DownloadService _sut;

    public DownloadServiceTests()
    {
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        _sourceResolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://example.com/video.mp4");

        _settingsServiceMock
            .Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2 });

        _sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: _sourceResolverMock.Object,
            settingsService: _settingsServiceMock.Object);
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConPreferenciaDeAudioConfigurada_DeberiaReenviarlaAlResolver()
    {
        // Arrange: AppSettings.PreferenciaAudioAnimeAv1 = "DUB" debe llegar tal cual al resolver.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, PreferenciaAudioAnimeAv1 = "DUB" });

        string? audioRecibido = null;
        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, int, int?, string?, string?, CancellationToken>((_, _, _, audio, _, _) => audioRecibido = audio)
            .ReturnsAsync((string?)null); // sin URL: la descarga termina en "no encontrado", no hace falta simular la transferencia

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"pref_audio_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(500, "Anime Preferencia", carpeta, 1);
        await EsperarHastaAsync(() => audioRecibido != null);

        // Assert
        audioRecibido.Should().Be("DUB");
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConServidorPreferidoConfigurado_DeberiaReenviarloAlResolver()
    {
        // Arrange: AppSettings.ServidorPreferidoAnimeAv1 = "Voe" debe llegar tal cual al resolver.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, ServidorPreferidoAnimeAv1 = "Voe" });

        string? servidorRecibido = null;
        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, int, int?, string?, string?, CancellationToken>((_, _, _, _, servidor, _) => servidorRecibido = servidor)
            .ReturnsAsync((string?)null);

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"pref_servidor_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(501, "Anime Preferencia Servidor", carpeta, 1);
        await EsperarHastaAsync(() => servidorRecibido != null);

        // Assert
        servidorRecibido.Should().Be("Voe");
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConTorrentHabilitadoYSinResultadoHttp_DeberiaCaerANyaaYCompletar()
    {
        // Arrange: el resolver HTTP no encuentra nada, pero BusquedaTorrentHabilitada = true
        // y Nyaa sí tiene un candidato — la descarga debe completarse vía torrent.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, BusquedaTorrentHabilitada = true });

        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var candidato = new CandidatoTorrent("[SubsPlease] Anime - 01 (1080p).mkv", "https://nyaa.si/download/1.torrent", "hash", 100, 500_000_000L);
        var nyaaMock = new Mock<INyaaSourceService>();
        nyaaMock
            .Setup(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CandidatoTorrent> { candidato });

        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string destino, int _, bool _, IProgress<(double, double)>? progress, CancellationToken _) =>
            {
                progress?.Report((100, 0));
                return new ResultadoTorrent(true, destino, null);
            });

        var dbMock = new Mock<IDatabaseService>();
        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        dbMock.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<AnimeLocalTracker.Models.DescargaHistorial>()))
            .Callback<AnimeLocalTracker.Models.DescargaHistorial>(h => guardada.TrySetResult(h))
            .Returns(Task.CompletedTask);

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object,
            database: dbMock.Object,
            nyaaSourceService: nyaaMock.Object,
            torrentDownloadService: torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"torrent_fallback_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(600, "Anime Torrent", carpeta, 1);
        var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        historial.Completada.Should().BeTrue();
        historial.AniListId.Should().Be(600);
        torrentMock.Verify(t => t.DescargarAsync(candidato.TorrentUrl, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double, double)>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IniciarDescargaTorrentManualAsync_ConCandidatoElegido_DeberiaDescargarloSinConsultarNyaaNiElResolver()
    {
        // Arrange: Fase 2d — el candidato ya viene elegido por el usuario (selector
        // manual), no debe tocarse ni el resolver HTTP ni la búsqueda automática de Nyaa.
        var candidatoElegido = new CandidatoTorrent("[Erai-raws] Anime - 05 [1080p]", "https://nyaa.si/download/9.torrent", "hash9", 80, 900_000_000L);

        var nyaaMock = new Mock<INyaaSourceService>();
        var resolverMock = new Mock<IVideoSourceResolver>();

        string? torrentUrlRecibido = null;
        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, int, bool, IProgress<(double, double)>?, CancellationToken>((url, _, _, _, _, _, _) => torrentUrlRecibido = url)
            .ReturnsAsync((string _, string _, string destino, int _, bool _, IProgress<(double, double)>? progress, CancellationToken _) =>
            {
                progress?.Report((100, 0));
                return new ResultadoTorrent(true, destino, null);
            });

        var dbMock = new Mock<IDatabaseService>();
        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        dbMock.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<AnimeLocalTracker.Models.DescargaHistorial>()))
            .Callback<AnimeLocalTracker.Models.DescargaHistorial>(h => guardada.TrySetResult(h))
            .Returns(Task.CompletedTask);

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: _settingsServiceMock.Object,
            database: dbMock.Object,
            nyaaSourceService: nyaaMock.Object,
            torrentDownloadService: torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"torrent_manual_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaTorrentManualAsync(604, "Anime Manual", carpeta, 5, candidatoElegido);
        var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        historial.Completada.Should().BeTrue();
        historial.NumeroEpisodio.Should().Be(5);
        torrentUrlRecibido.Should().Be(candidatoElegido.TorrentUrl);
        nyaaMock.Verify(n => n.BuscarEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        nyaaMock.Verify(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        resolverMock.Verify(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConSeguirSembrandoHabilitado_DeberiaReenviarloAlTorrentDownloadService()
    {
        // Arrange: AppSettings.SeguirSembrandoTorrents = true debe llegar tal cual a
        // ITorrentDownloadService.DescargarAsync.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, BusquedaTorrentHabilitada = true, SeguirSembrandoTorrents = true });

        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var candidato = new CandidatoTorrent("[SubsPlease] Anime - 01 (1080p).mkv", "https://nyaa.si/download/1.torrent", "hash", 100, 500_000_000L);
        var nyaaMock = new Mock<INyaaSourceService>();
        nyaaMock
            .Setup(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CandidatoTorrent> { candidato });

        bool? seguirSembrandoRecibido = null;
        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, int, bool, IProgress<(double, double)>?, CancellationToken>((_, _, _, _, seguir, _, _) => seguirSembrandoRecibido = seguir)
            .ReturnsAsync((string _, string _, string destino, int _, bool _, IProgress<(double, double)>? _, CancellationToken _) => new ResultadoTorrent(true, destino, null));

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object,
            nyaaSourceService: nyaaMock.Object,
            torrentDownloadService: torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"seguir_sembrando_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(603, "Anime Sembrando", carpeta, 1);
        await EsperarHastaAsync(() => seguirSembrandoRecibido != null);

        // Assert
        seguirSembrandoRecibido.Should().BeTrue();
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConPreferenciasDeTorrentConfiguradas_DeberiaReenviarlasANyaa()
    {
        // Arrange: GrupoFansubPreferidoTorrent/ResolucionPreferidaTorrent deben llegar tal
        // cual a INyaaSourceService.BuscarCandidatosAsync.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings
            {
                DescargasSimultaneas = 2,
                BusquedaTorrentHabilitada = true,
                GrupoFansubPreferidoTorrent = "SubsPlease",
                ResolucionPreferidaTorrent = "720p",
            });

        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        string? grupoRecibido = null;
        string? resolucionRecibida = null;
        var nyaaMock = new Mock<INyaaSourceService>();
        nyaaMock
            .Setup(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, int, string?, string?, int?, CancellationToken>((_, _, grupo, resolucion, _, _) =>
            {
                grupoRecibido = grupo;
                resolucionRecibida = resolucion;
            })
            .ReturnsAsync(new List<CandidatoTorrent>());

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object,
            nyaaSourceService: nyaaMock.Object,
            torrentDownloadService: new Mock<ITorrentDownloadService>().Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"pref_torrent_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(602, "Anime Preferencia Torrent", carpeta, 1);
        await EsperarHastaAsync(() => grupoRecibido != null);

        // Assert
        grupoRecibido.Should().Be("SubsPlease");
        resolucionRecibida.Should().Be("720p");
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConBusquedaTorrentDeshabilitada_NuncaDeberiaConsultarNyaa()
    {
        // Arrange: BusquedaTorrentHabilitada = false (por defecto) — sin resultado HTTP,
        // el flujo debe fallar directo, sin tocar Nyaa/torrent aunque estén inyectados.
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion())
            .Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, BusquedaTorrentHabilitada = false });

        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var nyaaMock = new Mock<INyaaSourceService>();
        var torrentMock = new Mock<ITorrentDownloadService>();

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: settingsMock.Object,
            nyaaSourceService: nyaaMock.Object,
            torrentDownloadService: torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"torrent_disabled_{Guid.NewGuid():N}");

        // Act
        await sut.IniciarDescargaEpisodioAsync(601, "Anime Sin Torrent", carpeta, 1);
        await Task.Delay(300); // deja que el bucle de descarga llegue a fallar

        // Assert
        nyaaMock.Verify(n => n.BuscarEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        nyaaMock.Verify(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        torrentMock.Verify(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double, double)>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadVideoAsync_ConManifiestoHls_DeberiaDescargarConElDaemon()
    {
        // Arrange: URL m3u8 → se enruta al daemon (download-stream), nunca al HttpClient
        var bridgeMock = new Mock<IPythonBridgeService>();
        var tempDest = Path.Combine(Path.GetTempPath(), $"hls_{Guid.NewGuid():N}.mkv");
        var archivoDaemon = tempDest + ".mp4"; // yt-dlp añade la extensión real al outtmpl
        await File.WriteAllTextAsync(archivoDaemon, "contenido-segmentado");
        bridgeMock.Setup(b => b.ExecuteCommandOneShotAsync<object, DownloadService.DownloadStreamResult>(
                "download-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadService.DownloadStreamResult { Success = true, RutaArchivo = archivoDaemon });

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: _sourceResolverMock.Object,
            settingsService: _settingsServiceMock.Object,
            pythonBridge: bridgeMock.Object);
        try
        {
            // Act
            await sut.DownloadVideoAsync("https://cdn.example.com/master.m3u8", tempDest);

            // Assert: el archivo del daemon se normalizó al destino esperado
            File.Exists(tempDest).Should().BeTrue("el archivo descargado debe quedar en el destino");
            File.Exists(archivoDaemon).Should().BeFalse("la ruta con extensión del daemon se mueve al destino");
            bridgeMock.Verify(b => b.ExecuteCommandOneShotAsync<object, DownloadService.DownloadStreamResult>(
                "download-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            try { if (File.Exists(tempDest)) File.Delete(tempDest); } catch { }
            try { if (File.Exists(archivoDaemon)) File.Delete(archivoDaemon); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ConManifiestoHlsYDaemonFallido_DeberiaLanzarConElError()
    {
        // Arrange: el daemon devuelve error (p. ej. el proveedor HLS cayó)
        var bridgeMock = new Mock<IPythonBridgeService>();
        bridgeMock.Setup(b => b.ExecuteCommandOneShotAsync<object, DownloadService.DownloadStreamResult>(
                "download-stream", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadService.DownloadStreamResult { Success = false, Error = "timeout de red" });

        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: _sourceResolverMock.Object,
            settingsService: _settingsServiceMock.Object,
            pythonBridge: bridgeMock.Object);

        // Act & Assert
        var destino = Path.Combine(Path.GetTempPath(), $"hls_fail_{Guid.NewGuid():N}.mkv");
        var act = async () => await sut.DownloadVideoAsync("https://cdn.example.com/master.m3u8", destino);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*timeout de red*", "el error del daemon debe propagarse al usuario");
    }

    [Fact]
    public void EstaDescargando_DeberiaDevolverFalse_CuandoNoHayDescargas()
    {
        // Act
        bool result = _sut.EstaDescargando(10, 1, out double prog);

        // Assert
        result.Should().BeFalse();
        prog.Should().Be(0);
    }

    [Fact]
    public void CancelarDescarga_Inexistente_NoDeberiaLanzarExcepcion()
    {
        // Act
        var act = () => _sut.CancelarDescarga(999, 1);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void CancelarTodas_NoDeberiaLanzarExcepcion_YDebeLimpiarDescargas()
    {
        // Act
        var act = () => _sut.CancelarTodas();

        // Assert
        act.Should().NotThrow();
        _sut.ObtenerDescargasActivas().Should().BeEmpty();
    }

    [Fact]
    public async Task LimiteDescargasSimultaneas_ConLimite2_DeberiaPermitirSolo2ActivasALaVez()
    {
        // Arrange: resolver que bloquea cada descarga hasta que se libera manualmente
        var puertas = new List<TaskCompletionSource<bool>>();
        var activeCounter = 0;
        var maxConcurrent = 0;

        _sourceResolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> t, int ep, int? aniListId, string? audio, string? servidor, CancellationToken ct) =>
            {
                int now = Interlocked.Increment(ref activeCounter);
                InterlockedExchangeMax(ref maxConcurrent, now);

                var puerta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                puertas.Add(puerta);
                await puerta.Task;
                Interlocked.Decrement(ref activeCounter);
                return "https://example.com/video.mp4";
            });

        _sut.ActualizarLimiteDescargas(2);

        // Act: iniciar 4 descargas (esperan por slots)
        var tareas = new List<Task>();
        for (int i = 1; i <= 4; i++)
        {
            tareas.Add(_sut.IniciarDescargaEpisodioAsync(100 + i, $"Anime {i}", Path.Combine(_carpetaCola, "AltTest"), i));
        }

        // Esperar a que se alcancen 2 concurrentes
        await EsperarHastaAsync(() => activeCounter >= 2);

        // Assert: solo 2 activas en el pico mientras el límite es 2
        maxConcurrent.Should().BeLessThanOrEqualTo(2);

        // Liberar la primera descarga: otra debería ocupar su slot
        puertas[0].TrySetResult(true);
        await Task.Delay(300);

        // Liberar el resto para que terminen
        foreach (var p in puertas)
        {
            p.TrySetResult(true);
        }

        // Esperar a que todas terminen o sean canceladas
        await Task.WhenAll(tareas);
        maxConcurrent.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ActualizarLimiteDescargas_SubirLimite_DeberiaLiberarPendientes()
    {
        // Arrange
        var puertas = new List<TaskCompletionSource<bool>>();
        var resolucionesIniciadas = 0;

        _sourceResolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> t, int ep, int? aniListId, string? audio, string? servidor, CancellationToken ct) =>
            {
                Interlocked.Increment(ref resolucionesIniciadas);
                var puerta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                puertas.Add(puerta);
                await puerta.Task;
                return "https://example.com/video.mp4";
            });

        _sut.ActualizarLimiteDescargas(1);
        var tareas = new List<Task>();
        for (int i = 1; i <= 3; i++)
        {
            tareas.Add(_sut.IniciarDescargaEpisodioAsync(200 + i, $"Anime {i}", Path.Combine(_carpetaCola, "AltTest2"), i));
        }

        // Esperar a que la primera descarga esté resolviendo (slot 1 ocupado)
        await EsperarHastaAsync(() => resolucionesIniciadas >= 1);
        await Task.Delay(200);

        // Act: subir el límite a 3 → las 2 pendientes deben arrancar
        _sut.ActualizarLimiteDescargas(3);
        await EsperarHastaAsync(() => resolucionesIniciadas >= 3, timeoutMs: 5000);

        // Assert: las 3 descargas han alcanzado la fase de resolución (todas con slot)
        resolucionesIniciadas.Should().Be(3);

        foreach (var p in puertas) p.TrySetResult(true);
        await Task.WhenAll(tareas);
    }

    [Fact]
    public async Task IniciarDescargaEpisodioAsync_ConMismoEpisodio_NoDeberiaDuplicar()
    {
        // Arrange
        string carpeta = Path.Combine(_carpetaCola, "AltTest3");
        _sut.ActualizarLimiteDescargas(4);

        // Act: iniciar el mismo episodio dos veces
        await _sut.IniciarDescargaEpisodioAsync(300, "Anime Duplicado", carpeta, 1);
        await _sut.IniciarDescargaEpisodioAsync(300, "Anime Duplicado", carpeta, 1);

        // Assert: solo hay una entrada activa
        _sut.ObtenerDescargasActivas().Should().ContainSingle(d => d.AniListId == 300 && d.NumeroEpisodio == 1);
        _sut.CancelarTodas();
    }

    [Theory]
    [InlineData(1000L, 0L, false, 1000L + Margen)]      // descarga nueva: todo el archivo más el margen
    [InlineData(1000L, 1000L, false, 0L)]               // reanudar: el parcial ya tiene su tamaño reservado
    [InlineData(1000L, 400L, false, 600L + Margen)]     // parcial más pequeño: solo lo que falta por reservar
    [InlineData(1000L, 1000L, true, 1000L + Margen)]    // Mega: hace falta sitio para la copia descifrada
    [InlineData(1000L, 5000L, false, 0L)]               // parcial mayor que el total: nada que reservar
    public void EspacioLibreNecesario_NoCuentaDosVecesLoQueElParcialYaReservo(long total, long yaReservado, bool mega, long esperado)
    {
        DownloadService.EspacioLibreNecesario(total, yaReservado, mega).Should().Be(esperado);
    }

    private const long Margen = 100L * 1024 * 1024;

    [Theory]
    [InlineData("\0\0\0\u0020ftypisom\0\0\u0002\0isomiso2avc1mp41", true)]   // MP4
    [InlineData("\u001AE\u00DF\u00A3 matroska", true)]                       // MKV
    [InlineData("<html><body>403 Forbidden</body></html>", false)]
    [InlineData("  {\"error\":\"expired\"}", false)]
    [InlineData("#EXTM3U\n#EXT-X-VERSION:3\nhttps://otro.sitio/segmento.ts\n", false)]
    [InlineData("\r\n#EXTM3U\nfile:///C:/Users/x/secreto.mkv\n", false)]
    [InlineData("ffconcat version 1.0\nfile 'C:/Users/x/secreto.mkv'\n", false)]
    public void ArchivoPareceVideo_RechazaPaginasDeErrorYListasDeReproduccion(string contenido, bool esperado)
    {
        string ruta = Path.Combine(Path.GetTempPath(), $"AltCabecera_{Guid.NewGuid():N}.downloading");
        File.WriteAllBytes(ruta, System.Text.Encoding.Latin1.GetBytes(contenido));
        try
        {
            DownloadService.ArchivoPareceVideo(ruta).Should().Be(esperado);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Theory]
    [InlineData("https://a4.mp4upload.com:183/d/abc/video.mp4", true)]
    [InlineData("https://delivery.voe-cdn.example/video.mp4", false)]
    [InlineData("https://gfs270n123.userstorage.mega.co.nz/dl/abc", false)]
    [InlineData("https://download2267.mediafire.com/abc/video.mp4", false)]
    public void CrearPeticionVideo_SoloMandaElRefererDeMp4UploadAMp4Upload(string url, bool conReferer)
    {
        using var peticion = DownloadService.CrearPeticionVideo(HttpMethod.Get, url);

        peticion.Headers.UserAgent.ToString().Should().Contain("Mozilla");
        (peticion.Headers.Referrer != null).Should().Be(conReferer);
    }

    private static async Task EsperarHastaAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
        condition().Should().BeTrue($"Condición no alcanzada tras {timeoutMs} ms");
    }

    private static void InterlockedExchangeMax(ref int target, int value)
    {
        int current;
        int updated;
        do
        {
            current = Volatile.Read(ref target);
            updated = Math.Max(current, value);
        }
        while (Interlocked.CompareExchange(ref target, updated, current) != current);
    }

    [Fact]
    public async Task DownloadVideoAsync_ConTamanoDeclaradoGigante_SinSoporteRange_DeberiaAbortarYNoCrearArchivo()
    {
        // Arrange (SEC-03): servidor sin soporte de rangos que declara > 35 GB
        const long gigante = 36L * 1024 * 1024 * 1024;
        var destino = Path.Combine(Path.GetTempPath(), $"cap_{Guid.NewGuid():N}.mp4");

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                };
                respuesta.Content.Headers.ContentLength = req.Method == HttpMethod.Head ? gigante : gigante;
                return Task.FromResult(respuesta);
            });

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handlerMock.Object));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object);

        try
        {
            // Act & Assert: aborta con IOException y no deja archivo parcial
            var act = async () => await sut.DownloadVideoAsync("https://cdn.example.com/video-gigante.mp4", destino);
            await act.Should().ThrowAsync<IOException>()
                .WithMessage("*límite de seguridad*");
            File.Exists(destino).Should().BeFalse("no debe quedar archivo parcial tras el corte de seguridad");
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_AlReanudarSinSoporteDeRangos_DeberiaReiniciarEnVezDeCorromper()
    {
        // Arrange (FUN-014): archivo parcial en disco y servidor que ignora Range (200 en vez de 206)
        var destino = Path.Combine(Path.GetTempPath(), $"no206_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(destino, "01234"u8.ToArray());

        byte[] cuerpoCompleto = "0123456789"u8.ToArray();
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(cuerpoCompleto)
                };
                respuesta.Content.Headers.ContentLength = cuerpoCompleto.Length;
                return Task.FromResult(respuesta);
            });

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handlerMock.Object));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object);

        try
        {
            // Act: reanudar contra un servidor que no respeta el Range
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            // Assert: el archivo final es el cuerpo completo, sin doblar el parcial
            var contenido = await File.ReadAllBytesAsync(destino);
            contenido.Should().Equal(cuerpoCompleto);
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    private static byte[] VideoSintetico(int tamano)
    {
        var video = new byte[tamano]; // empieza como un mp4 (tamaño de caja + "ftyp") y sigue con bytes cualesquiera
        new Random(7).NextBytes(video);
        "\0\0\0 ftyp"u8.ToArray().CopyTo(video, 0);
        return video;
    }

    private DownloadService CrearServiceQueSirve(byte[] cuerpo)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(cuerpo) };
                respuesta.Content.Headers.ContentLength = cuerpo.Length;
                return Task.FromResult(respuesta);
            });
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handlerMock.Object));
        return new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object);
    }

    [Fact]
    public async Task DownloadVideoAsync_ConUrlDeMegaConClave_BajaElArchivoCifradoYLoGuardaEnClaro()
    {
        // Arrange: Mega sirve el archivo cifrado (AES-CTR); la clave viaja en el fragmento de la URL
        var clave = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var video = VideoSintetico(600_000);
        var cifrado = (byte[])video.Clone();
        MegaTransferIt.Descifrar(clave, 0, cifrado);
        var destino = Path.Combine(Path.GetTempPath(), $"mega_{Guid.NewGuid():N}.mp4");
        var sut = CrearServiceQueSirve(cifrado);

        try
        {
            // Act
            await sut.DownloadVideoAsync(MegaTransferIt.UrlConClave("https://gfs1.userstorage.mega.co.nz/dl/token", clave), destino);

            // Assert: el archivo final es el video en claro y no queda el temporal del descifrado
            (await File.ReadAllBytesAsync(destino)).Should().Equal(video);
            File.Exists(destino + ".dec").Should().BeFalse();
        }
        finally
        {
            try { File.Delete(destino); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ConUrlDeMegaYArchivoYaEnClaro_NoLoDescifraDeNuevo()
    {
        // Arrange: lo que llega ya es un video en claro (p. ej. una reanudación tras un corte justo al terminar de
        // descifrar): descifrarlo otra vez lo convertiría en basura.
        var clave = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var video = VideoSintetico(600_000);
        var destino = Path.Combine(Path.GetTempPath(), $"mega_{Guid.NewGuid():N}.mp4");
        var sut = CrearServiceQueSirve(video);

        try
        {
            await sut.DownloadVideoAsync(MegaTransferIt.UrlConClave("https://gfs1.userstorage.mega.co.nz/dl/token", clave), destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(video);
        }
        finally
        {
            try { File.Delete(destino); } catch { }
        }
    }

    /// <summary>Servidor falso de video con soporte de Range, con fallos configurables.</summary>
    private sealed class ServidorRangosHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        private int _cortesPendientes;
        public bool IgnorarRange { get; init; }

        public ServidorRangosHandler(byte[] datos, int cortesPendientes = 0)
        {
            _datos = datos;
            _cortesPendientes = cortesPendientes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = _datos.Length;
                head.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(head);
            }

            var rango = request.Headers.Range?.Ranges.FirstOrDefault();
            if (rango == null || IgnorarRange)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(_datos) });
            }

            long desde = rango.From ?? 0;
            long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
            int longitud = (int)(hasta - desde + 1);

            // Simula un servidor que cierra la conexión a mitad del trozo: entrega solo la mitad.
            if (longitud > 2 && Interlocked.Decrement(ref _cortesPendientes) >= 0)
            {
                longitud /= 2;
            }

            var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_datos, (int)desde, longitud)
            };
            respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
            return Task.FromResult(respuesta);
        }
    }

    private DownloadService CrearServicioConServidor(HttpMessageHandler handler)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        return new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object);
    }

    private static byte[] GenerarVideoFalso(int bytes)
    {
        var datos = new byte[bytes];
        new Random(42).NextBytes(datos);
        return datos;
    }

    [Fact]
    public async Task DownloadVideoAsync_ConServidorConRangos_DeberiaDescargarElArchivoIntacto()
    {
        var datos = GenerarVideoFalso(10 * 1024 * 1024 + 123);
        var destino = Path.Combine(Path.GetTempPath(), $"rangos_{Guid.NewGuid():N}.mp4");
        var sut = CrearServicioConServidor(new ServidorRangosHandler(datos));

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElServidorCortaLaConexionATrozoMedio_DeberiaReintentarSoloEseTrozoYNoCorromper()
    {
        // Antes: un corte a mitad de trozo dejaba un hueco de ceros en el archivo final.
        var datos = GenerarVideoFalso(10 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"cortes_{Guid.NewGuid():N}.mp4");
        var sut = CrearServicioConServidor(new ServidorRangosHandler(datos, cortesPendientes: 3));

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElServidorAnunciaRangosPeroLosIgnora_DeberiaCaerASecuencialSinCorromper()
    {
        // Antes: cada segmento escribía el inicio del video en su propia posición.
        var datos = GenerarVideoFalso(10 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"ignora_{Guid.NewGuid():N}.mp4");
        var sut = CrearServicioConServidor(new ServidorRangosHandler(datos) { IgnorarRange = true });

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            File.Exists(destino + ".state").Should().BeFalse();
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    /// <summary>Servidor cuyos sondeos (HEAD) fallan —como mp4upload— y que solo sirve rangos; cuenta los bytes servidos.</summary>
    private sealed class ServidorSinSondeoHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        public long BytesServidos;
        public int PeticionesSinRango;

        public ServidorSinSondeoHandler(byte[] datos) => _datos = datos;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head) throw new HttpRequestException("sondeo fallido");

            var rango = request.Headers.Range?.Ranges.FirstOrDefault();
            if (rango == null)
            {
                Interlocked.Increment(ref PeticionesSinRango);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(_datos) });
            }

            if (rango.From == 0 && rango.To == 0) throw new HttpRequestException("sondeo GET Range(0,0) fallido");

            long desde = rango.From ?? 0;
            long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
            Interlocked.Add(ref BytesServidos, hasta - desde + 1);
            var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_datos, (int)desde, (int)(hasta - desde + 1))
            };
            respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
            return Task.FromResult(respuesta);
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_AlReanudarConEstadoGuardado_NoSondeaYSoloPideLosTrozosPendientes()
    {
        // Regresión: al pausar y reanudar una descarga al 65 %, los sondeos HEAD/GET fallaban, se caía al modo
        // secuencial y el servidor respondía 200 a la petición con Range: la descarga volvía a empezar desde cero.
        const int totalTrozos = 6; // max(6, ceil(12 MB / 4 MB))
        var datos = GenerarVideoFalso(12 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"reanuda_{Guid.NewGuid():N}.mp4");
        var store = new DownloadStateStore();
        var estado = await store.CargarOInicializarAsync(destino + ".state", datos.Length, totalTrozos);

        // Los primeros 4 trozos (~2/3) ya estaban descargados cuando se pausó.
        var parcial = new byte[datos.Length];
        for (int i = 0; i < 4; i++)
        {
            var t = estado.Segments[i];
            Array.Copy(datos, t.Start, parcial, t.Start, t.End - t.Start + 1);
            t.CurrentOffset = t.End + 1;
        }
        await File.WriteAllBytesAsync(destino, parcial);
        await store.GuardarAsync(destino + ".state", estado);
        long pendientes = estado.Segments.Where(t => t.CurrentOffset <= t.End).Sum(t => t.End - t.CurrentOffset + 1);

        var servidor = new ServidorSinSondeoHandler(datos);
        var sut = CrearServicioConServidor(servidor);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.PeticionesSinRango.Should().Be(0, "reanudar no debe pedir el archivo completo");
            servidor.BytesServidos.Should().Be(pendientes, "solo se descargan los trozos que faltaban");
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ConEstadoGuardadoPeroSinArchivoParcial_DescargaTodoEnVezDeDejarCeros()
    {
        // Regresión: un escaneo de carpeta borraba el parcial de una descarga en pausa pero dejaba el .state;
        // al reanudar, los trozos "ya descargados" quedaban a ceros en un archivo nuevo y el video salía corrupto.
        const int totalTrozos = 6;
        var datos = GenerarVideoFalso(12 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"huerfano_{Guid.NewGuid():N}.mp4");
        var store = new DownloadStateStore();
        var estado = await store.CargarOInicializarAsync(destino + ".state", datos.Length, totalTrozos);
        for (int i = 0; i < 4; i++) estado.Segments[i].CurrentOffset = estado.Segments[i].End + 1;
        await store.GuardarAsync(destino + ".state", estado);

        var sut = CrearServicioConServidor(new ServidorRangosHandler(datos));

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    private static Mock<IDatabaseService> CrearBdQueCapturaHistorial(TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial> guardada)
    {
        var dbMock = new Mock<IDatabaseService>();
        dbMock.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<AnimeLocalTracker.Models.DescargaHistorial>()))
            .Callback<AnimeLocalTracker.Models.DescargaHistorial>(h => guardada.TrySetResult(h))
            .Returns(Task.CompletedTask);
        return dbMock;
    }

    [Fact]
    public async Task ReanudarDescarga_DeUnTorrentEnPausa_SigueConElMismoTorrentYSuCarpetaSinIrAAnimeAv1()
    {
        // Regresión: reanudar un torrent elegido a mano lanzaba la búsqueda en AnimeAv1 en vez de seguir con él.
        var candidato = new CandidatoTorrent("[SubsPlease] Anime - 03 (1080p).mkv", "https://nyaa.si/download/3.torrent", "hash3", 50, 700_000_000L);
        var carpetas = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var urls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int llamadas = 0;
        var primeraEnCurso = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string url, string carpeta, string destino, int _, bool _, IProgress<(double, double)>? _, CancellationToken ct) =>
            {
                urls.Enqueue(url);
                carpetas.Enqueue(carpeta);
                if (Interlocked.Increment(ref llamadas) == 1)
                {
                    primeraEnCurso.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct); // la pausa cancela esta primera descarga
                }
                return new ResultadoTorrent(true, destino, null);
            });

        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverMock = new Mock<IVideoSourceResolver>();
        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: _settingsServiceMock.Object,
            database: CrearBdQueCapturaHistorial(guardada).Object,
            nyaaSourceService: new Mock<INyaaSourceService>().Object,
            torrentDownloadService: torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"torrent_pausa_{Guid.NewGuid():N}");
        try
        {
            await sut.IniciarDescargaTorrentManualAsync(605, "Anime Pausa", carpeta, 3, candidato);
            await primeraEnCurso.Task.WaitAsync(TimeSpan.FromSeconds(5));

            sut.PausarDescarga(605, 3);
            sut.ReanudarDescarga(605, 3);
            var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(5));

            historial.Completada.Should().BeTrue();
            llamadas.Should().Be(2);
            urls.Should().OnlyContain(u => u == candidato.TorrentUrl);
            carpetas.Distinct().Should().HaveCount(1, "reanudar debe usar la misma carpeta para retomar las piezas ya bajadas");
            resolverMock.Verify(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            try { Directory.Delete(carpeta, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task IniciarDescargaTorrentManualAsync_SiElTorrentFalla_GuardaElMotivoRealEnElHistorial()
    {
        var candidato = new CandidatoTorrent("[SubsPlease] Anime - 04 (1080p).mkv", "https://nyaa.si/download/4.torrent", "hash4", 0, 700_000_000L);
        const string motivo = "Nadie está compartiendo este torrent (sin datos en 5 min).";
        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResultadoTorrent(false, null, motivo));

        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new DownloadService(
            _httpClientFactoryMock.Object,
            sourceResolver: new Mock<IVideoSourceResolver>().Object,
            settingsService: _settingsServiceMock.Object,
            database: CrearBdQueCapturaHistorial(guardada).Object,
            torrentDownloadService: torrentMock.Object);

        await sut.IniciarDescargaTorrentManualAsync(606, "Anime Sin Semillas", Path.Combine(Path.GetTempPath(), $"torrent_falla_{Guid.NewGuid():N}"), 4, candidato);
        var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(5));

        historial.Completada.Should().BeFalse();
        historial.Error.Should().Be(motivo);
    }

    /// <summary>
    /// Servidor de rangos que tarda un poco en cada trozo y mide cuántas conexiones de trozo
    /// llegan a estar abiertas a la vez; opcionalmente contesta 429 a las primeras peticiones,
    /// o no contesta nunca al HEAD (como mp4upload a veces).
    /// </summary>
    private sealed class ServidorConcurrenteHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        private int _enCurso;
        private int _rechazos429Pendientes;
        public int MaxSimultaneas;
        public bool HeadNoResponde { get; init; }
        public TimeSpan RetrasoPorTrozo { get; init; } = TimeSpan.FromMilliseconds(150);

        public ServidorConcurrenteHandler(byte[] datos, int rechazos429 = 0)
        {
            _datos = datos;
            _rechazos429Pendientes = rechazos429;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                if (HeadNoResponde) await Task.Delay(Timeout.Infinite, cancellationToken);
                var head = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = _datos.Length;
                head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            var rango = request.Headers.Range!.Ranges.First();
            long desde = rango.From ?? 0;
            long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
            bool esSondeo = desde == 0 && hasta == 0;

            if (!esSondeo)
            {
                if (Interlocked.Decrement(ref _rechazos429Pendientes) >= 0)
                {
                    var saturado = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                    saturado.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                    return saturado;
                }

                int ahora = Interlocked.Increment(ref _enCurso);
                InterlockedExchangeMax(ref MaxSimultaneas, ahora);
                try { await Task.Delay(RetrasoPorTrozo, cancellationToken); }
                finally { Interlocked.Decrement(ref _enCurso); }
            }

            var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_datos, (int)desde, (int)(hasta - desde + 1))
            };
            respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
            return respuesta;
        }
    }

    private static DownloadService CrearServicioConLimite(HttpMessageHandler handler, int descargasSimultaneas)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = descargasSimultaneas });
        return new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMockEstatico.Object, settingsService: settingsMock.Object);
    }

    private static readonly Mock<IVideoSourceResolver> _sourceResolverMockEstatico = new();

    [Fact]
    public async Task DownloadVideoAsync_UnaSolaDescargaConLimiteDe5_UsaTodasSusConexiones()
    {
        // Antes: las conexiones se repartían según el LÍMITE configurado (12 / 5 → 3) aunque el
        // episodio se estuviera descargando solo. Ahora se reparten entre las descargas activas.
        var datos = GenerarVideoFalso(36 * 1024 * 1024 + 7); // 10 trozos de 4 MB
        var destino = Path.Combine(Path.GetTempPath(), $"conexiones_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorConcurrenteHandler(datos);
        var sut = CrearServicioConLimite(servidor, descargasSimultaneas: 5);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.MaxSimultaneas.Should().Be(8);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElServidorPideCalma_ReintentaYTerminaIntacto()
    {
        var datos = GenerarVideoFalso(24 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"saturado_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorConcurrenteHandler(datos, rechazos429: 3);
        var sut = CrearServicioConLimite(servidor, descargasSimultaneas: 1);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElHeadNoResponde_NoEsperaSuTiempoLimiteParaEmpezar()
    {
        // Antes: HEAD (hasta 6 s) y luego GET Range(0,0), uno detrás de otro.
        var datos = GenerarVideoFalso(8 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"sondeo_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorConcurrenteHandler(datos) { HeadNoResponde = true, RetrasoPorTrozo = TimeSpan.Zero };
        var sut = CrearServicioConLimite(servidor, descargasSimultaneas: 1);

        try
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    /// <summary>Stream que entrega sus datos a ritmo limitado (como un servidor que limita cada conexión).</summary>
    private sealed class StreamLento : Stream
    {
        private readonly MemoryStream _interno;
        private readonly TimeSpan _pausaPorLectura;
        private readonly int _bytesPorLectura;
        private readonly Action _alCerrar;
        private int _cerrado;

        public StreamLento(byte[] datos, int desde, int longitud, int bytesPorLectura, TimeSpan pausaPorLectura, Action alCerrar)
        {
            _interno = new MemoryStream(datos, desde, longitud, writable: false);
            _bytesPorLectura = bytesPorLectura;
            _pausaPorLectura = pausaPorLectura;
            _alCerrar = alCerrar;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_pausaPorLectura, cancellationToken);
            int leidos = _interno.Read(buffer.Span[..Math.Min(buffer.Length, _bytesPorLectura)]);
            if (leidos == 0) Cerrar();
            return leidos;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        private void Cerrar()
        {
            if (Interlocked.Exchange(ref _cerrado, 1) == 0) _alCerrar();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cerrar();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Servidor que limita la velocidad de CADA conexión: más conexiones = más velocidad total.</summary>
    private sealed class ServidorLimitadoPorConexionHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        private int _abiertas;
        public int MaxSimultaneas;

        public ServidorLimitadoPorConexionHandler(byte[] datos) => _datos = datos;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = _datos.Length;
                head.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(head);
            }

            var rango = request.Headers.Range!.Ranges.First();
            long desde = rango.From ?? 0;
            long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
            int longitud = (int)(hasta - desde + 1);

            HttpContent contenido;
            if (desde == 0 && hasta == 0)
            {
                contenido = new ByteArrayContent(_datos, 0, 1);
            }
            else
            {
                InterlockedExchangeMax(ref MaxSimultaneas, Interlocked.Increment(ref _abiertas));
                // ~1,6 MB/s por conexión (64 KB cada 40 ms)
                contenido = new StreamContent(new StreamLento(_datos, (int)desde, longitud, 64 * 1024, TimeSpan.FromMilliseconds(40), () => Interlocked.Decrement(ref _abiertas)));
            }

            var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = contenido };
            respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
            return Task.FromResult(respuesta);
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiMasConexionesAceleran_SubePorEncimaDe8()
    {
        // Fase 4: con un servidor que limita cada conexión, abrir más conexiones sube la velocidad
        // total; el ajuste automático lo mide y pasa del reparto fijo de 8.
        var datos = GenerarVideoFalso(96 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"ajuste_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorLimitadoPorConexionHandler(datos);

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(servidor));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object)
        {
            VentanaMedicionConexiones = TimeSpan.FromMilliseconds(300)
        };

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.MaxSimultaneas.Should().BeGreaterThan(8);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    /// <summary>Servidor que tarda en contestar CADA petición (como a3.mp4upload.com con Connection: close); anota los rangos pedidos.</summary>
    private sealed class ServidorLentoEnConectarHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        private readonly TimeSpan _latencia;
        public readonly System.Collections.Concurrent.ConcurrentBag<long> TamanosPedidos = new();
        public long BytesServidos;

        public ServidorLentoEnConectarHandler(byte[] datos, TimeSpan latencia)
        {
            _datos = datos;
            _latencia = latencia;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_latencia, cancellationToken);
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                head.Content.Headers.ContentLength = _datos.Length;
                head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            var rango = request.Headers.Range!.Ranges.First();
            long desde = rango.From ?? 0;
            long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
            int longitud = (int)(hasta - desde + 1);
            if (!(desde == 0 && hasta == 0))
            {
                TamanosPedidos.Add(longitud);
                Interlocked.Add(ref BytesServidos, longitud);
            }

            var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_datos, (int)desde, longitud) };
            respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
            return respuesta;
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ServidorLentoEnConectar_PideVariosTrozosJuntosYNoCorrompe()
    {
        // Al reanudar hay trozos completos, uno a medias y el resto sin empezar: agrupar no debe
        // volver a pedir lo ya bajado ni dejar huecos.
        const long Trozo = 4L * 1024 * 1024;
        var datos = GenerarVideoFalso((int)(25 * Trozo));
        var destino = Path.Combine(Path.GetTempPath(), $"agrupado_{Guid.NewGuid():N}.mp4");
        var store = new DownloadStateStore();
        var estado = await store.CargarOInicializarAsync(destino + ".state", datos.Length, 25);
        var parcial = new byte[datos.Length];
        void Marcar(int i, long bytes)
        {
            var t = estado.Segments[i];
            Array.Copy(datos, t.Start, parcial, t.Start, bytes);
            t.CurrentOffset = t.Start + bytes;
        }
        Marcar(0, Trozo);      // completo
        Marcar(9, Trozo / 3);  // a medias
        Marcar(12, Trozo);     // completo
        await File.WriteAllBytesAsync(destino, parcial);
        await store.GuardarAsync(destino + ".state", estado);
        long pendientes = estado.Segments.Sum(t => t.End - t.CurrentOffset + 1);

        var servidor = new ServidorLentoEnConectarHandler(datos, TimeSpan.FromMilliseconds(120));
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(servidor));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object)
        {
            UmbralServidorLento = TimeSpan.FromMilliseconds(40)
        };

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.BytesServidos.Should().Be(pendientes, "no se vuelve a pedir nada ya descargado");
            servidor.TamanosPedidos.Should().Contain(t => t > Trozo, "con el servidor lento se piden varios trozos por petición");
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ServidorRapido_PideUnTrozoPorPeticion()
    {
        const long Trozo = 4L * 1024 * 1024;
        var datos = GenerarVideoFalso((int)(25 * Trozo));
        var destino = Path.Combine(Path.GetTempPath(), $"no_agrupado_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorLentoEnConectarHandler(datos, TimeSpan.FromMilliseconds(5));
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(servidor));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.TamanosPedidos.Should().OnlyContain(t => t <= Trozo);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    /// <summary>
    /// Servidor que acepta como mucho <c>tope</c> peticiones a la vez y responde 403 a las de más
    /// (como a4.mp4upload.com con varias conexiones del mismo usuario). Con tope 0 rechaza todo (enlace caducado).
    /// </summary>
    private sealed class ServidorConTopeHandler : HttpMessageHandler
    {
        private readonly byte[] _datos;
        private readonly int _tope;
        private int _enCurso;
        public int Rechazos;
        public int MaxAceptadas;

        public ServidorConTopeHandler(byte[] datos, int tope)
        {
            _datos = datos;
            _tope = tope;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int ahora = Interlocked.Increment(ref _enCurso);
            if (ahora > _tope)
            {
                Interlocked.Decrement(ref _enCurso);
                Interlocked.Increment(ref Rechazos);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            }

            try
            {
                InterlockedExchangeMax(ref MaxAceptadas, ahora);
                await Task.Delay(80, cancellationToken);

                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                    head.Content.Headers.ContentLength = _datos.Length;
                    head.Headers.AcceptRanges.Add("bytes");
                    return head;
                }

                var rango = request.Headers.Range!.Ranges.First();
                long desde = rango.From ?? 0;
                long hasta = Math.Min(rango.To ?? _datos.Length - 1, _datos.Length - 1);
                var respuesta = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent(_datos, (int)desde, (int)(hasta - desde + 1)) };
                respuesta.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(desde, hasta, _datos.Length);
                return respuesta;
            }
            finally
            {
                Interlocked.Decrement(ref _enCurso);
            }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElServidorLimitaConexionesCon403_SeAdaptaYTerminaIntacto()
    {
        // Log real (16:08): un 403 esporádico con 10 conexiones cortaba toda la descarga, buscaba otro
        // enlace y tiraba lo descargado. Ahora el 403 con otras conexiones funcionando es "demasiadas".
        var datos = GenerarVideoFalso(40 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"tope403_{Guid.NewGuid():N}.mp4");
        var servidor = new ServidorConTopeHandler(datos, tope: 5);
        var sut = CrearServicioConServidor(servidor);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
            servidor.Rechazos.Should().BeGreaterThan(0, "el test debe provocar el 403 por exceso de conexiones");
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_AlReanudarContraUnServidorConTope_NoLoConfundeConEnlaceRechazado()
    {
        // Al reanudar no hay sondeo (no hay prueba reciente de que el enlace funciona): si se abrieran las 8
        // conexiones a la vez, los 403 por exceso parecerían un enlace caducado. Arranque prudente: una primero.
        const int totalTrozos = 10;
        var datos = GenerarVideoFalso(totalTrozos * 4 * 1024 * 1024);
        var destino = Path.Combine(Path.GetTempPath(), $"tope_reanuda_{Guid.NewGuid():N}.mp4");
        var store = new DownloadStateStore();
        var estado = await store.CargarOInicializarAsync(destino + ".state", datos.Length, totalTrozos);
        var parcial = new byte[datos.Length];
        for (int i = 0; i < 2; i++)
        {
            var t = estado.Segments[i];
            Array.Copy(datos, t.Start, parcial, t.Start, t.End - t.Start + 1);
            t.CurrentOffset = t.End + 1;
        }
        await File.WriteAllBytesAsync(destino, parcial);
        await store.GuardarAsync(destino + ".state", estado);

        var servidor = new ServidorConTopeHandler(datos, tope: 4);
        var sut = CrearServicioConServidor(servidor);

        try
        {
            await sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await File.ReadAllBytesAsync(destino)).Should().Equal(datos);
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_DosEpisodiosDelMismoServidorConTope_TerminanAmbos()
    {
        // Log real (14:30): con Clevatess ep 12 bajando de a4, el ep 9 del mismo servidor recibía 403
        // en cada intento y fallaba. El tope se comparte entre descargas: el segundo espera su turno.
        var datos1 = GenerarVideoFalso(24 * 1024 * 1024);
        var datos2 = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(datos2);
        var destino1 = Path.Combine(Path.GetTempPath(), $"tope_a_{Guid.NewGuid():N}.mp4");
        var destino2 = Path.Combine(Path.GetTempPath(), $"tope_b_{Guid.NewGuid():N}.mp4");

        var servidor1 = new ServidorConTopeHandler(datos1, tope: 6);
        var servidor2 = new ServidorConTopeHandler(datos2, tope: 6);
        var enrutador = new EnrutadorPorArchivoHandler(("video1.mp4", servidor1), ("video2.mp4", servidor2));
        var tope = new TopeCompartidoHandler(6) { InnerHandler = enrutador };
        var sut = CrearServicioConServidor(tope);

        try
        {
            await Task.WhenAll(
                sut.DownloadVideoAsync("https://cdn.example.com/video1.mp4", destino1),
                sut.DownloadVideoAsync("https://cdn.example.com/video2.mp4", destino2));

            (await File.ReadAllBytesAsync(destino1)).Should().Equal(datos1);
            (await File.ReadAllBytesAsync(destino2)).Should().Equal(datos2);
        }
        finally
        {
            foreach (var d in new[] { destino1, destino2 })
            {
                try { File.Delete(d); File.Delete(d + ".state"); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_ConElServidorAlTope_ElSondeoEsperaTurnoAunqueTardeMasQueSuPlazo()
    {
        // Log real (16:39–16:40): con 4 episodios de a3 en marcha, el sondeo del quinto agotó su plazo
        // esperando turno, cayó al modo de una conexión (sin turno) y recibió un 403 → falso "enlace rechazado".
        var datosA = GenerarVideoFalso(24 * 1024 * 1024);
        var datosB = new byte[8 * 1024 * 1024];
        new Random(11).NextBytes(datosB);
        var destinoA = Path.Combine(Path.GetTempPath(), $"turno_a_{Guid.NewGuid():N}.mp4");
        var destinoB = Path.Combine(Path.GetTempPath(), $"turno_b_{Guid.NewGuid():N}.mp4");

        var enrutador = new EnrutadorPorArchivoHandler(
            ("videoA.mp4", new ServidorConTopeHandler(datosA, tope: 100)),
            ("videoB.mp4", new ServidorConTopeHandler(datosB, tope: 100)));
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new TopeCompartidoHandler(3) { InnerHandler = enrutador }));
        var sut = new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: _settingsServiceMock.Object)
        {
            TiempoMaximoSondeo = TimeSpan.FromMilliseconds(150)
        };

        try
        {
            var descargaA = sut.DownloadVideoAsync("https://cdn.example.com/videoA.mp4", destinoA);
            await Task.Delay(400); // A ya llenó el servidor y aprendió su tope
            await sut.DownloadVideoAsync("https://cdn.example.com/videoB.mp4", destinoB);
            await descargaA;

            (await File.ReadAllBytesAsync(destinoA)).Should().Equal(datosA);
            (await File.ReadAllBytesAsync(destinoB)).Should().Equal(datosB, "B no debe caer al modo sin turno ni fallar por el 403");
        }
        finally
        {
            foreach (var d in new[] { destinoA, destinoB })
            {
                try { File.Delete(d); File.Delete(d + ".state"); } catch { }
            }
        }
    }

    [Fact]
    public async Task DownloadVideoAsync_SiElEnlaceEstaRechazadoDeVerdad_FallaRapidoCon403()
    {
        // Sin ninguna conexión funcionando, un 403 sí es del enlace: no hay que quedarse esperando turno.
        var servidor = new ServidorConTopeHandler(GenerarVideoFalso(8 * 1024 * 1024), tope: 0);
        var sut = CrearServicioConServidor(servidor);
        var destino = Path.Combine(Path.GetTempPath(), $"rechazado_{Guid.NewGuid():N}.mp4");

        try
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            var accion = () => sut.DownloadVideoAsync("https://cdn.example.com/video.mp4", destino);

            (await accion.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
            reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { File.Delete(destino); File.Delete(destino + ".state"); } catch { }
        }
    }

    /// <summary>Reparte las peticiones entre servidores falsos según el nombre del archivo pedido.</summary>
    private sealed class EnrutadorPorArchivoHandler : HttpMessageHandler
    {
        private readonly (string Archivo, HttpMessageHandler Handler)[] _rutas;
        private readonly System.Reflection.MethodInfo _send = typeof(HttpMessageHandler).GetMethod("SendAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        public EnrutadorPorArchivoHandler(params (string Archivo, HttpMessageHandler Handler)[] rutas) => _rutas = rutas;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var handler = _rutas.First(r => request.RequestUri!.AbsolutePath.EndsWith(r.Archivo, StringComparison.Ordinal)).Handler;
            return (Task<HttpResponseMessage>)_send.Invoke(handler, new object[] { request, cancellationToken })!;
        }
    }

    /// <summary>Tope de peticiones simultáneas del SERVIDOR (host) entero, compartido por todos sus archivos.</summary>
    private sealed class TopeCompartidoHandler : DelegatingHandler
    {
        private readonly int _tope;
        private int _enCurso;

        public TopeCompartidoHandler(int tope) => _tope = tope;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _enCurso) > _tope)
            {
                Interlocked.Decrement(ref _enCurso);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            }
            try { return await base.SendAsync(request, cancellationToken); }
            finally { Interlocked.Decrement(ref _enCurso); }
        }
    }

    /// <summary>Servidor que nunca responde a tiempo (como a3.mp4upload.com saturado): todo acaba en tiempo agotado.</summary>
    private sealed class ServidorSinRespuestaHandler : HttpMessageHandler
    {
        public int Peticiones;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Peticiones);
            throw new TaskCanceledException("The operation was canceled.", new TimeoutException("La conexión tardó demasiado."));
        }
    }

    private DownloadService CrearServicioSinRespuesta(ServidorSinRespuestaHandler servidor, bool torrentHabilitado, Mock<IDatabaseService> db,
        INyaaSourceService? nyaa = null, ITorrentDownloadService? torrent = null)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(servidor));
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.ObtenerConfiguracion()).Returns(new AnimeLocalTracker.Models.AppSettings { DescargasSimultaneas = 2, BusquedaTorrentHabilitada = torrentHabilitado });
        return new DownloadService(factoryMock.Object, sourceResolver: _sourceResolverMock.Object, settingsService: settingsMock.Object,
            database: db.Object, nyaaSourceService: nyaa, torrentDownloadService: torrent);
    }

    [Fact]
    public async Task Descarga_SiElServidorNuncaResponde_FallaConUnErrorVisibleYQuedaEnElHistorial()
    {
        // Regresión (log real con a3.mp4upload.com): agotados los reintentos por tiempo de espera,
        // la descarga desaparecía de la lista sin error ni historial ("Descarga interrumpida... Pausado: False").
        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = CrearServicioSinRespuesta(new ServidorSinRespuestaHandler(), torrentHabilitado: false, CrearBdQueCapturaHistorial(guardada));

        var carpeta = Path.Combine(Path.GetTempPath(), $"sin_respuesta_{Guid.NewGuid():N}");
        try
        {
            await sut.IniciarDescargaEpisodioAsync(608, "Anime Servidor Caido", carpeta, 14);
            var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(20));

            historial.Completada.Should().BeFalse();
            historial.Error.Should().Be(LocalizationService.T("Desc_ErrorServidorNoResponde"));
            sut.EstaDescargando(608, 14, out _).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(carpeta, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Descarga_SiElServidorNuncaRespondeYHayTorrent_LaCompletaPorNyaa()
    {
        var candidato = new CandidatoTorrent("[SubsPlease] Anime - 14 (1080p).mkv", "https://nyaa.si/download/14.torrent", "hash14", 200, 1_400_000_000L);
        var nyaaMock = new Mock<INyaaSourceService>();
        nyaaMock
            .Setup(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CandidatoTorrent> { candidato });
        var torrentMock = new Mock<ITorrentDownloadService>();
        torrentMock
            .Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<IProgress<(double Progreso, double VelocidadBps)>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string destino, int _, bool _, IProgress<(double, double)>? _, CancellationToken _) => new ResultadoTorrent(true, destino, null));

        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var servidor = new ServidorSinRespuestaHandler();
        var sut = CrearServicioSinRespuesta(servidor, torrentHabilitado: true, CrearBdQueCapturaHistorial(guardada), nyaaMock.Object, torrentMock.Object);

        var carpeta = Path.Combine(Path.GetTempPath(), $"sin_respuesta_torrent_{Guid.NewGuid():N}");
        try
        {
            await sut.IniciarDescargaEpisodioAsync(609, "Anime Servidor Caido Torrent", carpeta, 14);
            var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(20));

            historial.Completada.Should().BeTrue();
            torrentMock.Verify(t => t.DescargarAsync(candidato.TorrentUrl, It.IsAny<string>(), It.IsAny<string>(), 14, It.IsAny<bool>(), It.IsAny<IProgress<(double, double)>?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(carpeta, recursive: true); } catch { }
        }
    }

    /// <summary>Conectividad controlada por el test: esperar a la red la "devuelve" al instante.</summary>
    private sealed class ConectividadFalsa : IConectividadRed
    {
        public volatile bool Online = true;
        public int Esperas;

        public bool HayInternet => Online;

        public Task<bool> EsperarInternetAsync(TimeSpan maximo, CancellationToken ct)
        {
            Interlocked.Increment(ref Esperas);
            Online = true;
            return Task.FromResult(true);
        }
    }

    /// <summary>Falla toda petición mientras la conectividad falsa esté sin red.</summary>
    private sealed class CaidaDeRedHandler : DelegatingHandler
    {
        private readonly ConectividadFalsa _red;

        public CaidaDeRedHandler(ConectividadFalsa red, HttpMessageHandler interno) : base(interno) => _red = red;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _red.Online ? base.SendAsync(request, cancellationToken) : throw new HttpRequestException("No se puede establecer la conexión (sin red).");
    }

    [Fact]
    public async Task Descarga_SiSeCaeInternet_EsperaALaRedSinGastarReintentosYTermina()
    {
        // Regresión: con el wifi caído unos minutos, los 5 reintentos se agotaban y la descarga fallaba.
        var datos = GenerarVideoFalso(1024 * 1024);
        new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }.CopyTo(datos, 0); // cabecera MP4
        var red = new ConectividadFalsa();

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new CaidaDeRedHandler(red, new ServidorRangosHandler(datos))));

        var resolverMock = new Mock<IVideoSourceResolver>();
        resolverMock
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => red.Online = false) // la red se cae justo cuando empieza a descargar
            .ReturnsAsync("https://cdn.example.com/video.mp4");

        var guardada = new TaskCompletionSource<AnimeLocalTracker.Models.DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = new DownloadService(
            factoryMock.Object,
            sourceResolver: resolverMock.Object,
            settingsService: _settingsServiceMock.Object,
            database: CrearBdQueCapturaHistorial(guardada).Object,
            conectividad: red);

        var mensajes = new System.Collections.Concurrent.ConcurrentQueue<DescargaProgresoMensaje>();
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(receptor, (_, m) => { if (m.AniListId == 607) mensajes.Enqueue(m); });

        var carpeta = Path.Combine(Path.GetTempPath(), $"sin_red_{Guid.NewGuid():N}");
        try
        {
            await sut.IniciarDescargaEpisodioAsync(607, "Anime Sin Red", carpeta, 1);
            var historial = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(10));

            historial.Completada.Should().BeTrue();
            (await File.ReadAllBytesAsync(historial.RutaArchivo)).Should().Equal(datos);
            red.Esperas.Should().Be(1);
            mensajes.Should().Contain(m => m.SinConexion, "la fila debe mostrar que espera a la red");
            mensajes.Should().OnlyContain(m => m.Reintentos == 0, "esperar a la red no gasta reintentos");
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<DescargaProgresoMensaje>(receptor);
            try { Directory.Delete(carpeta, recursive: true); } catch { }
        }
    }
}
