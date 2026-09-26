using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Minijuegos;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Minijuegos;

public class AdivinaOpEdJuegoTests
{
    private static AnimeItem Anime(int id, string titulo, string generos = "Action, Fantasy", int anio = 2020, string temporada = "FALL") =>
        new() { AniListId = id, Titulo = titulo, Generos = generos, AnioLanzamiento = anio, Temporada = temporada };

    private static List<AnimeItem> BibliotecaDe(params string[] titulos) =>
        titulos.Select((t, i) => Anime(i + 1, t)).ToList();

    private static AnimeThemeInfo Tema(string slug, int version = 1, bool spoiler = false, string audio = "https://a.animethemes.moe/x.ogg") => new()
    {
        Slug = slug,
        Tipo = slug.StartsWith("ED", StringComparison.OrdinalIgnoreCase) ? "ED" : "OP",
        Version = version,
        EsSpoiler = spoiler,
        AudioUrlOgg = audio
    };

    private static readonly TemaParaJugar TemaCompleto = new("OP", "OP1", "Guren no Yumiya", "Linked Horizon", @"C:\Music\1\OP1.mp3");
    private static readonly TemaParaJugar TemaSinDatos = new("OP", "OP1", "", "", @"C:\Music\1\OP1.mp3");

    // === Pasos de ayuda ===

    [Fact]
    public void ConstruirPasos_ConTodosLosDatos_DeberiaOrdenarDeMenosAMasReveladora()
    {
        var pasos = AdivinaOpEdJuego.ConstruirPasos(Anime(1, "Shingeki no Kyojin"), TemaCompleto);

        pasos.Select(p => p.Tipo).Should().Equal(
            TipoPasoOpEd.MasAudio, TipoPasoOpEd.Artista, TipoPasoOpEd.Estreno, TipoPasoOpEd.MasAudio, TipoPasoOpEd.Generos, TipoPasoOpEd.Cancion);
        pasos[0].Segundos.Should().Be(12);
        pasos[1].Valor.Should().Be("Linked Horizon");
        pasos[2].Valor.Should().Be("2020");
        pasos[2].Extra.Should().Be("FALL");
        pasos[3].Segundos.Should().Be(20);
        pasos[4].Valor.Should().Be("Action, Fantasy");
        pasos[5].Valor.Should().Be("Guren no Yumiya");
    }

    [Fact]
    public void ConstruirPasos_SinConexion_DeberiaOmitirArtistaYCancion()
    {
        var pasos = AdivinaOpEdJuego.ConstruirPasos(Anime(1, "X"), TemaSinDatos);

        pasos.Select(p => p.Tipo).Should().NotContain([TipoPasoOpEd.Artista, TipoPasoOpEd.Cancion]);
        pasos.Should().NotBeEmpty();
    }

    [Fact]
    public void ConstruirPasos_SiElAnimeNoTieneDatos_DeberiaQuedarseSoloConLosDeAudio()
    {
        var anime = new AnimeItem { AniListId = 1, Titulo = "Vacío", Generos = "", AnioLanzamiento = 0 };

        var pasos = AdivinaOpEdJuego.ConstruirPasos(anime, TemaSinDatos);

        pasos.Select(p => p.Tipo).Should().Equal(TipoPasoOpEd.MasAudio, TipoPasoOpEd.MasAudio);
    }

    [Fact]
    public void ConstruirPasos_SinTemporada_DeberiaDejarElExtraDelEstrenoNulo()
    {
        var pasos = AdivinaOpEdJuego.ConstruirPasos(Anime(1, "X", temporada: ""), TemaSinDatos);

        pasos.Single(p => p.Tipo == TipoPasoOpEd.Estreno).Extra.Should().BeNull();
    }

    [Fact]
    public void SegundosDeClip_DeberiaCrecerSoloConLosPasosDeAudioPedidos()
    {
        var pasos = AdivinaOpEdJuego.ConstruirPasos(Anime(1, "X"), TemaCompleto);

        AdivinaOpEdJuego.SegundosDeClip(pasos, 0).Should().Be(AdivinaOpEdJuego.SegundosIniciales);
        AdivinaOpEdJuego.SegundosDeClip(pasos, 1).Should().Be(12);
        AdivinaOpEdJuego.SegundosDeClip(pasos, 2).Should().Be(12, "pedir el artista no alarga el clip");
        AdivinaOpEdJuego.SegundosDeClip(pasos, 3).Should().Be(12);
        AdivinaOpEdJuego.SegundosDeClip(pasos, 4).Should().Be(20);
        AdivinaOpEdJuego.SegundosDeClip(pasos, 99).Should().Be(20);
        AdivinaOpEdJuego.SegundosDeClip(pasos, -3).Should().Be(AdivinaOpEdJuego.SegundosIniciales);
    }

    // === Elección del tema ===

    [Fact]
    public void ElegirTema_DeberiaDescartarSpoilersYTemasSinAudio()
    {
        var temas = new List<AnimeThemeInfo> { Tema("OP1", spoiler: true), Tema("ED1", audio: ""), Tema("OP2") };

        for (int semilla = 0; semilla < 20; semilla++)
            AdivinaOpEdJuego.ElegirTema(temas, _ => false, new Random(semilla))!.Slug.Should().Be("OP2");
    }

    [Fact]
    public void ElegirTema_SiTodosSonSpoilerOSinAudio_DeberiaDevolverNulo()
    {
        var temas = new List<AnimeThemeInfo> { Tema("OP1", spoiler: true), Tema("ED1", audio: " ") };

        AdivinaOpEdJuego.ElegirTema(temas, _ => false, new Random(1)).Should().BeNull();
        AdivinaOpEdJuego.ElegirTema([], _ => false, new Random(1)).Should().BeNull();
    }

