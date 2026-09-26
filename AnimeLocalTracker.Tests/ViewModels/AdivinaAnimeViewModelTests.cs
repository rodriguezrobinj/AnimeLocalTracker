using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>"Adivina el anime": flujo de la partida (inicio → rondas → resumen), puntos y pistas. La lógica pura
/// (distractores, censura, puntuación) se prueba aparte en AdivinaAnimeJuegoTests.</summary>
public class AdivinaAnimeViewModelTests
{
    private static readonly string[] Titulos =
        ["Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama", "Berserk", "Trigun", "Hunter x Hunter", "Mushishi", "Cowboy Bebop", "Steins Gate"];

    private static readonly string[] TitulosConFranquicia = ["Naruto", "Naruto Shippuden", "Bleach", "Death Note"];

    private readonly Mock<IDatabaseService> _db = new();

    private static List<AnimeItem> Biblioteca(int cantidad) =>
        Titulos.Take(cantidad).Select((t, i) => new AnimeItem
        {
            AniListId = i + 1,
            Titulo = t,
            Generos = "Action, Drama",
            AnioLanzamiento = 2000 + i,
            TotalEpisodios = 12 + i,
            Temporada = "FALL",
            Sinopsis = "Una historia larga sobre alguien que busca su lugar en el mundo."
        }).ToList();

    private async Task<AdivinaAnimeViewModel> CrearSutAsync(int animes)
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(Biblioteca(animes));
        var sut = new AdivinaAnimeViewModel(_db.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        return sut;
    }

    private static void ResponderBien(AdivinaAnimeViewModel sut) => sut.ResponderCommand.Execute(sut.Opciones[sut.IndiceCorrecto]);

