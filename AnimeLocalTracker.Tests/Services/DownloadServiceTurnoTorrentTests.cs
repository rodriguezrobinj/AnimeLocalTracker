using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Episodios del mismo torrent: de uno en uno, sin ocupar huecos mientras esperan.</summary>
public sealed class DownloadServiceTurnoTorrentTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"alt_turno_{Guid.NewGuid():N}");
    private readonly Mock<ITorrentDownloadService> _torrent = new();
    private readonly Dictionary<int, TaskCompletionSource<bool>> _fin = new();
    private readonly List<int> _empezados = new();
    private int _activos, _maxActivos;
    private DownloadService? _sut;

    public void Dispose()
    {
        _sut?.CancelarTodas();
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private DownloadService CrearSut()
    {
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DescargasSimultaneas = 3 });
        _torrent.Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<IProgress<(double, double)>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string destino, int ep, bool _, IProgress<(double, double)>? _, CancellationToken ct) =>
            {
                lock (_empezados) { _empezados.Add(ep); _maxActivos = Math.Max(_maxActivos, ++_activos); }
                try { await _fin[ep].Task.WaitAsync(ct); }
                finally { lock (_empezados) _activos--; }
                return new ResultadoTorrent(true, destino, null);
            });
        _sut = new DownloadService(fabrica.Object, settingsService: ajustes.Object, database: Mock.Of<IDatabaseService>(), torrentDownloadService: _torrent.Object);
        return _sut;
    }

    private static CandidatoTorrent C(string hash) => new("Show", $"https://nyaa.si/download/{hash}.torrent", hash, 0, 1);

    private static async Task Esperar(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue();
    }

    [Fact]
    public async Task MismoTorrent_DeUnoEnUno()
    {
        var sut = CrearSut();
        foreach (int ep in new[] { 1, 2, 3 }) _fin[ep] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (int ep in new[] { 1, 2, 3 }) await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, ep, C("h"));
        await Esperar(() => _empezados.Count == 1);
        await Task.Delay(200);
        _empezados.Should().HaveCount(1, "los otros esperan su turno");

        foreach (int ep in new[] { 1, 2, 3 }) _fin[ep].TrySetResult(true);
        await Esperar(() => _empezados.Count == 3);
        _maxActivos.Should().Be(1);
    }

    [Fact]
    public async Task TorrentsDistintos_NoSeEsperan()
    {
        var sut = CrearSut();
        _fin[1] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[2] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 1, C("a"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 2, C("b"));

        await Esperar(() => _activos == 2);
        _fin[1].TrySetResult(true);
        _fin[2].TrySetResult(true);
    }

    [Fact]
    public async Task PausarElQueEspera_LiberaYNoArranca()
    {
        var sut = CrearSut();
        _fin[1] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[2] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[3] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 1, C("h"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 2, C("h"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 3, C("h"));
        await Esperar(() => _empezados.Count == 1);

        sut.PausarDescarga(9, 2);
        _fin[1].TrySetResult(true);
        _fin[3].TrySetResult(true);

        await Esperar(() => _empezados.Contains(3));
        _empezados.Should().NotContain(2, "está en pausa");
    }
}
