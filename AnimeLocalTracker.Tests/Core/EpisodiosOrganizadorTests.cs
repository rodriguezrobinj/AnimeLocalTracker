using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// ARQ-01: lógica pura de filtrado/orden de episodios extraída de DetalleViewModel.
/// </summary>
public class EpisodiosOrganizadorTests
{
    private static List<EpisodioItem> CrearEpisodios()
    {
        return new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, Visto = true, Descargado = true, Favorito = false },
            new() { NumeroEpisodio = 2, Visto = false, Descargado = false, Favorito = true },
            new() { NumeroEpisodio = 3, Visto = true, Descargado = false, Favorito = false },
            new() { NumeroEpisodio = 4, Visto = false, Descargado = true, Favorito = true }
        };
    }

    [Fact]
    public void FiltrarYOrdenar_SinFiltroDescendente_DeberiaDevolverTodosEnOrdenInverso()
    {
        // Act
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "Todos", ordenAscendente: false);

        // Assert
        resultado.Select(e => e.NumeroEpisodio).Should().Equal(4, 3, 2, 1);
    }

    [Fact]
    public void FiltrarYOrdenar_FiltroDescargadosAscendente_DeberiaDevolverSoloDescargados()
    {
        // Act
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "Descargados", ordenAscendente: true);

        // Assert
        resultado.Select(e => e.NumeroEpisodio).Should().Equal(1, 4);
    }

    [Fact]
    public void FiltrarYOrdenar_FiltroVistos_DeberiaDevolverSoloVistos()
    {
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "Vistos", ordenAscendente: true);
        resultado.Select(e => e.NumeroEpisodio).Should().Equal(1, 3);
    }

    [Fact]
    public void FiltrarYOrdenar_FiltroNoVistos_DeberiaExcluirLosVistos()
    {
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "No Vistos", ordenAscendente: true);
        resultado.Select(e => e.NumeroEpisodio).Should().Equal(2, 4);
    }

    [Fact]
    public void FiltrarYOrdenar_FiltroFavoritos_DeberiaDevolverSoloFavoritos()
    {
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "Favoritos", ordenAscendente: true);
        resultado.Select(e => e.NumeroEpisodio).Should().Equal(2, 4);
    }

    [Fact]
    public void FiltrarYOrdenar_ListaVacia_DeberiaDevolverVacio()
    {
        EpisodiosOrganizador.FiltrarYOrdenar(new List<EpisodioItem>(), "Todos", true).Should().BeEmpty();
    }

    [Fact]
    public void FiltrarYOrdenar_FiltroDesconocido_DeberiaTratarseComoTodos()
    {
        var resultado = EpisodiosOrganizador.FiltrarYOrdenar(CrearEpisodios(), "Inexistente", ordenAscendente: true);
        resultado.Should().HaveCount(4);
    }
}
