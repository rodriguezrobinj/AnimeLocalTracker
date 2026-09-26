using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Minijuegos;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Minijuegos;

public class AdivinaAnimeJuegoTests
{
    private static AnimeItem Anime(int id, string titulo, string generos = "Action, Fantasy", int anio = 2020, int episodios = 12,
        string sinopsis = "Un joven parte de viaje para cambiar su destino y el de todos.", string alternativos = "")
        => new()
        {
            AniListId = id,
            Titulo = titulo,
            Generos = generos,
            AnioLanzamiento = anio,
            TotalEpisodios = episodios,
            Sinopsis = sinopsis,
            NombresAlternativos = alternativos,
            Temporada = "FALL"
        };

    private static List<AnimeItem> BibliotecaDe(params string[] titulos) =>
        titulos.Select((t, i) => Anime(i + 1, t)).ToList();

    // === Puntuación y desenfoque ===

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(2, 80)]
    [InlineData(3, 60)]
    [InlineData(4, 40)]
    [InlineData(5, 20)]
    [InlineData(9, 20)]
    public void Puntos_DeberiaRestar20PorCadaPistaExtraSinBajarDe20(int pistas, int esperado)
    {
        AdivinaAnimeJuego.Puntos(pistas).Should().Be(esperado);
    }

    [Fact]
    public void RadioDesenfoque_DeberiaSerMaximoConUnaPistaYBajarConCadaPistaExtra()
    {
        AdivinaAnimeJuego.RadioDesenfoque(1, 5).Should().Be(30);

        var radios = Enumerable.Range(1, 5).Select(n => AdivinaAnimeJuego.RadioDesenfoque(n, 5)).ToList();
        radios.Should().BeInDescendingOrder();
        radios.Last().Should().BeGreaterThan(0, "con todas las pistas la portada sigue algo borrosa hasta que respondes");
    }

    [Fact]
    public void RadioDesenfoque_SinPistas_DeberiaSerCero()
    {
        AdivinaAnimeJuego.RadioDesenfoque(1, 0).Should().Be(0);
    }

    // === Franquicias y normalización ===

    [Theory]
    [InlineData("One Piece", "One Piece Film: Red", true)]
    [InlineData("Kanojo, Okarishimasu", "Kanojo, Okarishimasu 2nd Season", true)]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu", "Re:Zero kara Hajimeru Kyuukei Jikan", true)]
    [InlineData("Dungeon Meshi", "Dungeon ni Deai wo Motomeru no wa Machigatteiru Darou ka", false)]
    [InlineData("Naruto", "Bleach", false)]
    [InlineData("", "Bleach", false)]
    public void MismaFranquicia_DeberiaAgruparSeriesYNoConfundirAnimesDistintos(string a, string b, bool esperado)
    {
        AdivinaAnimeJuego.MismaFranquicia(a, b).Should().Be(esperado);
        AdivinaAnimeJuego.MismaFranquicia(b, a).Should().Be(esperado, "la relación es simétrica");
    }

    [Theory]
    [InlineData("Shingeki no Kyojin: Season 2", "shingeki no kyojin season 2")]
    [InlineData("Pokémon", "pokemon")]
    [InlineData("  Re:Zero  ", "re zero")]
    [InlineData(null, "")]
    public void NormalizarTitulo_DeberiaQuitarTildesSignosYEspaciosSobrantes(string? titulo, string esperado)
    {
        AdivinaAnimeJuego.NormalizarTitulo(titulo).Should().Be(esperado);
    }

    // === Censura de spoilers del título ===

    [Fact]
    public void CensurarTitulos_DeberiaTaparElTituloYSusAlias()
    {
        string texto = "Eren se une al cuerpo de exploración en Shingeki no Kyojin. En Attack on Titan la humanidad resiste.";

        string resultado = AdivinaAnimeJuego.CensurarTitulos(texto, ["Shingeki no Kyojin", "Attack on Titan"]);

        resultado.Should().NotContain("Shingeki").And.NotContain("Attack on Titan");
        resultado.Should().Contain(AdivinaAnimeJuego.MarcaCensura);
        resultado.Should().Contain("Eren se une", "solo se tapan los títulos, no el resto del texto");
    }

    [Fact]
    public void CensurarTitulos_DeberiaTaparLaVarianteSinTemporadaYSinSubtitulo()
    {
        AdivinaAnimeJuego.CensurarTitulos("Kanojo, Okarishimasu vuelve con más enredos.", ["Kanojo, Okarishimasu 2nd Season"])
            .Should().NotContain("Okarishimasu");

        AdivinaAnimeJuego.CensurarTitulos("En Sword Art Online, Kirito juega de nuevo.", ["Sword Art Online: Alicization"])
            .Should().NotContain("Sword Art Online");
    }

    [Fact]
    public void CensurarTitulos_NoDeberiaTocarPalabrasQueSoloContienenElTitulo()
    {
        // "Monster" es un título, pero "Monsters" (otra palabra) no debe quedar mutilada.
        AdivinaAnimeJuego.CensurarTitulos("Los Monsters aparecen de noche.", ["Monster"])
            .Should().Be("Los Monsters aparecen de noche.");
    }

    [Fact]
    public void CensurarTitulos_DeberiaIgnorarTitulosMuyCortos()
    {
        // Tapar "Ai" destrozaría medio texto.
        AdivinaAnimeJuego.CensurarTitulos("Ai es una chica que canta.", ["Ai"])
            .Should().Be("Ai es una chica que canta.");
    }

    [Fact]
    public void SinopsisCensurada_DeberiaQuitarHtmlCensurarYRecortar()
    {
        var anime = Anime(1, "Death Note",
            sinopsis: "<b>Light</b> encuentra el cuaderno de Death Note.<br>" + string.Concat(Enumerable.Repeat("Sigue una larga historia de justicia y engaño. ", 20)));

        string resultado = AdivinaAnimeJuego.SinopsisCensurada(anime);

        resultado.Should().NotContain("<").And.NotContain("Death Note");
        resultado.Should().Contain(AdivinaAnimeJuego.MarcaCensura);
        resultado.Length.Should().BeLessThanOrEqualTo(302);
        resultado.Should().EndWith("…");
    }

    // === Pistas ===

    [Fact]
    public void ConstruirPistas_ConTodosLosDatos_DeberiaDevolverCincoPistasDeMenosAMasReveladora()
    {
        var extra = new DatosExtraAnime { Estudio = "MAPPA", Formato = "TV", Fuente = "MANGA" };

        var pistas = AdivinaAnimeJuego.ConstruirPistas(Anime(1, "Frieren"), extra);

        pistas.Select(p => p.Tipo).Should().Equal(
            TipoPista.Generos, TipoPista.Estreno, TipoPista.Episodios, TipoPista.Produccion, TipoPista.Sinopsis);
        pistas[0].Valor.Should().Be("Action, Fantasy");
        pistas[1].Valor.Should().Be("2020");
        pistas[1].Extra.Should().Be("FALL");
        pistas[2].Valor.Should().Be("12");
        pistas[3].Valor.Should().Be("MAPPA");
        pistas[3].Extra.Should().Be("TV");
        pistas[3].Extra2.Should().Be("MANGA");
    }

    [Fact]
    public void ConstruirPistas_SinDatosExtra_DeberiaOmitirLaPistaDeProduccion()
    {
        AdivinaAnimeJuego.ConstruirPistas(Anime(1, "Frieren"), null)
            .Select(p => p.Tipo).Should().NotContain(TipoPista.Produccion);
    }

    [Fact]
    public void ConstruirPistas_DeberiaOmitirLasQueElAnimeNoTiene()
    {
        var anime = new AnimeItem { AniListId = 1, Titulo = "Vacío", Generos = "", AnioLanzamiento = 0, TotalEpisodios = 0, Sinopsis = "corta" };

        AdivinaAnimeJuego.ConstruirPistas(anime, new DatosExtraAnime()).Should().BeEmpty(
            "sin géneros, año, episodios, estudio ni sinopsis suficiente no hay nada que preguntar");
    }

    [Fact]
    public void ConstruirPistas_SinTemporada_DeberiaDejarElExtraDeEstrenoNulo()
    {
        var anime = Anime(1, "X");
        anime.Temporada = "";

        AdivinaAnimeJuego.ConstruirPistas(anime, null).Single(p => p.Tipo == TipoPista.Estreno).Extra.Should().BeNull();
    }

    // === Distractores y rondas ===

    [Fact]
    public void ElegirDistractores_DeberiaDevolverTresDistintosSinLaRespuestaNiSuFranquicia()
    {
        var biblioteca = BibliotecaDe("One Piece", "One Piece Film: Red", "Naruto", "Bleach", "Death Note", "Clannad", "One Piece Stampede");
        var respuesta = biblioteca[0];

        for (int semilla = 0; semilla < 25; semilla++)
        {
            var distractores = AdivinaAnimeJuego.ElegirDistractores(respuesta, biblioteca, 3, new Random(semilla));

            distractores.Should().HaveCount(3);
            distractores.Should().NotContain(respuesta);
            distractores.Select(d => d.Titulo).Should().OnlyContain(t => !t.StartsWith("One Piece"), "son de la misma franquicia que la respuesta");
            distractores.Select(d => d.AniListId).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void ElegirDistractores_DeberiaPreferirDosDeGenerosParecidos()
    {
        var respuesta = Anime(1, "Respuesta", generos: "Action, Fantasy");
        var biblioteca = new List<AnimeItem>
        {
            respuesta,
            Anime(2, "Parecido Uno", generos: "Action, Fantasy"),
            Anime(3, "Parecido Dos", generos: "Action"),
            Anime(4, "Comedia Alfa", generos: "Comedy"),
            Anime(5, "Comedia Beta", generos: "Comedy"),
            Anime(6, "Comedia Gamma", generos: "Slice of Life")
        };

        for (int semilla = 0; semilla < 25; semilla++)
        {
            var distractores = AdivinaAnimeJuego.ElegirDistractores(respuesta, biblioteca, 3, new Random(semilla));

            distractores.Select(d => d.Titulo).Should().Contain(["Parecido Uno", "Parecido Dos"]);
        }
    }

    [Fact]
    public void CrearRonda_DeberiaArmarCuatroOpcionesConLaRespuestaEnElIndiceIndicado()
    {
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note", "Clannad", "Monster");

        for (int semilla = 0; semilla < 25; semilla++)
        {
            var ronda = AdivinaAnimeJuego.CrearRonda(biblioteca[0], biblioteca, null, new Random(semilla));

            ronda.Should().NotBeNull();
            ronda!.Opciones.Should().HaveCount(AdivinaAnimeJuego.OpcionesPorRonda);
            ronda.Opciones.Select(o => o.AniListId).Should().OnlyHaveUniqueItems();
            ronda.Opciones[ronda.IndiceCorrecto].Should().BeSameAs(biblioteca[0]);
        }
    }

    [Fact]
    public void CrearRonda_ConMismaSemilla_DeberiaSerReproducible()
    {
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama");

        var a = AdivinaAnimeJuego.CrearRonda(biblioteca[0], biblioteca, null, new Random(7))!;
        var b = AdivinaAnimeJuego.CrearRonda(biblioteca[0], biblioteca, null, new Random(7))!;

        a.Opciones.Select(o => o.AniListId).Should().Equal(b.Opciones.Select(o => o.AniListId));
    }

    [Fact]
    public void CrearRonda_ConPocosDistractoresDisponibles_DeberiaDevolverNulo()
    {
        // 3 animes: la respuesta + 2 = solo 2 distractores posibles (hacen falta 3).
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note");

        AdivinaAnimeJuego.CrearRonda(biblioteca[0], biblioteca, null, new Random(1)).Should().BeNull();
    }

    [Fact]
    public void CrearRonda_SiLosDemasSonDeLaMismaFranquicia_DeberiaDevolverNulo()
    {
        var biblioteca = BibliotecaDe("One Piece", "One Piece Film: Red", "One Piece Film: Gold", "One Piece Stampede");

        AdivinaAnimeJuego.CrearRonda(biblioteca[0], biblioteca, null, new Random(1)).Should().BeNull(
            "todas las opciones serían el mismo anime y la respuesta sería ambigua");
    }

    // === Selección de respuestas ===

    [Fact]
    public void ElegirRespuestas_DeberiaLimitarAlNumeroDeRondasSinRepetir()
    {
        var biblioteca = Enumerable.Range(1, 30).Select(i => Anime(i, $"Anime {(char)('A' + i)}{i}")).ToList();

        var respuestas = AdivinaAnimeJuego.ElegirRespuestas(biblioteca, 10, new Random(3));

        respuestas.Should().HaveCount(10);
        respuestas.Select(a => a.AniListId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ElegirRespuestas_DeberiaDescartarAnimesSinDatosSuficientes()
    {
        var pobre = new AnimeItem { AniListId = 99, Titulo = "Sin Datos" };
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note", "Clannad");
        biblioteca.Add(pobre);

        AdivinaAnimeJuego.ElegirRespuestas(biblioteca, 10, new Random(1)).Should().NotContain(pobre);
    }

    [Fact]
    public void PuedeJugar_DeberiaExigirAlMenosCuatroAnimesUtilizables()
    {
        AdivinaAnimeJuego.PuedeJugar(BibliotecaDe("A1", "B2", "C3")).Should().BeFalse();
        AdivinaAnimeJuego.PuedeJugar(BibliotecaDe("A1", "B2", "C3", "D4")).Should().BeTrue();

        var conInutilizables = BibliotecaDe("A1", "B2", "C3");
        conInutilizables.Add(new AnimeItem { AniListId = 0, Titulo = "Sin id" });
        conInutilizables.Add(new AnimeItem { AniListId = 9, Titulo = " " });
        AdivinaAnimeJuego.PuedeJugar(conInutilizables).Should().BeFalse("los animes sin id o sin título no cuentan");
    }
}
