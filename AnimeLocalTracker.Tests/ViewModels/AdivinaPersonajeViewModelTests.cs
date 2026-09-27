using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>"Adivina el personaje": preparación de la partida (personajes + imágenes), rondas, pistas, puntos y errores. La
/// lógica pura se prueba aparte en AdivinaPersonajeJuegoTests.</summary>
public class AdivinaPersonajeViewModelTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IPersonajesService> _personajes = new();
    private readonly Mock<IMinijuegosRecordsService> _records = new();
    private readonly List<PartidaMinijuego> _guardadas = new();

    private Func<PersonajeAnime, string?> _imagen = _ => "C:\\fake\\personaje.png";

    public AdivinaPersonajeViewModelTests()
    {
        _records.Setup(r => r.ObtenerAsync(It.IsAny<string>())).ReturnsAsync(RecordsMinijuego.Vacio);
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>()))
            .ReturnsAsync((PartidaMinijuego p) =>
            {
                _guardadas.Add(p);
                return new ResultadoPartida(false, 0, RecordsMinijuego.Calcular([p]));
            });

        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => ids.ToDictionary(id => id, PersonajesDe));
        _personajes.Setup(p => p.ObtenerImagenAsync(It.IsAny<PersonajeAnime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonajeAnime p, CancellationToken _) => _imagen(p));
    }

    private static List<PersonajeAnime> PersonajesDe(int animeId) =>
        Enumerable.Range(1, 12).Select(k => new PersonajeAnime
        {
            AnimeId = animeId,
            PersonajeId = animeId * 100 + k,
            Nombre = $"Personaje{animeId}x{k} Apellido",
            ImagenUrl = $"https://s4.anilist.co/file/anilistcdn/character/large/b{animeId * 100 + k}.png",
            Genero = k % 2 == 0 ? "Female" : "Male",
            Rol = k <= 3 ? "MAIN" : "SUPPORTING",
            Edad = (14 + k).ToString(),
            Favoritos = 1000 - k
        }).ToList();

    private async Task<AdivinaPersonajeViewModel> CrearSutAsync(int animes = 12)
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(
            Enumerable.Range(1, animes).Select(i => new AnimeItem { AniListId = i, Titulo = $"Anime número {i}" }).ToList());
        var sut = new AdivinaPersonajeViewModel(_db.Object, _personajes.Object, _records.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        return sut;
    }

    private async Task<AdivinaPersonajeViewModel> IniciarAsync(int animes = 12)
    {
        var sut = await CrearSutAsync(animes);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        return sut;
    }

    private static void ResponderBien(AdivinaPersonajeViewModel sut) => sut.ResponderCommand.Execute(sut.Opciones[sut.IndiceCorrecto]);

    private static void ResponderMal(AdivinaPersonajeViewModel sut) =>
        sut.ResponderCommand.Execute(sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto));

    // === Preparación ===

    [Fact]
    public async Task Prepararse_ConPocosAnimes_NoDejaJugar()
    {
        var sut = await CrearSutAsync(3);

        sut.PuedeJugar.Should().BeFalse();
        sut.MostrarSinAnimes.Should().BeTrue();

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        _personajes.Verify(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Iniciar_ArmaLaPrimeraRondaConImagenMuyPixeladaYUnaPista()
    {
        var sut = await IniciarAsync();

        sut.EsJugando.Should().BeTrue();
        sut.TotalRondas.Should().Be(10);
        sut.RondaNumero.Should().Be(1);
        sut.Opciones.Should().HaveCount(4);
        sut.Pistas.Should().ContainSingle();
        sut.LadoPixelado.Should().Be(10);
        sut.EstaPixelada.Should().BeTrue();
        sut.ImagenRonda.Should().Be("C:\\fake\\personaje.png");
        sut.EstaPreparando.Should().BeFalse();
        sut.EstaCargando.Should().BeFalse();
        sut.HayError.Should().BeFalse();
    }

    [Fact]
    public async Task Iniciar_ConsultaSoloAlgunosAnimesMasLosDeReserva()
    {
        IReadOnlyCollection<int>? pedidos = null;
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<int> ids, CancellationToken _) => pedidos = ids)
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => ids.ToDictionary(id => id, PersonajesDe));

        await IniciarAsync(animes: 30);

        pedidos.Should().HaveCount(AdivinaAnimeJuego.RondasPorPartida + AdivinaPersonajeViewModel.AnimesExtra, "no se piden a AniList los 30 animes");
    }

    [Fact]
    public async Task Iniciar_LasOpcionesSonPersonajesDistintosYLaCorrectaEsDeLaRonda()
    {
        var sut = await IniciarAsync();

        sut.Opciones.Select(o => o.Titulo).Should().OnlyHaveUniqueItems();
        sut.Opciones.Select(o => o.Numero).Should().Equal(1, 2, 3, 4);
        sut.IndiceCorrecto.Should().BeInRange(0, 3);
    }

    // === Sin datos ===

    [Fact]
    public async Task Iniciar_SinPersonajesNiConexion_MuestraElErrorYVuelveAlInicio()
    {
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, List<PersonajeAnime>>());

        var sut = await IniciarAsync();

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
        sut.MensajeError.Should().NotBeNullOrWhiteSpace();
        sut.EstaCargando.Should().BeFalse("el botón Jugar debe volver a verse");
        sut.EstaPreparando.Should().BeFalse();
        sut.MostrarPresentacion.Should().BeTrue();
    }

    [Fact]
    public async Task Iniciar_ElErrorSeBorraAlReintentarConExito()
    {
        var fallar = true;
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => fallar ? new Dictionary<int, List<PersonajeAnime>>() : ids.ToDictionary(id => id, PersonajesDe));
        var sut = await IniciarAsync();
        sut.HayError.Should().BeTrue();

        fallar = false;
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.HayError.Should().BeFalse();
        sut.EsJugando.Should().BeTrue();
    }

    [Fact]
    public async Task Iniciar_SiFallaElServicio_NoRompeLaAplicacion()
    {
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = await IniciarAsync();

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
    }

    [Fact]
    public async Task Iniciar_SiAlgunasImagenesFallan_SeSustituyenPorLasDeReserva()
    {
        int fallos = 0;
        _imagen = _ => Interlocked.Increment(ref fallos) <= AdivinaPersonajeViewModel.RondasExtra ? null : "C:\\fake\\ok.png";

        var sut = await IniciarAsync();

        sut.TotalRondas.Should().Be(10, "las 2 rondas de reserva cubren las 2 imágenes que no bajaron");
    }

    [Fact]
    public async Task Iniciar_SiFaltanImagenesDeSobra_JuegaConLasRondasQueTiene()
    {
        int n = 0;
        _imagen = _ => Interlocked.Increment(ref n) <= 5 ? null : "C:\\fake\\ok.png";

        var sut = await IniciarAsync();

        sut.EsJugando.Should().BeTrue();
        sut.TotalRondas.Should().Be(AdivinaAnimeJuego.RondasPorPartida + AdivinaPersonajeViewModel.RondasExtra - 5);
    }

    [Fact]
    public async Task Iniciar_SinNingunaImagen_MuestraElError()
    {
        _imagen = _ => null;

        var sut = await IniciarAsync();

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
    }

    [Fact]
    public async Task Iniciar_SiAniListTardaDemasiado_SeCortaYAvisa()
    {
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyCollection<int> _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new Dictionary<int, List<PersonajeAnime>>();
            });
        var sut = await CrearSutAsync();
        sut.TiempoMaximoPreparacion = TimeSpan.FromMilliseconds(100);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
        sut.EstaCargando.Should().BeFalse();
    }

    [Fact]
    public async Task Detener_MientrasSePrepara_CancelaYNoEmpiezaLaPartida()
    {
        var entro = new TaskCompletionSource();
        _personajes.Setup(p => p.ObtenerAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyCollection<int> _, CancellationToken ct) =>
            {
                entro.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new Dictionary<int, List<PersonajeAnime>>();
            });
        var sut = await CrearSutAsync();

        var iniciar = sut.IniciarPartidaCommand.ExecuteAsync(null);
        await entro.Task;
        sut.Detener();
        await iniciar;

        sut.EsJugando.Should().BeFalse();
        sut.HayError.Should().BeFalse("el jugador salió a propósito: no es un error");
        sut.EstaCargando.Should().BeFalse();
    }

    // === Pistas y puntos ===

    [Fact]
    public async Task PedirPista_AgregaUnaPista_AclaraLaImagenYBajaLosPuntosPosibles()
    {
        var sut = await IniciarAsync();
        int lado0 = sut.LadoPixelado;
        string puntos0 = sut.PuntosPosiblesTexto;

        sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(2);
        sut.LadoPixelado.Should().BeGreaterThan(lado0);
        sut.PuntosPosiblesTexto.Should().NotBe(puntos0);
    }

    [Fact]
    public async Task PedirPista_AlAgotarlas_SeDeshabilita()
    {
        var sut = await IniciarAsync();

        for (int i = 0; i < 10 && sut.PuedePedirPista; i++) sut.PedirPistaCommand.Execute(null);

        sut.PuedePedirPista.Should().BeFalse();
        int cantidad = sut.Pistas.Count;
        sut.PedirPistaCommand.Execute(null);
        sut.Pistas.Should().HaveCount(cantidad);
        sut.LadoPixelado.Should().Be(80);
    }

    [Fact]
    public async Task Acertar_ConLaPrimeraPista_DaCienPuntos_MuestraLaImagenEnteraYElAnime()
    {
        var sut = await IniciarAsync();

        ResponderBien(sut);

        sut.HaRespondido.Should().BeTrue();
        sut.UltimoFueAcierto.Should().BeTrue();
        sut.Puntos.Should().Be(100);
        sut.Aciertos.Should().Be(1);
        sut.LadoPixelado.Should().Be(0, "al responder la imagen sale entera");
        sut.EstaPixelada.Should().BeFalse();
        sut.AnimeReveladoTexto.Should().Contain("Anime número");
        sut.ResultadoTexto.Should().Contain("100");
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue();
    }

    [Fact]
    public async Task Acertar_ConPistasPedidas_DaMenosPuntos()
    {
        var sut = await IniciarAsync();
        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);

        ResponderBien(sut);

        sut.Puntos.Should().Be(60, "tres pistas vistas: 100 - 20 - 20");
    }

    [Fact]
    public async Task Fallar_NoDaPuntos_MarcaLaOpcionYMuestraElNombreCorrecto()
    {
        var sut = await IniciarAsync();
        string correcto = sut.Opciones[sut.IndiceCorrecto].Titulo;

        ResponderMal(sut);

        sut.UltimoFueAcierto.Should().BeFalse();
        sut.Puntos.Should().Be(0);
        sut.Aciertos.Should().Be(0);
        sut.Opciones.Count(o => o.EsIncorrecta).Should().Be(1);
        sut.ResultadoTexto.Should().Contain(correcto);
    }

    [Fact]
    public async Task Responder_DosVeces_SoloCuentaLaPrimera()
    {
        var sut = await IniciarAsync();

        ResponderBien(sut);
        ResponderBien(sut);

        sut.Puntos.Should().Be(100);
        sut.Aciertos.Should().Be(1);
    }

    [Fact]
    public async Task ResponderNumero_UsaLaOpcionConEseNumero()
    {
        var sut = await IniciarAsync();

        sut.ResponderNumeroCommand.Execute((sut.IndiceCorrecto + 1).ToString());

        sut.UltimoFueAcierto.Should().BeTrue();
    }

    [Fact]
    public async Task ResponderNumero_ConTextoInvalido_NoHaceNada()
    {
        var sut = await IniciarAsync();

        sut.ResponderNumeroCommand.Execute("x");
        sut.ResponderNumeroCommand.Execute("9");

        sut.HaRespondido.Should().BeFalse();
    }

    // === Partida completa ===

    [Fact]
    public async Task Siguiente_SoloFuncionaTrasResponder()
    {
        var sut = await IniciarAsync();

        await sut.SiguienteCommand.ExecuteAsync(null);

        sut.RondaNumero.Should().Be(1);
    }

    [Fact]
    public async Task PartidaCompleta_TerminaEnResumen_YGuardaLaPartidaConSuJuego()
    {
        var sut = await IniciarAsync();

        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 0) ResponderBien(sut); else ResponderMal(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.EsResumen.Should().BeTrue();
        sut.Aciertos.Should().Be(5);
        var partida = _guardadas.Should().ContainSingle().Subject;
        (partida.JuegoId, partida.Rondas, partida.Aciertos, partida.Puntos, partida.RachaMaxima).Should().Be(("adivina_personaje", 10, 5, 500, 1));
    }

    [Fact]
    public async Task UltimaRonda_OfreceVerResultadoEnLugarDeSiguiente()
    {
        var sut = await IniciarAsync();
        string siguiente = sut.TextoSiguiente;

        for (int i = 0; i < 9; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.TextoSiguiente.Should().NotBe(siguiente);
    }

    [Fact]
    public async Task NoSeRepiteElMismoPersonajeEnLaPartida()
    {
        var sut = await IniciarAsync();
        var nombres = new List<string>();

        for (int i = 0; i < 10; i++)
        {
            nombres.Add(sut.Opciones[sut.IndiceCorrecto].Titulo);
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        nombres.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task VolverAlInicio_TrasElResumen_PermiteJugarOtraPartida()
    {
        var sut = await IniciarAsync();
        for (int i = 0; i < 10; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        await sut.VolverAlInicioCommand.ExecuteAsync(null);
        sut.EsInicio.Should().BeTrue();
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsJugando.Should().BeTrue();
        sut.Puntos.Should().Be(0);
        sut.RondaNumero.Should().Be(1);
    }

    // === Textos ===

    [Fact]
    public void ConvertirPista_TraduceRolGeneroYEdad()
    {
        AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(TipoPistaPersonaje.Rol, "MAIN")).Texto.Should().NotBeNullOrWhiteSpace().And.NotBe("MAIN");
        AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(TipoPistaPersonaje.Genero, "Female")).Texto.Should().NotBe("Female");
        AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(TipoPistaPersonaje.Edad, "17")).Texto.Should().Contain("17");
        AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(TipoPistaPersonaje.Anime, "Death Note")).Texto.Should().Be("Death Note");
        AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(TipoPistaPersonaje.Inicial, "L")).Texto.Should().Be("L");
    }

    [Fact]
    public void ConvertirPista_TodasTienenIconoYEtiqueta()
    {
        foreach (var tipo in Enum.GetValues<TipoPistaPersonaje>())
        {
            var item = AdivinaPersonajeViewModel.ConvertirPista(new PistaPersonaje(tipo, "Male"));

            item.Icono.Should().NotBeNullOrWhiteSpace(tipo.ToString());
            item.Etiqueta.Should().NotBeNullOrWhiteSpace(tipo.ToString()).And.NotStartWith("Mini_", "falta la traducción");
        }
    }
}
