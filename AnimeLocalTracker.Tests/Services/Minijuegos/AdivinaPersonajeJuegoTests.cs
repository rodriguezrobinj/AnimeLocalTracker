using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Minijuegos;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Minijuegos;

/// <summary>Lógica pura de "Adivina el personaje": pistas, distractores, elección de respuestas y pixelado de la imagen.</summary>
public class AdivinaPersonajeJuegoTests
{
    private static PersonajeAnime P(int id, string nombre, string genero = "Male", string rol = "MAIN", string edad = "17",
        string alternativos = "", int animeId = 1, int favoritos = 100, string? imagen = null) => new()
    {
        AnimeId = animeId,
        PersonajeId = id,
        Nombre = nombre,
        Genero = genero,
        Rol = rol,
        Edad = edad,
        Alternativos = alternativos,
        Favoritos = favoritos,
        ImagenUrl = imagen ?? $"https://s4.anilist.co/file/anilistcdn/character/large/b{id}.png"
    };

    private static AnimeItem A(int id, string titulo) => new() { AniListId = id, Titulo = titulo };

    private static Dictionary<int, List<PersonajeAnime>> Pool(params (int AnimeId, PersonajeAnime[] Personajes)[] grupos) =>
        grupos.ToDictionary(g => g.AnimeId, g => g.Personajes.ToList());

