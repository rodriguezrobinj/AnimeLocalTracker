using System.Collections.Generic;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Selector de pistas de audio del reproductor: nombres legibles, idioma recordado y menú oculto con una sola pista.</summary>
public class ReproductorPistasAudioTests
{
    private static OpcionPistaAudio Opcion(string? idioma, bool actual = false) =>
        new(new object(), OpcionPistaAudio.ConstruirNombre(idioma, null, 1), idioma, actual);

    [Fact]
    public void ElNombreLlevaElIdiomaYElTituloDeLaPista()
    {
        string nombre = OpcionPistaAudio.ConstruirNombre("ja", "Original", 1);

        nombre.Should().EndWith(" · Original");
        nombre.Should().NotStartWith("ja", "se muestra el nombre del idioma, no el código");
    }

    [Fact]
    public void SinIdiomaNiTitulo_SeNumera()
    {
        OpcionPistaAudio.ConstruirNombre(null, "  ", 2).Should().Be("#2");
    }

    [Fact]
    public void ConUnCodigoDesconocido_SeMuestraTalCual()
    {
        OpcionPistaAudio.ConstruirNombre("zz-desconocido", null, 1).Should().Contain("zz");
    }

    [Fact]
    public void EligeLaPistaDelIdiomaRecordado()
    {
        var opciones = new List<OpcionPistaAudio> { Opcion("en", actual: true), Opcion("ja") };

        OpcionPistaAudio.ElegirPreferida(opciones, "ja").Should().BeSameAs(opciones[1]);
    }

    [Fact]
    public void SinPreferencia_ONingunaDeEseIdioma_ONoHayDondeElegir_NoCambiaNada()
    {
        var dos = new List<OpcionPistaAudio> { Opcion("en", actual: true), Opcion("ja") };

        OpcionPistaAudio.ElegirPreferida(dos, null).Should().BeNull();
        OpcionPistaAudio.ElegirPreferida(dos, "es").Should().BeNull();
        OpcionPistaAudio.ElegirPreferida(new List<OpcionPistaAudio> { Opcion("ja") }, "ja").Should().BeNull();
    }

    [Fact]
    public void SinVideoAbierto_ElMenuDeAudioNoSeMuestra()
    {
        using var sut = new ReproductorViewModel(Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>());

        sut.HayVariasPistasAudio.Should().BeFalse();
        sut.PistasAudio.Should().BeEmpty();
    }
}
