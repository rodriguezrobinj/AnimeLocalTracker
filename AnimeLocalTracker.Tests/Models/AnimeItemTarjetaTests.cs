using System.Collections.Generic;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

/// <summary>Lo que la tarjeta de la Galería enseña de cada anime: tu estado con él y en qué orden de estreno queda.</summary>
public class AnimeItemTarjetaTests
{
    [Theory]
    [InlineData("CURRENT", "Viendo")]
    [InlineData("REPEATING", "Viendo de nuevo")]
    [InlineData("COMPLETED", "Completado")]
    [InlineData("PAUSED", "En Pausa")]
    [InlineData("DROPPED", "Abandonado")]
    [InlineData("PLANNING", "Planeando")]
    [InlineData("", "Planeando")] // recién añadido, aún sin estado
    public void EstadoUsuarioVisual_DiceTuEstadoConElAnime(string estado, string esperado) =>
        new AnimeItem { EstadoUsuario = estado }.EstadoUsuarioVisual.Should().Be(esperado);

    [Theory]
    [InlineData("RELEASING", "En Emisión")]
    [InlineData("FINISHED", "Finalizado")]
    [InlineData("NOT_YET_RELEASED", "Próximamente")] // antes "Desconocido": un anime sin estrenar no es un dato que falte
    [InlineData("UNKNOWN", "Desconocido")]
    public void EstadoVisual_DiceElEstadoDeEmision(string estado, string esperado) =>
        new AnimeItem { Estado = estado }.EstadoVisual.Should().Be(esperado);

    // "3 de 1 vistos": el total guardado se quedó atrás cuando salieron los capítulos 2 y 3. Un episodio visto ya salió, pero
    // solo se corrige lo que se pinta: el total guardado no se toca.
    [Fact]
    public void LoVisto_NoSeMuestraPorEncimaDelTotalConocido()
    {
        var anime = new AnimeItem { TotalEpisodios = 1 };

        anime.EpisodiosVistos = 3;

        anime.ProgresoEpisodiosTexto.Should().Be("3 de 3 vistos");
        anime.ProgresoPorcentaje.Should().Be(100);
        anime.TotalEpisodios.Should().Be(1);
    }

    [Fact]
    public void SiAniListCorrigeElTotalAlaBaja_ElTotalGuardadoLoRespeta()
    {
        var anime = new AnimeItem { TotalEpisodios = 24, EpisodiosVistos = 24 };

        anime.TotalEpisodios = 12; // AniList parte la serie en temporadas

        anime.TotalEpisodios.Should().Be(12);
    }

    [Fact]
    public void ConTotalDesconocido_NoSeInventaElTotal()
    {
        var anime = new AnimeItem { TotalEpisodios = 0 };

        anime.EpisodiosVistos = 4;

        anime.TotalEpisodios.Should().Be(0);
        anime.ProgresoPorcentaje.Should().Be(0);
    }

    [Fact]
    public void AlCambiarElEstado_LaTarjetaSeEntera()
    {
        var anime = new AnimeItem { EstadoUsuario = "PLANNING" };
        var avisos = new List<string?>();
        anime.PropertyChanged += (_, e) => avisos.Add(e.PropertyName);

        anime.EstadoUsuario = "COMPLETED";

        avisos.Should().Contain(nameof(AnimeItem.EstadoUsuarioVisual));
    }

    [Fact]
    public void OrdenEstreno_VaPorAnioYLuegoPorTemporada()
    {
        var invierno2024 = new AnimeItem { AnioLanzamiento = 2024, Temporada = "WINTER" };
        var otono2024 = new AnimeItem { AnioLanzamiento = 2024, Temporada = "FALL" };
        var verano2023 = new AnimeItem { AnioLanzamiento = 2023, Temporada = "SUMMER" };
        var sinDatos = new AnimeItem();

        otono2024.OrdenEstreno.Should().BeGreaterThan(invierno2024.OrdenEstreno);
        invierno2024.OrdenEstreno.Should().BeGreaterThan(verano2023.OrdenEstreno);
        verano2023.OrdenEstreno.Should().BeGreaterThan(sinDatos.OrdenEstreno, "sin año conocido va al final");
    }
}