    private static void ResponderMal(AdivinaAnimeViewModel sut) =>
        sut.ResponderCommand.Execute(sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto));

    // === Preparación ===

    [Fact]
    public async Task PrepararAsync_ConSuficientesAnimes_DeberiaPermitirJugar()
    {
        var sut = await CrearSutAsync(6);

        sut.AnimesDisponibles.Should().Be(6);
        sut.PuedeJugar.Should().BeTrue();
        sut.MostrarPresentacion.Should().BeTrue();
        sut.MostrarSinAnimes.Should().BeFalse();
        sut.EstaCargando.Should().BeFalse();
    }

    [Fact]
    public async Task PrepararAsync_ConPocosAnimes_DeberiaMostrarElEstadoVacioYNoIniciar()
    {
        var sut = await CrearSutAsync(3);

        sut.PuedeJugar.Should().BeFalse();
        sut.MostrarSinAnimes.Should().BeTrue();
        sut.MostrarPresentacion.Should().BeFalse();

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue("sin animes suficientes no se puede empezar");
    }

    [Fact]
    public async Task PrepararAsync_SiLaBaseFalla_NoDeberiaLanzarYDejarElJuegoNoDisponible()
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ThrowsAsync(new InvalidOperationException("db caída"));
        var sut = new AdivinaAnimeViewModel(_db.Object);

        await sut.PrepararAsync();

        sut.PuedeJugar.Should().BeFalse();
        sut.EstaCargando.Should().BeFalse();
    }

    // === Inicio de partida y ronda ===

    [Fact]
    public async Task IniciarPartida_DeberiaMostrarLaPrimeraRondaConUnaPistaYCuatroOpciones()
    {
        var sut = await CrearSutAsync(12);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsJugando.Should().BeTrue();
        sut.RondaNumero.Should().Be(1);
        sut.TotalRondas.Should().Be(AdivinaAnimeJuego.RondasPorPartida);
        sut.Opciones.Should().HaveCount(4);
        sut.Pistas.Should().ContainSingle("la primera pista es gratis y las demás se piden");
        sut.Puntos.Should().Be(0);
        sut.HaRespondido.Should().BeFalse();
        sut.RadioDesenfoque.Should().BeGreaterThan(0);
        sut.PuedePedirPista.Should().BeTrue();
    }

    [Fact]
    public async Task IniciarPartida_ConMenosAnimesQueRondas_DeberiaAjustarElTotal()
    {
        var sut = await CrearSutAsync(5);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.TotalRondas.Should().Be(5);
    }

    // === Pistas ===

    [Fact]
    public async Task PedirPista_DeberiaMostrarOtraPistaAclararLaPortadaYBajarLosPuntosPosibles()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        double desenfoqueInicial = sut.RadioDesenfoque;
        string puntosPosiblesInicial = sut.PuntosPosiblesTexto;

        sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(2);
        sut.RadioDesenfoque.Should().BeLessThan(desenfoqueInicial);
        sut.PuntosPosiblesTexto.Should().NotBe(puntosPosiblesInicial);
        sut.PuntosPosiblesTexto.Should().Contain("80");
    }

    [Fact]
    public async Task PedirPista_NoDeberiaPasarDelTotalDePistasDisponibles()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        for (int i = 0; i < 20; i++) sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(4, "género, estreno, episodios y sinopsis (no hay datos de AniList en esta prueba)");
        sut.PuedePedirPista.Should().BeFalse();
    }

    // === Respuestas ===

    [Fact]
    public async Task Responder_ConAciertoYUnaPista_DeberiaDar100PuntosYMarcarLaCorrecta()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        ResponderBien(sut);

        sut.Puntos.Should().Be(100);
        sut.Aciertos.Should().Be(1);
        sut.UltimoFueAcierto.Should().BeTrue();
        sut.HaRespondido.Should().BeTrue();
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue();
        sut.Opciones.Count(o => o.EsIncorrecta).Should().Be(0);
        sut.RadioDesenfoque.Should().Be(0, "al responder la portada queda nítida");
        sut.PuedePedirPista.Should().BeFalse();
        sut.ResultadoTexto.Should().Contain("100");
    }

    [Fact]
    public async Task Responder_ConAciertoTrasPedirDosPistas_DeberiaDar60Puntos()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);

        ResponderBien(sut);

        sut.Puntos.Should().Be(60);
    }

    [Fact]
    public async Task Responder_ConFallo_DeberiaDar0PuntosMarcarLaElegidaComoIncorrectaYRevelarLaCorrecta()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        var elegida = sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto);

        sut.ResponderCommand.Execute(elegida);

        sut.Puntos.Should().Be(0);
        sut.Aciertos.Should().Be(0);
        sut.UltimoFueAcierto.Should().BeFalse();
        elegida.EsIncorrecta.Should().BeTrue();
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue("aunque falles, ves cuál era");
        sut.Opciones.Count(o => o.EsIncorrecta).Should().Be(1);
    }

    [Fact]
    public async Task Responder_UnaSegundaVezEnLaMismaRonda_DeberiaIgnorarse()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        ResponderBien(sut);
        ResponderBien(sut);
        ResponderMal(sut);

        sut.Puntos.Should().Be(100, "no se puede puntuar dos veces ni cambiar la respuesta");
        sut.Aciertos.Should().Be(1);
    }

    [Fact]
    public async Task PedirPista_DespuesDeResponder_NoDeberiaHacerNada()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        ResponderBien(sut);
        int pistas = sut.Pistas.Count;

        sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(pistas);
    }

    [Fact]
    public async Task ResponderNumero_DeberiaResponderConLaOpcionDeEseNumero()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        int numeroCorrecto = sut.IndiceCorrecto + 1;

        sut.ResponderNumeroCommand.Execute(numeroCorrecto.ToString());

        sut.UltimoFueAcierto.Should().BeTrue();
        sut.Puntos.Should().Be(100);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("x")]
    [InlineData(null)]
    public async Task ResponderNumero_ConNumeroInvalido_NoDeberiaResponder(string? numero)
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.ResponderNumeroCommand.Execute(numero);

        sut.HaRespondido.Should().BeFalse();
    }

    // === Avance y fin de partida ===

    [Fact]
    public async Task Siguiente_SinResponderAntes_NoDeberiaAvanzar()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        await sut.SiguienteCommand.ExecuteAsync(null);

        sut.RondaNumero.Should().Be(1, "no se puede saltar una ronda sin responder");
    }

    [Fact]
    public async Task Siguiente_DespuesDeResponder_DeberiaPasarALaSiguienteRondaConEstadoLimpio()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        ResponderBien(sut);

        await sut.SiguienteCommand.ExecuteAsync(null);

        sut.RondaNumero.Should().Be(2);
        sut.HaRespondido.Should().BeFalse();
        sut.Pistas.Should().ContainSingle("cada ronda empieza con una sola pista");
        sut.Opciones.Should().OnlyContain(o => !o.EsCorrecta && !o.EsIncorrecta);
        sut.ResultadoTexto.Should().BeEmpty();
        sut.Puntos.Should().Be(80, "los puntos de la partida se conservan");
    }

    [Fact]
    public async Task PartidaCompleta_ConTodosLosAciertosSinPistas_DeberiaTerminarEnElResumenConPuntuacionMaxima()
    {
        var sut = await CrearSutAsync(5);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        for (int ronda = 1; ronda <= 5; ronda++)
        {
            sut.EsJugando.Should().BeTrue();
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.EsResumen.Should().BeTrue();
        sut.Puntos.Should().Be(500);
        sut.Aciertos.Should().Be(5);
        sut.RondaNumero.Should().Be(5);
        sut.ResumenAciertosTexto.Should().Contain("5");
        sut.ResumenPuntosTexto.Should().Contain("500");
    }

    [Fact]
    public async Task Resumen_ConUnSoloAcierto_NoDeberiaDecirAciertosEnPlural()
    {
        var sut = await CrearSutAsync(5);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        ResponderBien(sut);
        await sut.SiguienteCommand.ExecuteAsync(null);
        for (int i = 0; i < 4; i++)
        {
            ResponderMal(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.EsResumen.Should().BeTrue();
        sut.Aciertos.Should().Be(1);
        sut.ResumenAciertosTexto.Should().NotContain("aciertos", "con 1 acierto la frase va en singular (\"1 acierto de 5 rondas\")");
    }

    [Fact]
    public async Task IniciarPartida_DespuesDeTerminar_DeberiaReiniciarPuntosYContadores()
    {
        var sut = await CrearSutAsync(5);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        for (int i = 0; i < 5; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }
        sut.EsResumen.Should().BeTrue();

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsJugando.Should().BeTrue();
        sut.Puntos.Should().Be(0);
        sut.Aciertos.Should().Be(0);
        sut.RondaNumero.Should().Be(1);
    }

    [Fact]
    public async Task VolverAlInicio_DesdeElResumen_DeberiaMostrarLaPresentacion()
    {
        var sut = await CrearSutAsync(5);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        for (int i = 0; i < 5; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        await sut.VolverAlInicioCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        sut.MostrarPresentacion.Should().BeTrue();
    }

    [Fact]
    public async Task PrepararAsync_ConUnaPartidaEnCurso_NoDeberiaInterrumpirla()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        ResponderBien(sut);

        await sut.PrepararAsync(); // p. ej. cambias de pestaña y vuelves a Minijuegos

        sut.EsJugando.Should().BeTrue();
        sut.Puntos.Should().Be(100);
        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Once);
    }

    [Fact]
    public async Task Partida_ConFranquiciasRepetidas_DeberiaSaltarLasRondasImposiblesYAjustarElTotal()
    {
        // "A" y "A 2nd Season" son de la misma serie: cuando la respuesta es una de ellas solo quedan 2
        // distractores posibles, así que esas rondas se descartan en vez de mostrar una pregunta ambigua.
        var animes = TitulosConFranquicia
            .Select((t, i) => new AnimeItem
            {
                AniListId = i + 1, Titulo = t, Generos = "Action", AnioLanzamiento = 2005, TotalEpisodios = 20,
                Sinopsis = "Una historia larga sobre alguien que busca su lugar en el mundo."
            }).ToList();
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);
        var sut = new AdivinaAnimeViewModel(_db.Object) { Rng = new Random(5) };
        await sut.PrepararAsync();
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        int rondasJugadas = 0;
        while (sut.EsJugando)
        {
            rondasJugadas++;
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        rondasJugadas.Should().Be(2, "solo Bleach y Death Note tienen 3 distractores válidos");
        sut.TotalRondas.Should().Be(2);
        sut.Aciertos.Should().Be(2);
    }

    [Fact]
    public async Task Ronda_ConDatosExtraDeAniList_DeberiaAnadirLaPistaDeProduccion()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(It.IsAny<int>()))
            .ReturnsAsync(new DatosExtraAnime { Estudio = "MAPPA", Formato = "TV", Fuente = "MANGA" });
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        for (int i = 0; i < 10; i++) sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(5);
        sut.Pistas.Should().Contain(p => p.Texto.Contains("MAPPA"));
    }

    [Fact]
    public async Task Ronda_SiLosDatosExtraFallan_DeberiaSeguirConLasDemasPistas()
    {
        _db.Setup(d => d.ObtenerDatosExtraAsync(It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("sin datos"));
        var sut = await CrearSutAsync(6);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsJugando.Should().BeTrue();
        sut.Pistas.Should().NotBeEmpty();
    }

    // === Pistas: textos localizados ===

    [Fact]
    public void TextoEstreno_ConTemporada_DeberiaIncluirAnioYTemporada_YSinTemporadaSoloElAnio()
    {
        AdivinaAnimeViewModel.TextoEstreno(new PistaAnime(TipoPista.Estreno, "2013", "FALL")).Should().Contain("2013").And.NotBe("2013");
        AdivinaAnimeViewModel.TextoEstreno(new PistaAnime(TipoPista.Estreno, "2013")).Should().Be("2013");
    }

    [Fact]
    public void TextoEpisodios_DeberiaIncluirLaCantidad()
    {
        AdivinaAnimeViewModel.TextoEpisodios("24").Should().Contain("24");
    }

    [Fact]
    public void TextoProduccion_DeberiaUnirEstudioFormatoYFuente_OmitiendoLoQueFalta()
    {
        string completo = AdivinaAnimeViewModel.TextoProduccion(new PistaAnime(TipoPista.Produccion, "MAPPA", "TV", "MANGA"));
        completo.Should().Contain("MAPPA").And.Contain("·");

        string soloEstudio = AdivinaAnimeViewModel.TextoProduccion(new PistaAnime(TipoPista.Produccion, "MAPPA"));
        soloEstudio.Should().Contain("MAPPA").And.NotContain("·");
    }

    // === Idioma ===

    [Fact]
    public async Task CambioDeIdioma_EnMitadDeRonda_NoDeberiaPerderElEstadoDeLaPartida()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        ResponderBien(sut);

        sut.Receive(new IdiomaCambiadoMensaje());

        sut.Pistas.Should().HaveCount(2);
        sut.Puntos.Should().Be(80);
        sut.ResultadoTexto.Should().NotBeEmpty();
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue();
    }
}