    [Fact]
    public void ElegirTema_DeberiaContarCadaOpEdUnaSolaVezAunqueTengaVariasVersiones()
    {
        // OP1 con 3 versiones y ED1 con 1: sin agrupar, ED1 saldría ~25 % de las veces; agrupado, ~50 %.
        var temas = new List<AnimeThemeInfo> { Tema("OP1", 1), Tema("OP1", 2), Tema("OP1", 3), Tema("ED1", 1) };

        int ed = Enumerable.Range(0, 400).Count(s => AdivinaOpEdJuego.ElegirTema(temas, _ => false, new Random(s))!.Slug == "ED1");

        ed.Should().BeInRange(140, 260);
    }

    [Fact]
    public void ElegirTema_DeberiaPreferirLosYaDescargadosParaNoLlenarElDisco()
    {
        var temas = new List<AnimeThemeInfo> { Tema("OP1"), Tema("OP2"), Tema("ED1"), Tema("ED2") };

        for (int semilla = 0; semilla < 30; semilla++)
            AdivinaOpEdJuego.ElegirTema(temas, t => t.Slug == "ED2", new Random(semilla))!.Slug.Should().Be("ED2");
    }

    [Fact]
    public void ElegirTema_SiUnaVersionYaEstaDescargada_DeberiaDevolverEsaVersion()
    {
        var v1 = Tema("OP1", 1);
        var v2 = Tema("OP1", 2);

        AdivinaOpEdJuego.ElegirTema([v1, v2], t => ReferenceEquals(t, v2), new Random(1)).Should().BeSameAs(v2);
    }

    [Fact]
    public void ElegirTemaLocal_DeberiaElegirUnoPorSlugEIgnorarArchivosSinRuta()
    {
        var locales = new List<TemaLocalDisponible>
        {
            new("OP", "OP1", 1, null, @"C:\a\OP1_v1.mp3"),
            new("OP", "OP1", 2, null, @"C:\a\OP1_v2.mp3"),
            new("ED", "ED1", 1, null, ""),
        };

        for (int semilla = 0; semilla < 20; semilla++)
            AdivinaOpEdJuego.ElegirTemaLocal(locales, new Random(semilla))!.Slug.Should().Be("OP1");
    }

    [Fact]
    public void ElegirTemaLocal_SinTemas_DeberiaDevolverNulo()
    {
        AdivinaOpEdJuego.ElegirTemaLocal([], new Random(1)).Should().BeNull();
    }

    // === Rondas ===

    [Fact]
    public void CrearRonda_DeberiaArmarCuatroOpcionesConLaRespuestaEnSuIndice()
    {
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note", "Clannad", "Monster");

        for (int semilla = 0; semilla < 25; semilla++)
        {
            var ronda = AdivinaOpEdJuego.CrearRonda(biblioteca[0], TemaCompleto, biblioteca, new Random(semilla));

            ronda.Should().NotBeNull();
            ronda!.Opciones.Should().HaveCount(AdivinaAnimeJuego.OpcionesPorRonda);
            ronda.Opciones.Select(o => o.AniListId).Should().OnlyHaveUniqueItems();
            ronda.Opciones[ronda.IndiceCorrecto].Should().BeSameAs(biblioteca[0]);
            ronda.Tema.Should().Be(TemaCompleto);
            ronda.Pasos.Should().NotBeEmpty();
            ronda.PosicionInicio.Should().BeInRange(0.05, 0.40);
        }
    }

    [Fact]
    public void CrearRonda_ConPocosDistractores_DeberiaDevolverNulo()
    {
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note");

        AdivinaOpEdJuego.CrearRonda(biblioteca[0], TemaCompleto, biblioteca, new Random(1)).Should().BeNull();
    }

    [Fact]
    public void CrearRonda_ConMismaSemilla_DeberiaSerReproducible()
    {
        var biblioteca = BibliotecaDe("Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama");

        var a = AdivinaOpEdJuego.CrearRonda(biblioteca[0], TemaCompleto, biblioteca, new Random(9))!;
        var b = AdivinaOpEdJuego.CrearRonda(biblioteca[0], TemaCompleto, biblioteca, new Random(9))!;

        a.Opciones.Select(o => o.AniListId).Should().Equal(b.Opciones.Select(o => o.AniListId));
        a.PosicionInicio.Should().Be(b.PosicionInicio);
    }

    [Fact]
    public void PosicionAleatoria_DeberiaQuedarEntreElCincoYElCuarentaPorCiento()
    {
        for (int semilla = 0; semilla < 200; semilla++)
            AdivinaOpEdJuego.PosicionAleatoria(new Random(semilla)).Should().BeInRange(0.05, 0.40);
    }

    // === Slug del tema ===

    [Theory]
    [InlineData("OP2", "op", "OP", 2)]
    [InlineData("ED1-TV", "ED", "ED", 1)]
    [InlineData("op10", "OP", "OP", 10)]
    [InlineData("OP", "OP", "OP", null)]
    [InlineData("raro", "ed", "ED", null)]
    [InlineData("", "op", "OP", null)]
    public void ParsearSlug_DeberiaSacarTipoYNumero(string slug, string tipoPorDefecto, string tipoEsperado, int? numeroEsperado)
    {
        var (tipo, numero) = AdivinaOpEdJuego.ParsearSlug(slug, tipoPorDefecto);

        tipo.Should().Be(tipoEsperado);
        numero.Should().Be(numeroEsperado);
    }
}
