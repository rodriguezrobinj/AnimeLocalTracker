using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.Tests.Services;

public class FileScannerServiceTests
{
    [Theory]
    [InlineData("[Erai-raws] Boku no Hero Academia - 138 [1080p][Multiple Subtitle].mkv", 138)]
    [InlineData("Naruto Shippuden Ep 05.mp4", 5)]
    [InlineData("One Piece E1071.mkv", 1071)]
    [InlineData("Bleach - Episode 02.avi", 2)]
    [InlineData("Death Note Episodio 15 [720p].mkv", 15)]
    [InlineData("Dragon Ball Z Cap 01", 1)]
    [InlineData("Jujutsu Kaisen Capitulo 24", 24)]
    [InlineData("Shingeki no Kyojin - 87 (1080p).mkv", 87)]
    [InlineData("Solo Leveling 12.mkv", 12)]
    [InlineData("Frieren 04.mp4", 4)]
    [InlineData("Movie Name 1080p.mkv", 0)] // No debería detectar la resolución como episodio
    // Un número a secas es un episodio aunque coincida con una resolución: en series largas existe el episodio 1080.
    // (El escáner real ya lo aceptaba; lo rechazaba solo la primera de sus tres pasadas.)
    [InlineData("Episodio 1080", 1080)]
    [InlineData("Anime 480.mkv", 480)]
    [InlineData("Anime 720.mkv", 720)]
    // Lo que el daemon Python "rescataba" y estaba mal (nombres reales de fansub): especiales con decimales, películas y volúmenes.
    [InlineData("[Erai-raws] Youjo Senki - 06.5 [1080p][Multiple Subtitle]", 0)]
    [InlineData("[KAF-TEAM]_One_Piece_Movie_9_vostfr_HD", 0)]
    [InlineData("Evangelion_1.11_You_Are_(Not)_Alone_[1080p,BluRay,x264,DTS-ES]_-_THORA", 0)]
    [InlineData("[Harunatsu] Classroom Crisis - Vol.1 [BD 720p-AAC]", 0)]
    [InlineData("The iDOLM@STER 765 Pro to Iu Monogatari", 0)]
    public void ExtraerNumeroEpisodio_DeberiaDetectarEpisodioCorrecto(string fileName, int expectedEpisode)
    {
        // Act
        int result = FileScannerService.ExtraerNumeroEpisodio(fileName);

        // Assert
        result.Should().Be(expectedEpisode);
    }

    [Fact]
    public async Task EscanearEpisodios_NumeraSoloConElNucleoRust_SinDaemonPython()
    {
        string carpeta = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());
        Directory.CreateDirectory(carpeta);
        try
        {
            foreach (string nombre in new[] { "Episodio 07.mp4", "Episodio 1080.mp4", "[Erai-raws] Youjo Senki - 06 [1080p].mkv",
                         "[Erai-raws] Youjo Senki - 06.5 [1080p].mkv", "Serie - 03.webm", "notas.txt" })
            {
                await File.WriteAllBytesAsync(Path.Combine(carpeta, nombre), [0]);
            }

            var episodios = await new FileScannerService().EscanearEpisodiosAsync(carpeta);

            episodios.Select(e => e.NumeroEpisodio).Should().Equal(0, 3, 6, 7, 1080);
            episodios.Single(e => e.NumeroEpisodio == 6).TituloArchivo.Should().Be("[Erai-raws] Youjo Senki - 06 [1080p]",
                "el especial 06.5 no debe pasar por el episodio 6");
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }
}
