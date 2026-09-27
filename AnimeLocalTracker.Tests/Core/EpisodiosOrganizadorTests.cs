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

    // === CalcularMaxEpisodio ===

    private static List<EpisodioItem> Archivo(int numero) => new() { new() { NumeroEpisodio = numero } };

    [Fact]
    public void CalcularMaxEpisodio_ConTotalOficialConocido_ManaAunqueHayaUnArchivoLocalConNumeroAbsurdo()
    {
        // El caso reportado: una carpeta con 12 episodios reales pero un archivo mal nombrado "Episodio 3000.mp4".
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 12, encontrados: Archivo(3000), episodiosVistos: 5);

        resultado.Should().Be(12, "el archivo de 3000 no debe agrandar la lista: el total oficial de AniList manda");
    }

    [Fact]
    public void CalcularMaxEpisodio_SinTotalOficial_ConfiaEnElArchivoLocal()
    {
        // Anime recién añadido, aún sin sincronizar con AniList: no hay con qué comparar, así que se confía en el disco.
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 0, encontrados: Archivo(12), episodiosVistos: 0);

        resultado.Should().Be(12);
    }

    [Fact]
    public void CalcularMaxEpisodio_ConLoVistoPorEncimaDelOficial_ExtiendeLaLista()
    {
        // El total oficial puede estar desactualizado; si ya se vieron más episodios de los que registra, esas filas no deben desaparecer.
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 12, encontrados: [], episodiosVistos: 13);

        resultado.Should().Be(13);
    }

    [Fact]
    public void CalcularMaxEpisodio_ConUnArchivoLocalDentroDelRangoOficial_NoLoRecorta()
    {
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 24, encontrados: Archivo(24), episodiosVistos: 0);

        resultado.Should().Be(24);
    }

    [Theory]
    [InlineData(13)] // preestreno/filtración de un episodio 1-2 días antes de que AniList lo cuente como emitido
    [InlineData(15)] // un par de especiales numerados justo después del último episodio regular
    [InlineData(17)] // el borde exacto del margen (12 + MargenEpisodiosAdelantados)
    public void CalcularMaxEpisodio_ConUnArchivoLocalUnPocoPorEncimaDelOficial_SeSigueMostrando(int numeroLocal)
    {
        // Antes de este ajuste, capar en seco en el total oficial también habría ocultado un episodio real que ya
        // tienes (filtrado o de preestreno) solo porque AniList todavía no lo cuenta como emitido.
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 12, encontrados: Archivo(numeroLocal), episodiosVistos: 0);

        resultado.Should().Be(numeroLocal);
    }

    [Fact]
    public void CalcularMaxEpisodio_JustoUnEpisodioMasAlladelMargen_YaNoSeMuestra()
    {
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 12, encontrados: Archivo(18), episodiosVistos: 0);

        resultado.Should().Be(12, "18 ya está fuera del margen razonable de preestreno/especiales: no es un caso de filtración, es ruido");
    }

    [Fact]
    public void CalcularMaxEpisodio_UnArchivoAbsurdamenteAltoSigueIgnorandoseAunqueHayaOtrosDentroDelMargen()
    {
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: 12, encontrados: [.. Archivo(13), .. Archivo(4000)], episodiosVistos: 0);

        resultado.Should().Be(13, "el 13 (preestreno plausible) sí cuenta; el 4000 (ruido) no debe colarse solo porque el 13 abrió la puerta");
    }

    [Fact]
    public void CalcularMaxEpisodio_SinTotalNiArchivosNiVistos_EsCero()
    {
        EpisodiosOrganizador.CalcularMaxEpisodio(0, [], 0).Should().Be(0);
    }

    [Fact]
    public void CalcularMaxEpisodio_TotalOficialNegativoSeTrataComoDesconocido()
    {
        var resultado = EpisodiosOrganizador.CalcularMaxEpisodio(totalEpisodiosOficial: -1, encontrados: Archivo(4000), episodiosVistos: 0);

        resultado.Should().Be(4000, "un total negativo no es un dato oficial real: se cae al mismo camino que 'desconocido'");
    }
}
