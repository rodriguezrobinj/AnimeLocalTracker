using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
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
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
    public async Task DownloadVideoAsync_ConManifiestoHls_DeberiaDescargarConElDaemon()
    {
        // Arrange: URL m3u8 â†’ se enruta al daemon (download-stream), nunca al HttpClient
        var bridgeMock = new Mock<IPythonBridgeService>();
        var tempDest = Path.Combine(Path.GetTempPath(), $"hls_{Guid.NewGuid():N}.mkv");
        var archivoDaemon = tempDest + ".mp4"; // yt-dlp aÃ±ade la extensiÃ³n real al outtmpl
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

            // Assert: el archivo del daemon se normalizÃ³ al destino esperado
            File.Exists(tempDest).Should().BeTrue("el archivo descargado debe quedar en el destino");
            File.Exists(archivoDaemon).Should().BeFalse("la ruta con extensiÃ³n del daemon se mueve al destino");
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
        // Arrange: el daemon devuelve error (p. ej. el proveedor HLS cayÃ³)
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
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> t, int ep, CancellationToken ct) =>
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

        // Assert: solo 2 activas en el pico mientras el lÃ­mite es 2
        maxConcurrent.Should().BeLessThanOrEqualTo(2);

        // Liberar la primera descarga: otra deberÃ­a ocupar su slot
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
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> t, int ep, CancellationToken ct) =>
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

        // Esperar a que la primera descarga estÃ© resolviendo (slot 1 ocupado)
        await EsperarHastaAsync(() => resolucionesIniciadas >= 1);
        await Task.Delay(200);

        // Act: subir el lÃ­mite a 3 â†’ las 2 pendientes deben arrancar
        _sut.ActualizarLimiteDescargas(3);
        await EsperarHastaAsync(() => resolucionesIniciadas >= 3, timeoutMs: 5000);

        // Assert: las 3 descargas han alcanzado la fase de resoluciÃ³n (todas con slot)
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
        condition().Should().BeTrue($"CondiciÃ³n no alcanzada tras {timeoutMs} ms");
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
}
