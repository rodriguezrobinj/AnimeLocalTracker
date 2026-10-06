using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>Qué episodios se borran tras terminar de ver uno, según el modo (docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md, parte 2).</summary>
public class LimpiezaTrasVerTests
{
    private static int[] De(params int[] numeros) => numeros;

    [Fact]
    public void Apagado_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Apagado, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otra cosa")]
    public void UnModoRaro_NoBorraNada(string? modo)
    {
        LimpiezaTrasVer.Decidir(modo!, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().BeEmpty();
    }

    [Fact]
    public void Automatico_BorraSoloElQueSeAcabaDeVer()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Automatico, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().Equal(5);
    }

    [Fact]
    public void Automatico_SiElEpisodioNoTieneArchivo_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Automatico, 3, 5, 12, De(1, 2, 3)).Should().BeEmpty();
    }

    [Fact]
    public void ConsumoLigero_ConservaLosNDeNumeroMasAlto()
    {
        // Vistos del 1 al 8 con N = 3: se quedan 6, 7 y 8.
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 3, 8, 12, De(1, 2, 3, 4, 5, 6, 7, 8)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void ConsumoLigero_ConPocosVistos_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 3, 2, 12, De(1, 2)).Should().BeEmpty();
    }

    [Fact]
    public void ConsumoLigero_NuncaConservaMenosDeUno()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 0, 4, 12, De(1, 2, 3, 4)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void AlCompletarSerie_EnElUltimoEpisodio_BorraTodosLosVistos()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 12, 12, De(3, 1, 2)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void AlCompletarSerie_SiAunNoEsElUltimo_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 7, 12, De(1, 2, 3, 4, 5, 6, 7)).Should().BeEmpty();
    }

    [Fact]
    public void AlCompletarSerie_ConTotalDesconocido_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 12, 0, De(1, 2, 3, 12)).Should().BeEmpty();
    }
}
