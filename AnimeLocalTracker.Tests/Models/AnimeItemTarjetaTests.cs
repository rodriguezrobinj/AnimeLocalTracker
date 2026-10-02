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