    // === Puntos y pixelado ===

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 80)]
    [InlineData(4, 40)]
    [InlineData(6, 20)]
    [InlineData(9, 20)]
    public void Puntos_DeberiaRestarPorCadaPistaExtraConUnMinimo(int pistas, int esperado) =>
        AdivinaPersonajeJuego.Puntos(pistas).Should().Be(esperado);

    [Fact]
    public void LadoPixelado_EmpiezaMuyBloqueado_YGanaDetalleConCadaPista()
    {
        var lados = Enumerable.Range(1, 6).Select(n => AdivinaPersonajeJuego.LadoPixelado(n, 6)).ToList();

        lados.First().Should().Be(10, "con una sola pista la imagen es casi irreconocible");
        lados.Last().Should().Be(80);
        lados.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(99, 6)]
    public void LadoPixelado_FueraDeRango_SeAjustaAlLimite(int pistas, int total) =>
        AdivinaPersonajeJuego.LadoPixelado(pistas, total).Should().BeInRange(10, 80);

    [Fact]
    public void LadoPixelado_ConUnaSolaPistaEnTotal_NoDivideEntreCero() =>
        AdivinaPersonajeJuego.LadoPixelado(1, 1).Should().Be(80);

    // === Utilizables ===

    [Fact]
    public void EsUtilizable_DescartaSinNombreSinImagenYLaImagenPorDefectoDeAniList()
    {
        AdivinaPersonajeJuego.EsUtilizable(P(1, "Light Yagami")).Should().BeTrue();
        AdivinaPersonajeJuego.EsUtilizable(P(2, " ")).Should().BeFalse();
        AdivinaPersonajeJuego.EsUtilizable(P(3, "Ryuk", imagen: "")).Should().BeFalse();
        AdivinaPersonajeJuego.EsUtilizable(P(4, "Misa", imagen: "https://s4.anilist.co/file/anilistcdn/character/large/default.jpg")).Should().BeFalse();
        AdivinaPersonajeJuego.EsUtilizable(P(0, "Sin id")).Should().BeFalse();
    }

    // === Pistas ===

    [Theory]
    [InlineData("17-", 17)]
    [InlineData("13-15 (Pre-timeskip); 19-20 (Post-timeskip)", 13)]
    [InlineData("24", 24)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Unknown", null)]
    [InlineData("0", null)]
    public void ExtraerEdad_TomaElPrimerNumeroDelTextoLibre(string? texto, int? esperado) =>
        AdivinaPersonajeJuego.ExtraerEdad(texto).Should().Be(esperado);

    [Fact]
    public void ElegirApodo_SaltaLosQueDelatanElNombreYLosNoLatinos()
    {
        var levi = P(1, "Levi", alternativos: "Levi Heichou (リヴァイ兵長) | Captain Levi | 兵士長 | Humanity's Strongest Soldier | Ab");

        AdivinaPersonajeJuego.ElegirApodo(levi).Should().Be("Humanity's Strongest Soldier");
    }

    [Fact]
    public void ElegirApodo_SinApodosValidos_DevuelveNull()
    {
        AdivinaPersonajeJuego.ElegirApodo(P(1, "Levi", alternativos: "")).Should().BeNull();
        AdivinaPersonajeJuego.ElegirApodo(P(2, "Luffy Monkey", alternativos: "Monkey D. Luffy | Mugiwara (麦わら)")).Should().BeNull();
    }

    [Fact]
    public void ConstruirPistas_ConTodosLosDatos_VaDeMenosAMasReveladora()
    {
        var p = P(1, "Luffy Monkey", alternativos: "Straw Hat");

        var pistas = AdivinaPersonajeJuego.ConstruirPistas(p, "One Piece");

        pistas.Select(x => x.Tipo).Should().Equal(
            TipoPistaPersonaje.Rol, TipoPistaPersonaje.Genero, TipoPistaPersonaje.Edad,
            TipoPistaPersonaje.Apodo, TipoPistaPersonaje.Anime, TipoPistaPersonaje.Inicial);
        pistas.Single(x => x.Tipo == TipoPistaPersonaje.Edad).Valor.Should().Be("17");
        pistas.Single(x => x.Tipo == TipoPistaPersonaje.Anime).Valor.Should().Be("One Piece");
        pistas.Single(x => x.Tipo == TipoPistaPersonaje.Inicial).Valor.Should().Be("L");
    }

    [Fact]
    public void ConstruirPistas_OmiteLoQueElPersonajeNoTiene()
    {
        var p = P(1, "Hange Zoe", genero: "Non-binary", edad: "", rol: "SUPPORTING");

        var pistas = AdivinaPersonajeJuego.ConstruirPistas(p, "Shingeki no Kyojin");

        pistas.Select(x => x.Tipo).Should().Equal(TipoPistaPersonaje.Rol, TipoPistaPersonaje.Anime, TipoPistaPersonaje.Inicial);
    }

    [Fact]
    public void ConstruirPistas_NingunaPista_NoDebeIncluirElNombreCompleto()
    {
        var p = P(1, "Light Yagami", alternativos: "Kira | Light Asahi");

        var pistas = AdivinaPersonajeJuego.ConstruirPistas(p, "Death Note");

        pistas.Select(x => x.Valor).Should().NotContain("Light Yagami");
        pistas.Where(x => x.Tipo == TipoPistaPersonaje.Apodo).Select(x => x.Valor).Should().Equal(["Kira"], "el otro apodo contiene «Light»");
    }

    // === Distractores ===

    [Fact]
    public void ElegirDistractores_NuncaIncluyeAlMismoPersonajeNiOtroConSuNombre()
    {
        var respuesta = P(1, "Naruto Uzumaki");
        var pool = new[]
        {
            respuesta,
            P(1, "Naruto Uzumaki", animeId: 2),          // mismo id en otro anime
            P(99, "naruto  uzumaki", animeId: 3),        // mismo nombre, otro id
            P(2, "Sasuke Uchiha"), P(3, "Sakura Haruno", genero: "Female"), P(4, "Kakashi Hatake"), P(5, "Itachi Uchiha")
        };

        var d = AdivinaPersonajeJuego.ElegirDistractores(respuesta, pool, 3, new Random(1));

        d.Should().HaveCount(3);
        d.Select(x => x.PersonajeId).Should().NotContain([1, 99]).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void ElegirDistractores_PrefiereElMismoGeneroParaQueNoSeaObvio()
    {
        var respuesta = P(1, "Mikasa Ackerman", genero: "Female");
        var pool = new List<PersonajeAnime> { respuesta };
        for (int i = 2; i <= 12; i++) pool.Add(P(i, $"Chico {i}", genero: "Male"));
        pool.Add(P(20, "Chica A", genero: "Female"));
        pool.Add(P(21, "Chica B", genero: "Female"));
        pool.Add(P(22, "Chica C", genero: "Female"));

        for (int semilla = 0; semilla < 10; semilla++)
        {
            var d = AdivinaPersonajeJuego.ElegirDistractores(respuesta, pool, 3, new Random(semilla));
            d.Count(x => x.Genero == "Female").Should().BeGreaterThanOrEqualTo(2, $"semilla {semilla}");
        }
    }

    [Fact]
    public void ElegirDistractores_ConPocosCandidatos_DevuelveLosQueHay()
    {
        var respuesta = P(1, "A A");

        var d = AdivinaPersonajeJuego.ElegirDistractores(respuesta, [respuesta, P(2, "B B")], 3, new Random(1));

        d.Should().ContainSingle();
    }

    // === Respuestas ===

    [Fact]
    public void ElegirRespuestas_UnPersonajePorAnime_YSaltaLosAnimesSinPersonajes()
    {
        var animes = new List<AnimeItem> { A(1, "Uno"), A(2, "Dos"), A(3, "Tres") };
        var pool = Pool((1, [P(10, "Aaa Aaa"), P(11, "Bbb Bbb")]), (3, [P(30, "Ccc Ccc")]));

        var r = AdivinaPersonajeJuego.ElegirRespuestas(animes, pool, 10, new Random(1));

        r.Select(x => x.Anime.AniListId).Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public void ElegirRespuestas_RespetaElNumeroDeRondas()
    {
        var animes = Enumerable.Range(1, 8).Select(i => A(i, $"Anime {i}")).ToList();
        var pool = animes.ToDictionary(a => a.AniListId, a => new List<PersonajeAnime> { P(a.AniListId * 10, $"Pers {a.AniListId} X", animeId: a.AniListId) });

        AdivinaPersonajeJuego.ElegirRespuestas(animes, pool, 5, new Random(1)).Should().HaveCount(5);
    }

    [Fact]
    public void ElegirRespuestas_NoRepiteElMismoPersonajeAunqueSalgaEnDosAnimes()
    {
        var animes = new List<AnimeItem> { A(1, "Naruto"), A(2, "Naruto Shippuden") };
        var naruto = P(17, "Naruto Uzumaki");
        var pool = Pool((1, [naruto]), (2, [P(17, "Naruto Uzumaki", animeId: 2)]));

        AdivinaPersonajeJuego.ElegirRespuestas(animes, pool, 10, new Random(1)).Should().ContainSingle();
    }

    [Fact]
    public void ElegirRespuestas_SoloEligeEntreLosMasConocidosDelAnime()
    {
        var animes = new List<AnimeItem> { A(1, "Uno") };
        var personajes = new List<PersonajeAnime>();
        for (int i = 1; i <= 8; i++) personajes.Add(P(i, $"Principal {i} X", rol: i <= 2 ? "MAIN" : "SUPPORTING", favoritos: 1000 - i));
        for (int i = 9; i <= 12; i++) personajes.Add(P(i, $"Menor {i} X", rol: "BACKGROUND", favoritos: 1));
        var pool = new Dictionary<int, List<PersonajeAnime>> { [1] = personajes };

        for (int semilla = 0; semilla < 40; semilla++)
        {
            var r = AdivinaPersonajeJuego.ElegirRespuestas(animes, pool, 1, new Random(semilla));
            r.Single().Personaje.PersonajeId.Should().BeLessThanOrEqualTo(8, $"semilla {semilla}: los de fondo no son justos");
        }
    }

    // === Rondas ===

    private static (AnimeItem Anime, PersonajeAnime Respuesta, Dictionary<int, List<PersonajeAnime>> Pool) Escenario()
    {
        var respuesta = P(1, "Light Yagami", alternativos: "Kira");
        var pool = Pool(
            (1, [respuesta, P(2, "Ryuk")]),
            (2, [P(3, "Edward Elric"), P(4, "Winry Rockbell", genero: "Female")]),
            (3, [P(5, "Levi"), P(6, "Mikasa Ackerman", genero: "Female")]));
        return (A(1, "Death Note"), respuesta, pool);
    }

    [Fact]
    public void CrearRonda_TieneCuatroOpcionesDistintasYLaCorrectaEnSuIndice()
    {
        var (anime, respuesta, pool) = Escenario();

        var ronda = AdivinaPersonajeJuego.CrearRonda(anime, respuesta, pool, 4, new Random(3));

        ronda.Should().NotBeNull();
        ronda!.Opciones.Should().HaveCount(4);
        ronda.Opciones.Select(o => o.PersonajeId).Should().OnlyHaveUniqueItems();
        ronda.Opciones[ronda.IndiceCorrecto].PersonajeId.Should().Be(1);
    }

    [Fact]
    public void CrearRonda_SinSuficientesDistractores_DevuelveNull()
    {
        var respuesta = P(1, "Light Yagami");
        var pool = Pool((1, [respuesta, P(2, "Ryuk")]));

        AdivinaPersonajeJuego.CrearRonda(A(1, "Death Note"), respuesta, pool, 4, new Random(3)).Should().BeNull();
    }

    [Fact]
    public void CrearRonda_ConMenosDeDosPistas_DevuelveNull()
    {
        var (_, _, pool) = Escenario();
        var pobre = P(1, "", rol: "", genero: "", edad: "");
        pobre.Nombre = "?"; // sin inicial ni rol ni género: solo queda la pista del anime

        AdivinaPersonajeJuego.CrearRonda(A(1, "Death Note"), pobre, pool, 4, new Random(3)).Should().BeNull();
    }
}
