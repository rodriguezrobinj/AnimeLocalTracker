using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Recomendaciones;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Tags de AniList guardados con el anime (para "Parecidos en tu biblioteca").</summary>
public class EtiquetasAnimeTests
{
    private static AniListTag Tag(string nombre, int rank, bool spoiler = false, bool spoilerGeneral = false)
        => new() { Name = nombre, Rank = rank, IsMediaSpoiler = spoiler, IsGeneralSpoiler = spoilerGeneral };

    [Fact]
    public void Serializar_SoloLosDe50OMasSinSpoilers_OrdenadosPorRank()
    {
        string guardado = EtiquetasAnime.Serializar(new[]
        {
            Tag("Thriller", 80), Tag("Time Manipulation", 95), Tag("Poco", 49),
            Tag("Destripe", 90, spoiler: true), Tag("Destripe general", 90, spoilerGeneral: true),
        });

        EtiquetasAnime.Leer(guardado).Should().Equal(("Time Manipulation", 95), ("Thriller", 80));
    }

    [Fact]
    public void Serializar_Maximo15()
        => EtiquetasAnime.Leer(EtiquetasAnime.Serializar(Enumerable.Range(0, 30).Select(i => Tag($"T{i}", 60 + i)))).Should().HaveCount(15);

    [Fact]
    public void NombresConSeparadores_SobrevivenIdaYVuelta()
        => EtiquetasAnime.Leer(EtiquetasAnime.Serializar(new[] { Tag("Raro: a|b \"c\"", 70) })).Should().Equal(("Raro: a|b \"c\"", 70));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no es json")]
    public void Leer_VacioONulo_ListaVacia(string? guardado) => EtiquetasAnime.Leer(guardado).Should().BeEmpty();

    [Fact]
    public void RespuestaDeAniList_TraeLosTags()
    {
        const string json = """{"id":9253,"tags":[{"name":"Time Manipulation","rank":95,"isMediaSpoiler":false,"isGeneralSpoiler":false}]}""";

        var media = JsonSerializer.Deserialize<AniListMedia>(json)!;

        media.Tags.Should().ContainSingle().Which.Should().BeEquivalentTo(Tag("Time Manipulation", 95));
    }

    [Fact]
    public void AplicarDatosDeAniList_CopiaLasEtiquetasYMarcaElAnimeComoModificado()
    {
        var anime = new AnimeItem { AniListId = 9253, Titulo = "Steins;Gate", TotalEpisodios = 24, Estado = "FINISHED" };
        var datos = new Dictionary<int, AniListMedia>
        {
            [9253] = new() { Id = 9253, Episodes = 24, Status = "FINISHED", Tags = new() { Tag("Time Manipulation", 95) } },
        };

        var (modificados, _) = GaleriaViewModel.AplicarDatosDeAniList(new[] { anime }, datos, conCuenta: false, new HashSet<int>());

        modificados.Should().ContainSingle();
        EtiquetasAnime.Leer(anime.Etiquetas).Should().Equal(("Time Manipulation", 95));
    }

    [Theory]
    [InlineData("FINISHED", "SPRING", 2020, null, true)]         // sin tags todavía (base anterior a v24): se piden
    [InlineData("FINISHED", "SPRING", 2020, "[]", false)]        // AniList no tiene tags para él: no se vuelve a pedir
    [InlineData("FINISHED", "SPRING", 2020, "[{\"n\":\"Thriller\",\"r\":80}]", false)]
    [InlineData("NOT_YET_RELEASED", "SPRING", 2020, "[]", true)] // sin estrenar: como antes
    [InlineData("FINISHED", "", 0, "[]", true)]                  // sin temporada ni año: como antes
    public void NecesitaDatosDeAniList_IncluyeLosQueNoTienenTags(string estado, string temporada, int anio, string? etiquetas, bool necesita)
        => GaleriaViewModel.NecesitaDatosDeAniList(new AnimeItem { Estado = estado, Temporada = temporada, AnioLanzamiento = anio, Etiquetas = etiquetas })
            .Should().Be(necesita);

    [Fact]
    public void AplicarDatosDeAniList_SinElCampoTags_ConservaLasQueHabia()
    {
        string previas = EtiquetasAnime.Serializar(new[] { Tag("Thriller", 80) });
        var anime = new AnimeItem { AniListId = 1, TotalEpisodios = 12, Estado = "FINISHED", Etiquetas = previas };
        var datos = new Dictionary<int, AniListMedia> { [1] = new() { Id = 1, Episodes = 12, Status = "FINISHED", Tags = null } };

        var (modificados, _) = GaleriaViewModel.AplicarDatosDeAniList(new[] { anime }, datos, conCuenta: false, new HashSet<int>());

        modificados.Should().BeEmpty();
        anime.Etiquetas.Should().Be(previas);
    }
}
