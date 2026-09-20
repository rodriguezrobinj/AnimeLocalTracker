using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Cola con prioridad ("descargar ahora") y registro del resultado final en el historial de descargas.</summary>
public class DownloadServiceColaEHistorialTests
{
    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly Mock<IVideoSourceResolver> _resolver = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IDatabaseService> _db = new();
    private static readonly string[] TitulosAlt = { "Sousou no Frieren", "Frieren: Beyond Journey's End" };
    private static readonly string Carpeta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltColaTest");

    public DownloadServiceColaEHistorialTests()
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        _settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DescargasSimultaneas = 1 });
    }

    private DownloadService CrearSut() => new(_factory.Object, sourceResolver: _resolver.Object, settingsService: _settings.Object, database: _db.Object);

    private static async Task EsperarHastaAsync(Func<bool> condicion, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condicion() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(25);
        condicion().Should().BeTrue($"condición no alcanzada tras {timeoutMs} ms");
    }

    /// <summary>Resolver que bloquea cada episodio en una puerta y registra el orden en que se resuelven.</summary>
    private (List<int> Orden, Dictionary<int, TaskCompletionSource<bool>> Puertas) ResolverConPuertas()
    {
        var orden = new List<int>();
        var puertas = new Dictionary<int, TaskCompletionSource<bool>>();
        _resolver
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> _, int ep, int? _, CancellationToken ct) =>
            {
                var puerta = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (orden) { orden.Add(ep); puertas[ep] = puerta; }
                using var reg = ct.Register(() => puerta.TrySetCanceled());
                await puerta.Task;
                return null; // termina sin enlace: no se descarga nada real
            });
        return (orden, puertas);
    }

    [Fact]
    public async Task ObtenerDescargasActivas_MarcaEnColaLasQueEsperanSlot()
    {
        var (orden, _) = ResolverConPuertas();
        var sut = CrearSut();

        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 1);
        await EsperarHastaAsync(() => { lock (orden) return orden.Count == 1; });
        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 2);

        var activas = sut.ObtenerDescargasActivas();
        activas.Single(d => d.NumeroEpisodio == 1).EnCola.Should().BeFalse("tiene el único slot");
        activas.Single(d => d.NumeroEpisodio == 2).EnCola.Should().BeTrue("espera un slot libre");

        sut.CancelarTodas();
    }

    [Fact]
    public async Task PriorizarDescarga_AdelantaLaDescargaEnColaAlPrimerPuesto()
    {
        var (orden, puertas) = ResolverConPuertas();
        var sut = CrearSut();

        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 1);
        await EsperarHastaAsync(() => { lock (orden) return orden.Count == 1; });
        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 2);
        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 3);

        sut.PriorizarDescarga(1, 3).Should().BeTrue();

        await Task.Delay(300); // deja que las tareas en segundo plano de 2 y 3 lleguen a la cola
        puertas[1].TrySetResult(true); // la primera termina y libera el slot
        await EsperarHastaAsync(() => { lock (orden) return orden.Count >= 2; });

        lock (orden) orden[1].Should().Be(3, "el episodio priorizado salta al episodio 2, que llegó antes; orden real: " + string.Join(",", orden));

        sut.CancelarTodas();
    }

    [Fact]
    public async Task PriorizarDescarga_NoHaceNadaSiNoEstaEnCola()
    {
        var (orden, _) = ResolverConPuertas();
        var sut = CrearSut();

        await sut.IniciarDescargaEpisodioAsync(1, "A", Carpeta, 1);
        await EsperarHastaAsync(() => { lock (orden) return orden.Count == 1; });

        sut.PriorizarDescarga(1, 1).Should().BeFalse("ya está descargando");
        sut.PriorizarDescarga(99, 9).Should().BeFalse("no existe");

        sut.CancelarTodas();
    }

    [Fact]
    public async Task SiNoSeEncuentraElEpisodio_GuardaLaFallidaEnElHistorialYAvisa()
    {
        _resolver
            .Setup(r => r.BuscarUrlEpisodioAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var guardada = new TaskCompletionSource<DescargaHistorial>(TaskCreationOptions.RunContinuationsAsynchronously);
        _db.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>()))
            .Callback<DescargaHistorial>(h => guardada.TrySetResult(h))
            .Returns(Task.CompletedTask);
        int avisos = 0;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<object, DescargaHistorialActualizadoMensaje>(receptor, (_, _) => Interlocked.Increment(ref avisos));
        var sut = CrearSut();

        try
        {
            await sut.IniciarDescargaEpisodioAsync(7, "Frieren", Carpeta, 4, TitulosAlt);

            var h = await guardada.Task.WaitAsync(TimeSpan.FromSeconds(5));
            h.Completada.Should().BeFalse();
            h.AniListId.Should().Be(7);
            h.NumeroEpisodio.Should().Be(4);
            h.AnimeTitulo.Should().Be("Frieren");
            h.CarpetaDestino.Should().Be(Carpeta);
            h.Error.Should().Contain("No se encontró");
            h.RutaArchivo.Should().BeEmpty();
            h.FechaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
            h.TitulosAlternativos.Should().Contain("Sousou no Frieren");

            await EsperarHastaAsync(() => Volatile.Read(ref avisos) > 0);
            sut.ObtenerDescargasActivas().Should().BeEmpty("la fallida ya no está activa");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }
    }

    [Fact]
    public async Task CancelarUnaDescarga_NoLaRegistraEnElHistorial()
    {
        var (orden, _) = ResolverConPuertas();
        var sut = CrearSut();

        await sut.IniciarDescargaEpisodioAsync(5, "Cancelada", Carpeta, 1);
        await EsperarHastaAsync(() => { lock (orden) return orden.Count == 1; });
        sut.CancelarDescarga(5, 1);
        await Task.Delay(300);

        _db.Verify(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>()), Times.Never);
    }
}
