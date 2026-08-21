using System;
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
    [InlineData("Anime 480.mkv", 0)] // Resolución
    [InlineData("Anime 720.mkv", 0)] // Resolución
    public void ExtraerNumeroEpisodio_DeberiaDetectarEpisodioCorrecto(string fileName, int expectedEpisode)
    {
        // Act
        int result = FileScannerService.ExtraerNumeroEpisodio(fileName);

        // Assert
        result.Should().Be(expectedEpisode);
    }
}
