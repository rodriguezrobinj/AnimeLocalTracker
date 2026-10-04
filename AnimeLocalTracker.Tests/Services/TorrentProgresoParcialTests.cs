using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Con el motor real de MonoTorrent pero sin red: un pack del que solo se quiere un episodio debe
/// darse por terminado cuando ESE episodio está completo, no cuando lo esté el pack entero.
/// </summary>
public sealed class TorrentProgresoParcialTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "ALT_torrent_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_raiz, recursive: true); } catch { /* limpieza best-effort */ }
    }

    [Fact]
    public async Task UnPackConSoloElEpisodioPedidoEnDisco_CuentaComoCompleto()
    {
        // Cada archivo ocupa piezas enteras: ninguna pieza del episodio pedido depende del otro archivo.
        const int pieza = 32 * 1024;
        string pack = Path.Combine(_raiz, "pack");
        Directory.CreateDirectory(pack);
        var azar = new Random(7);
        foreach (var nombre in new[] { "Anime - 01.mkv", "Anime - 02.mkv" })
        {
            var datos = new byte[pieza * 4];
            azar.NextBytes(datos);
            File.WriteAllBytes(Path.Combine(pack, nombre), datos);
        }

        var creador = new TorrentCreator(TorrentType.V1Only) { PieceLength = pieza };
        var torrent = Torrent.Load(await creador.CreateAsync(new TorrentFileSource(pack)));

        // Como al terminar de bajar solo el episodio 1: el resto del pack no está en disco.
        File.Delete(Path.Combine(pack, "Anime - 02.mkv"));

        using var motor = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_raiz, "cache"),
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            AutoSaveLoadDhtCache = false,
            AutoSaveLoadFastResume = false,
            DhtEndPoint = null,
            ListenEndPoints = new(),
        }.ToSettings());
        var manager = await motor.AddAsync(torrent, _raiz);
        foreach (var archivo in manager.Files)
        {
            await manager.SetFilePriorityAsync(archivo, archivo.Path.EndsWith("01.mkv", StringComparison.Ordinal) ? Priority.Normal : Priority.DoNotDownload);
        }
        await manager.HashCheckAsync(autoStart: false);

        manager.Progress.Should().BeLessThan(100, "el progreso del torrent entero nunca llega a 100 si parte del pack no se descarga");
        TorrentDownloadService.ProgresoDeLoPedido(manager).Should().Be(100, "el único episodio pedido ya está completo");
    }
}
