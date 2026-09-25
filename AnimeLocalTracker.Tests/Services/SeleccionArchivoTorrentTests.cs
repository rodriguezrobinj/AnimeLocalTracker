using System.Collections.Generic;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Lógica pura de <see cref="SeleccionArchivoTorrent"/> — sin MonoTorrent real, solo
/// listas de (ruta, tamaño), tal como se ensamblan desde <c>TorrentManager.Files</c>
/// en <see cref="TorrentDownloadService"/>.
/// </summary>
public class SeleccionArchivoTorrentTests
{
    [Fact]
    public void ElegirArchivoDeVideo_ConUnSoloArchivoDeVideo_LoElige()
    {
        var archivos = new List<(string, long)>
        {
            ("[SubsPlease] Anime - 13 (1080p).mkv", 500_000_000L),
            ("[SubsPlease] Anime - 13 (1080p).mkv.nfo", 1024L),
        };

        SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivos).Should().Be("[SubsPlease] Anime - 13 (1080p).mkv");
    }

    [Fact]
    public void ElegirArchivoDeVideo_ConVariosArchivosDeVideo_EligeElMasGrande()
    {
        var archivos = new List<(string, long)>
        {
            ("Anime - 13 (480p).mp4", 100_000_000L),
            ("Anime - 13 (1080p).mkv", 800_000_000L),
            ("Anime - 13 (720p).mp4", 400_000_000L),
        };

        SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivos).Should().Be("Anime - 13 (1080p).mkv");
    }

    [Fact]
    public void ElegirArchivoDeVideo_IgnoraArchivosQueNoSonDeVideo()
    {
        var archivos = new List<(string, long)>
        {
            ("Anime - 13.mkv", 500_000_000L),
            ("subs/Anime - 13.ass", 2_000_000_000L), // más grande, pero no es video
            ("cover.jpg", 5_000_000_000L),            // más grande aún, tampoco es video
        };

        SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivos).Should().Be("Anime - 13.mkv");
    }

    [Fact]
    public void ElegirArchivoDeVideo_SinNingunArchivoDeVideo_DevuelveNull()
    {
        var archivos = new List<(string, long)>
        {
            ("readme.txt", 100L),
            ("cover.jpg", 200_000L),
        };

        SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivos).Should().BeNull();
    }

    [Fact]
    public void ElegirArchivoDeVideo_ConListaVacia_DevuelveNull()
    {
        SeleccionArchivoTorrent.ElegirArchivoDeVideo(new List<(string, long)>()).Should().BeNull();
    }

    [Theory]
    [InlineData("Anime.MKV")]
    [InlineData("Anime.Mp4")]
    [InlineData("Anime.AVI")]
    public void ElegirArchivoDeVideo_ReconoceExtensionesSinImportarMayusculas(string ruta)
    {
        var archivos = new List<(string, long)> { (ruta, 100L) };

        SeleccionArchivoTorrent.ElegirArchivoDeVideo(archivos).Should().Be(ruta);
    }

    [Fact]
    public void ElegirArchivoDelEpisodio_DentroDeUnBatch_EligeElArchivoQueCoincideConElNumero()
    {
        var archivos = new List<(string, long)>
        {
            ("[Grupo] Anime - 01 (1080p).mkv", 500_000_000L),
            ("[Grupo] Anime - 02 (1080p).mkv", 510_000_000L),
            ("[Grupo] Anime - 03 (1080p).mkv", 490_000_000L),
        };

        // El episodio 2 no es el más grande de la lista — debe elegirlo por nombre, no por tamaño.
        SeleccionArchivoTorrent.ElegirArchivoDelEpisodio(archivos, numeroEpisodio: 2)
            .Should().Be("[Grupo] Anime - 02 (1080p).mkv");
    }

    [Fact]
    public void ElegirArchivoDelEpisodio_SinNingunArchivoQueCoincida_DevuelveNull()
    {
        var archivos = new List<(string, long)>
        {
            ("[Grupo] Anime - 01 (1080p).mkv", 500_000_000L),
            ("[Grupo] Anime - 02 (1080p).mkv", 510_000_000L),
        };

        // Se pide el episodio 7, que no está en este batch.
        SeleccionArchivoTorrent.ElegirArchivoDelEpisodio(archivos, numeroEpisodio: 7).Should().BeNull();
    }

    [Fact]
    public void ElegirArchivoDelEpisodio_IgnoraArchivosQueNoSonDeVideo()
    {
        var archivos = new List<(string, long)>
        {
            ("[Grupo] Anime - 02.ass", 50_000L), // subtítulo suelto del episodio 2 — no es video
            ("[Grupo] Anime - 02 (1080p).mkv", 500_000_000L),
        };

        SeleccionArchivoTorrent.ElegirArchivoDelEpisodio(archivos, numeroEpisodio: 2)
            .Should().Be("[Grupo] Anime - 02 (1080p).mkv");
    }
}
