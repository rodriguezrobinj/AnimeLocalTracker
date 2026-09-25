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

public class DownloadServiceTests
{
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
            .Setup(n => n.BuscarEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidato);

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
        nyaaMock.Verify(n => n.BuscarCandidatosAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Setup(n => n.BuscarEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidato);

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
        // cual a INyaaSourceService.BuscarEpisodioAsync.
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
            .Setup(n => n.BuscarEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, int, string?, string?, CancellationToken>((_, _, grupo, resolucion, _) =>
            {
                grupoRecibido = grupo;
                resolucionRecibida = resolucion;
            })
            .ReturnsAsync((CandidatoTorrent?)null);

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
            tareas.Add(_sut.IniciarDescargaEpisodioAsync(100 + i, $"Anime {i}", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltTest"), i));
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
            tareas.Add(_sut.IniciarDescargaEpisodioAsync(200 + i, $"Anime {i}", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltTest2"), i));
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
        string carpeta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltTest3");
        _sut.ActualizarLimiteDescargas(4);

        // Act: iniciar el mismo episodio dos veces
        await _sut.IniciarDescargaEpisodioAsync(300, "Anime Duplicado", carpeta, 1);
        await _sut.IniciarDescargaEpisodioAsync(300, "Anime Duplicado", carpeta, 1);

        // Assert: solo hay una entrada activa
        _sut.ObtenerDescargasActivas().Should().ContainSingle(d => d.AniListId == 300 && d.NumeroEpisodio == 1);
        _sut.CancelarTodas();
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
}
