using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

public class AnimeBusquedaItemTests
{
    // LOC-08: estas 3 ramas antes devolvían literales en español ("eps", "Ep {n}+", "Eps desc.",
    // "Sin título") sin pasar por LocalizationService.T(...) — se comparan contra el propio
    // LocalizationService en vez de contra un literal fijo para no depender del idioma activo.

    [Fact]
    public void EpisodiosTexto_ConEpisodiosConocidos_DeberiaUsarClaveLocalizada()
    {
        var item = new AnimeBusquedaItem { Media = new AniListMedia { Episodes = 28 } };

        item.EpisodiosTexto.Should().Be(string.Format(LocalizationService.T("Add_EpisodiosBadgeFormato"), 28));
    }

    [Fact]
    public void EpisodiosTexto_SinEpisodiosPeroConProximaEmision_DeberiaUsarClaveLocalizada()
    {
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia { NextAiringEpisode = new AniListNextAiringEpisode { Episode = 6 } }
        };

        item.EpisodiosTexto.Should().Be(string.Format(LocalizationService.T("Add_ProximoEpisodioBadge"), 5));
    }

    [Fact]
    public void EpisodiosTexto_SinDatosDeEpisodios_DeberiaUsarClaveLocalizada()
    {
        var item = new AnimeBusquedaItem { Media = new AniListMedia() };

        item.EpisodiosTexto.Should().Be(LocalizationService.T("Add_EpisodiosDesconocidoBadge"));
    }

    [Fact]
    public void TituloPrincipal_SinNingunTitulo_DeberiaUsarClaveLocalizada()
    {
        var item = new AnimeBusquedaItem { Media = new AniListMedia { Title = new AniListTitle() } };

        item.TituloPrincipal.Should().Be(LocalizationService.T("Media_TituloDesconocido"));
    }
}
