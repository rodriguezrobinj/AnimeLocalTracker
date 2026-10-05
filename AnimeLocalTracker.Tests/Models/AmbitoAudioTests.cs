using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

/// <summary>Ajustes de sonido del reproductor: iguales para todo, por anime o por capítulo.</summary>
public class AmbitoAudioTests
{
    [Theory]
    [InlineData(null, AmbitoAudio.Global)]
    [InlineData("", AmbitoAudio.Global)]
    [InlineData("algo raro", AmbitoAudio.Global)]
    [InlineData(AmbitoAudio.Global, AmbitoAudio.Global)]
    [InlineData(AmbitoAudio.PorAnime, AmbitoAudio.PorAnime)]
    [InlineData(AmbitoAudio.PorCapitulo, AmbitoAudio.PorCapitulo)]
    public void Normalizar_SoloAceptaLosTresValores(string? valor, string esperado) =>
        AmbitoAudio.Normalizar(valor).Should().Be(esperado);

    [Fact]
    public void Global_NoBuscaNadaPropio_YGuardaEnLosAjustes()
    {
        AmbitoAudio.EpisodiosDondeBuscar(AmbitoAudio.Global, 101, 3).Should().BeEmpty();
        AmbitoAudio.EpisodioDondeGuardar(AmbitoAudio.Global, 101, 3).Should().BeNull();
    }

    [Fact]
    public void PorAnime_UsaLaFilaDelAnimeEntero()
    {
        AmbitoAudio.EpisodiosDondeBuscar(AmbitoAudio.PorAnime, 101, 3).Should().Equal(0);
        AmbitoAudio.EpisodioDondeGuardar(AmbitoAudio.PorAnime, 101, 3).Should().Be(0);
    }

    // Un capítulo sin ajustes propios empieza con los del anime (si los hay) antes de caer en los globales.
    [Fact]
    public void PorCapitulo_BuscaElCapituloYLuegoElAnime_YGuardaEnElCapitulo()
    {
        AmbitoAudio.EpisodiosDondeBuscar(AmbitoAudio.PorCapitulo, 101, 3).Should().Equal(3, 0);
        AmbitoAudio.EpisodioDondeGuardar(AmbitoAudio.PorCapitulo, 101, 3).Should().Be(3);
    }

    // Un video que no pertenece a ningún anime de la biblioteca no tiene dónde guardar lo suyo: vale lo global.
    [Theory]
    [InlineData(AmbitoAudio.PorAnime)]
    [InlineData(AmbitoAudio.PorCapitulo)]
    public void SinAnime_SeComportaComoGlobal(string ambito)
    {
        AmbitoAudio.EpisodiosDondeBuscar(ambito, 0, 3).Should().BeEmpty();
        AmbitoAudio.EpisodioDondeGuardar(ambito, 0, 3).Should().BeNull();
    }
}
