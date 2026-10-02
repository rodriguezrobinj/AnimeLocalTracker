using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

public class AniListMediaEpisodiosEmitidosTests
{
    [Fact]
    public void SinEstrenar_Es0() =>
        new AniListMedia { Status = "NOT_YET_RELEASED", Episodes = 12 }.EpisodiosEmitidos(totalConocido: 5).Should().Be(0);

    [Fact]
    public void ConProximoEpisodioProgramado_EsElAnterior() =>
        new AniListMedia { Status = "RELEASING", Episodes = 24, NextAiringEpisode = new() { Episode = 8 } }
            .EpisodiosEmitidos(totalConocido: 3).Should().Be(7);

    [Fact]
    public void Finalizado_EsElTotalDeAniList() =>
        new AniListMedia { Status = "FINISHED", Episodes = 12 }.EpisodiosEmitidos(totalConocido: 10).Should().Be(12);

    [Theory]
    [InlineData("RELEASING")] // serie larga en pausa: AniList no da total ni próximo episodio
    [InlineData("FINISHED")]
    [InlineData(null)]
    public void SinProximoNiTotal_ConservaElQueYaSeConocia(string? estado) =>
        new AniListMedia { Status = estado, Episodes = null, NextAiringEpisode = null }
            .EpisodiosEmitidos(totalConocido: 1180).Should().Be(1180);
}
