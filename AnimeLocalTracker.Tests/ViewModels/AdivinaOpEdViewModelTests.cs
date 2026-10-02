using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

/// <summary>"Adivina el OP/ED": flujo de la partida, ayudas, puntos, y sobre todo cómo se comporta cuando AnimeThemes
/// falla, tarda o no hay red. La API, las descargas y el audio se simulan; la lógica pura se prueba aparte.</summary>
public class AdivinaOpEdViewModelTests
{
    private static readonly string[] Titulos =
        ["Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama", "Berserk", "Trigun", "Hunter x Hunter", "Mushishi", "Cowboy Bebop", "Steins Gate"];

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IClipPlayer> _player = new();

    public AdivinaOpEdViewModelTests()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new List<AnimeThemeInfo> { TemaApi(id) });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>()))
            .Returns((int id, AnimeThemeInfo t) => $@"C:\Music\{id}\{t.Slug}.mp3");
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, AnimeThemeInfo t, IProgress<double>? _, CancellationToken _) => $@"C:\Music\{id}\{t.Slug}.mp3");
        _descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());
    }

    private static AnimeThemeInfo TemaApi(int id, string slug = "OP1") => new()
    {
        Slug = slug,
        Tipo = slug.StartsWith("ED", StringComparison.OrdinalIgnoreCase) ? "ED" : "OP",
        TituloCancion = $"Song {id}",
        Artistas = $"Artist {id}",
        AudioUrlOgg = $"https://a.animethemes.moe/{id}.ogg"
    };

    private static List<AnimeItem> Biblioteca(int cantidad) =>
        Titulos.Take(cantidad).Select((t, i) => new AnimeItem
        {
            AniListId = i + 1,
            Titulo = t,
            Generos = "Action, Drama",
            AnioLanzamiento = 2000 + i,
            Temporada = "FALL",
            UrlPortada = $"https://img/{i + 1}.jpg"
        }).ToList();

    private async Task<AdivinaOpEdViewModel> CrearSutAsync(int animes)
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(Biblioteca(animes));
        var sut = new AdivinaOpEdViewModel(_db.Object, _themes.Object, _descargas.Object, _player.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        return sut;
    }

    private static void ResponderBien(AdivinaOpEdViewModel sut) => sut.ResponderCommand.Execute(sut.Opciones[sut.IndiceCorrecto]);

    private static void ResponderMal(AdivinaOpEdViewModel sut) =>
        sut.ResponderCommand.Execute(sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto));

    private static async Task EsperarAsync(Func<bool> condicion, int milisegundos = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milisegundos);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(10);
        condicion().Should().BeTrue("la condición esperada no se cumplió a tiempo");
    }

    private int LlamadasATemas() => _themes.Invocations.Count(i => i.Method.Name == nameof(IAnimeThemesService.ObtenerTemasAsync));

    private static TemaLocalDisponible Local(string slug = "OP1") => new("OP", slug, 1, null, $@"C:\Music\local\{slug}.mp3");

    // === Inicio y primera ronda ===

    [Fact]
    public async Task PrepararAsync_ConSuficientesAnimes_DeberiaPermitirJugar()
    {
        var sut = await CrearSutAsync(6);

        sut.PuedeJugar.Should().BeTrue();
        sut.MostrarPresentacion.Should().BeTrue();
    }

    [Fact]
    public async Task IniciarPartida_ConPocosAnimes_NoDeberiaHacerNada()
    {
        var sut = await CrearSutAsync(3);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        _player.Verify(p => p.Reproducir(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task IniciarPartida_DeberiaMostrarLaPrimeraRondaYHacerSonarElClipInicial()
    {
        var sut = await CrearSutAsync(12);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsJugando.Should().BeTrue();
        sut.EstaPreparandoRonda.Should().BeFalse();
        sut.MostrarContenidoRonda.Should().BeTrue();
        sut.RondaNumero.Should().Be(1);
        sut.TotalRondas.Should().Be(AdivinaAnimeJuego.RondasPorPartida);
        sut.Opciones.Should().HaveCount(4);
        sut.Pistas.Should().BeEmpty("al empezar solo se oye el clip y se ve si es un opening o un ending");
        sut.SegundosClip.Should().Be(AdivinaOpEdJuego.SegundosIniciales);
        sut.TipoTemaTexto.Should().Contain("Opening");
        sut.EstaSonando.Should().BeTrue();
        sut.PortadaRespuesta.Should().BeEmpty("la portada delataría la respuesta");
        sut.TemaReveladoTexto.Should().BeEmpty();
        _player.Verify(p => p.Reproducir(It.Is<string>(r => r.EndsWith("OP1.mp3")), TimeSpan.FromSeconds(6), It.IsInRange(0.05, 0.40, Moq.Range.Inclusive)), Times.Once);
    }

    [Fact]
    public async Task IniciarPartida_DeberiaAdelantarLaSiguienteRondaMientrasSeJuegaLaPrimera()
    {
        var sut = await CrearSutAsync(12);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        await EsperarAsync(() => LlamadasATemas() >= 2);
    }

    // === Ayudas ===

    [Fact]
    public async Task PedirPista_LaPrimeraAlargaElClipYLoVuelveAReproducir()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.PedirPistaCommand.Execute(null);

        sut.SegundosClip.Should().Be(12);
        sut.Pistas.Should().BeEmpty("la ayuda de \"más audio\" no añade tarjeta");
        sut.PuntosPosiblesTexto.Should().Contain("80");
        _player.Verify(p => p.Reproducir(It.IsAny<string>(), TimeSpan.FromSeconds(12), It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task PedirPista_LaSegundaRevelaElArtista()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().ContainSingle().Which.Texto.Should().StartWith("Artist ");
        sut.PuntosPosiblesTexto.Should().Contain("60");
    }

    [Fact]
    public async Task PedirPista_NoDeberiaPasarDelTotalDeAyudas()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        for (int i = 0; i < 20; i++) sut.PedirPistaCommand.Execute(null);

        sut.Pistas.Should().HaveCount(4, "artista, estreno, géneros y canción; las 2 de audio no cuentan como tarjeta");
        sut.SegundosClip.Should().Be(20);
        sut.PuedePedirPista.Should().BeFalse();
    }

    [Fact]
    public async Task Escuchar_DeberiaVolverAReproducirConLaDuracionActual()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        _player.Invocations.Clear();

        sut.EscucharCommand.Execute(null);

        _player.Verify(p => p.Reproducir(It.IsAny<string>(), TimeSpan.FromSeconds(6), It.IsAny<double>()), Times.Once);
    }

    // === Responder ===

    [Fact]
    public async Task Responder_ConAciertoSinAyudas_DeberiaDar100PuntosYRevelarElTema()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        string titulo = sut.Opciones[sut.IndiceCorrecto].Titulo;
        int id = Array.IndexOf(Titulos, titulo) + 1;
        _player.Invocations.Clear();

        ResponderBien(sut);

        sut.Puntos.Should().Be(100);
        sut.Aciertos.Should().Be(1);
        sut.UltimoFueAcierto.Should().BeTrue();
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue();
        sut.PortadaRespuesta.Should().Be($"https://img/{id}.jpg");
        sut.TemaReveladoTexto.Should().Contain($"Song {id}").And.Contain($"Artist {id}");
        sut.ResultadoTexto.Should().Contain("100");
        sut.PuedePedirPista.Should().BeFalse();
        _player.Verify(p => p.Reproducir(It.IsAny<string>(), TimeSpan.FromSeconds(AdivinaOpEdJuego.SegundosRevelacion), It.IsAny<double>()), Times.Once,
            "al responder suena un trozo más largo del tema");
    }

    [Fact]
    public async Task Responder_ConAciertoTrasDosAyudas_DeberiaDar60Puntos()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);

        ResponderBien(sut);

        sut.Puntos.Should().Be(60);
    }

    [Fact]
    public async Task Responder_ConFallo_DeberiaDar0PuntosYRevelarLaCorrecta()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        var elegida = sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto);

        sut.ResponderCommand.Execute(elegida);

        sut.Puntos.Should().Be(0);
        elegida.EsIncorrecta.Should().BeTrue();
        sut.Opciones[sut.IndiceCorrecto].EsCorrecta.Should().BeTrue();
        sut.TemaReveladoTexto.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Responder_UnaSegundaVez_DeberiaIgnorarse()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        ResponderBien(sut);
        ResponderBien(sut);
        ResponderMal(sut);

        sut.Puntos.Should().Be(100);
        sut.Aciertos.Should().Be(1);
    }

    [Fact]
    public async Task ResponderNumero_DeberiaResponderConLaOpcionDeEseNumero_YIgnorarLosInvalidos()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        foreach (var invalido in new[] { "0", "5", "x", null })
            sut.ResponderNumeroCommand.Execute(invalido);
        sut.HaRespondido.Should().BeFalse();

        sut.ResponderNumeroCommand.Execute((sut.IndiceCorrecto + 1).ToString());

        sut.UltimoFueAcierto.Should().BeTrue();
    }

    [Fact]
    public async Task LosClipsNuevos_VanALaCacheTemporal_NoATuCarpetaDeMusica()
    {
        var sut = await CrearSutAsync(6);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<CancellationToken>()), Times.Never,
            "jugar no debe dejar canciones en la biblioteca de música ni entradas en la pestaña Descargas");
    }

    [Fact]
    public async Task ElVolumen_SaleDeLosAjustesDeLaMusica_YSeGuardaAlCambiarlo()
    {
        var ajustes = new AppSettings { VolumenMusica = 0.3 };
        var servicio = new Mock<ISettingsService>();
        servicio.Setup(s => s.ObtenerConfiguracion()).Returns(ajustes);
        _player.SetupProperty(p => p.Volumen);
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(Biblioteca(6));

        var sut = new AdivinaOpEdViewModel(_db.Object, _themes.Object, _descargas.Object, _player.Object, settings: servicio.Object);

        sut.Volumen.Should().Be(0.3);
        _player.Object.Volumen.Should().Be(0.3);

        sut.Volumen = 0.6;
        await Task.Delay(50);

        _player.Object.Volumen.Should().Be(0.6);
        ajustes.VolumenMusica.Should().Be(0.6);
        servicio.Verify(s => s.GuardarConfiguracionAsync(ajustes), Times.Once);
    }

    [Fact]
    public async Task AlSonarUnClip_SeSabeCuantoDura_ParaMostrarSuAvance()
    {
        var sut = await CrearSutAsync(6);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.DuracionSonando.Should().Be(TimeSpan.FromSeconds(AdivinaOpEdJuego.SegundosIniciales));

        ResponderBien(sut);
        sut.DuracionSonando.Should().Be(TimeSpan.FromSeconds(AdivinaOpEdJuego.SegundosRevelacion));
    }

    [Fact]
    public async Task SiElClipNoSePuedeReproducir_DejaDeMarcarQueSuenaYAvisa()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        _player.Raise(p => p.ReproduccionFallida += null, EventArgs.Empty);

        sut.EstaSonando.Should().BeFalse();
        sut.AudioFallido.Should().BeTrue();

        sut.EscucharCommand.Execute(null);
        sut.AudioFallido.Should().BeFalse("al reintentar se quita el aviso hasta saber si vuelve a fallar");
    }

    [Fact]
    public async Task Escuchar_DespuesDeResponder_DeberiaReproducirElTrozoLargo()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        ResponderBien(sut);
        _player.Invocations.Clear();

        sut.EscucharCommand.Execute(null);

        _player.Verify(p => p.Reproducir(It.IsAny<string>(), TimeSpan.FromSeconds(AdivinaOpEdJuego.SegundosRevelacion), It.IsAny<double>()), Times.Once);
    }

    // === Avance y fin de partida ===

    [Fact]
    public async Task Siguiente_SinResponderAntes_NoDeberiaAvanzar()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        await sut.SiguienteCommand.ExecuteAsync(null);

        sut.RondaNumero.Should().Be(1);
    }

    [Fact]
    public async Task Siguiente_DespuesDeResponder_DeberiaPasarALaSiguienteRondaConEstadoLimpio()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);
        ResponderBien(sut);

        await sut.SiguienteCommand.ExecuteAsync(null);

        sut.RondaNumero.Should().Be(2);
        sut.HaRespondido.Should().BeFalse();
        sut.Pistas.Should().BeEmpty();
        sut.SegundosClip.Should().Be(AdivinaOpEdJuego.SegundosIniciales);
        sut.Opciones.Should().OnlyContain(o => !o.EsCorrecta && !o.EsIncorrecta);
        sut.PortadaRespuesta.Should().BeEmpty();
        sut.Puntos.Should().Be(60, "los puntos se conservan entre rondas");
    }

    [Fact]
    public async Task PartidaCompleta_ConTodosLosAciertosSinAyudas_DeberiaTerminarEnElResumenConPuntuacionMaxima()
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
        sut.ResumenAciertosTexto.Should().Contain("5");
        _player.Verify(p => p.Detener(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task UltimaRonda_DeberiaOfrecerVerResultadoEnVezDeSiguiente()
    {
        var sut = await CrearSutAsync(4);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        ResponderBien(sut);
        string textoRondaIntermedia = sut.TextoSiguiente;
        await sut.SiguienteCommand.ExecuteAsync(null);
        for (int i = 0; i < 2; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        ResponderBien(sut); // 4.ª y última ronda

        sut.TextoSiguiente.Should().NotBe(textoRondaIntermedia, "en la última ronda el botón cambia de \"Siguiente\" a \"Ver resultado\"");
    }

    [Fact]
    public async Task PartidaCompleta_ConLosDatosYaEnCache_NoDeberiaVolverADescargarNada()
    {
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(true);
        var sut = await CrearSutAsync(5);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _player.Verify(p => p.Reproducir(It.Is<string>(r => r.Contains(@"C:\Music\")), It.IsAny<TimeSpan>(), It.IsAny<double>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task IniciarPartida_DespuesDeTerminar_DeberiaReiniciarTodo()
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
        sut.MostrarContenidoRonda.Should().BeTrue();
    }

    // === Sin red / AnimeThemes falla ===

    [Fact]
    public async Task SinConexionYSinTemasDescargados_DeberiaVolverAlInicioConUnMensajeDeError()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var sut = await CrearSutAsync(5);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
        sut.MensajeError.Should().NotBeEmpty();
        sut.EstaPreparandoRonda.Should().BeFalse();
        _player.Verify(p => p.Reproducir(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public async Task ElError_DeberiaDesaparecerAlIniciarOtraPartidaQueSiFunciona()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var sut = await CrearSutAsync(5);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.HayError.Should().BeTrue();

        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new List<AnimeThemeInfo> { TemaApi(id) });
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.HayError.Should().BeFalse();
        sut.EsJugando.Should().BeTrue();
    }

    [Fact]
    public async Task SinConexion_DeberiaJugarConLosTemasYaDescargadosYSinArtistaNiCancion()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        _descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible> { Local() });
        var sut = await CrearSutAsync(5);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        for (int i = 0; i < 20; i++) sut.PedirPistaCommand.Execute(null);
        ResponderBien(sut);

        sut.EsJugando.Should().BeTrue();
        sut.Pistas.Select(p => p.Etiqueta).Should().HaveCount(2, "solo estreno y géneros: sin red no se conoce el artista ni la canción");
        sut.TemaReveladoTexto.Should().NotContain("—", "sin red solo se conoce el tipo de tema");
        sut.Puntos.Should().Be(AdivinaAnimeJuego.Puntos(1 + 4));
    }

    [Fact]
    public async Task SiAnimeThemesNoRespondeAtiempo_DeberiaPasarASoloLocalTrasDosTiemposAgotados()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (_, ct) =>
            {
                // Igual que el servicio real: al agotarse el tiempo no lanza, devuelve vacío.
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                return new List<AnimeThemeInfo>();
            });
        _descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible> { Local() });
        var sut = await CrearSutAsync(5);
        sut.TiempoMaximoApi = TimeSpan.FromMilliseconds(60);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        for (int i = 0; i < 5; i++)
        {
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.EsResumen.Should().BeTrue("las 5 rondas salieron de los temas descargados");
        sut.SoloLocal.Should().BeTrue();
        LlamadasATemas().Should().Be(2, "tras 2 tiempos agotados seguidos no se vuelve a consultar AnimeThemes en esta partida");
    }

    [Fact]
    public async Task SiLaDescargaFalla_DeberiaSaltarEseAnimeYUsarOtro()
    {
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(1, It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        int rondas = 0;
        while (sut.EsJugando)
        {
            rondas++;
            sut.Opciones[sut.IndiceCorrecto].Titulo.Should().NotBe(Titulos[0], "su clip no se pudo descargar");
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        rondas.Should().Be(5, "el sexto anime no pudo dar ronda");
        sut.EsResumen.Should().BeTrue();
        sut.TotalRondas.Should().Be(5, "el resumen cuenta las rondas realmente jugadas");
    }

    [Fact]
    public async Task SiTodoFalla_DeberiaRendirseTrasElLimiteDeFallosSeguidos()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var sut = await CrearSutAsync(12);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.HayError.Should().BeTrue();
        LlamadasATemas().Should().Be(AdivinaOpEdViewModel.LimiteFallosSeguidos, "no debe recorrer toda la biblioteca esperando");
    }

    [Fact]
    public async Task ConTemasDeVariosTipos_DeberiaPreferirElYaDescargado()
    {
        _themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new List<AnimeThemeInfo> { TemaApi(id, "OP1"), TemaApi(id, "OP2"), TemaApi(id, "ED1") });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.Is<AnimeThemeInfo>(t => t.Slug == "ED1"))).Returns(true);
        var sut = await CrearSutAsync(5);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        for (int i = 0; i < 5; i++)
        {
            sut.TipoTemaTexto.Should().Contain("Ending");
            ResponderBien(sut);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }
    }

    // === Sonido y estado ===

    [Fact]
    public async Task Detener_DeberiaCortarElSonidoSinAfectarALaPartida()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        _player.Invocations.Clear();

        sut.Detener();

        _player.Verify(p => p.Detener(), Times.Once);
        sut.EstaSonando.Should().BeFalse();
        sut.EsJugando.Should().BeTrue();
        sut.RondaNumero.Should().Be(1);
    }

    [Fact]
    public async Task CuandoElClipTermina_DeberiaDejarDeMostrarseComoSonando()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.EstaSonando.Should().BeTrue();

        _player.Raise(p => p.ReproduccionTerminada += null, EventArgs.Empty);

        sut.EstaSonando.Should().BeFalse();
    }

    [Fact]
    public async Task VolverAlInicio_DeberiaCortarElSonidoYPermitirEmpezarOtraPartida()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        _player.Invocations.Clear();

        await sut.VolverAlInicioCommand.ExecuteAsync(null);

        sut.EsInicio.Should().BeTrue();
        _player.Verify(p => p.Detener(), Times.AtLeastOnce);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.EsJugando.Should().BeTrue();
        sut.RondaNumero.Should().Be(1);
    }

    [Fact]
    public async Task CambioDeIdioma_EnMitadDeRonda_NoDeberiaPerderElEstado()
    {
        var sut = await CrearSutAsync(6);
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        sut.PedirPistaCommand.Execute(null);
        sut.PedirPistaCommand.Execute(null);
        ResponderBien(sut);

        sut.Receive(new IdiomaCambiadoMensaje());

        sut.Pistas.Should().HaveCount(1);
        sut.Puntos.Should().Be(60);
        sut.TemaReveladoTexto.Should().NotBeEmpty();
        sut.ResultadoTexto.Should().NotBeEmpty();
    }

    // === Textos ===

    [Fact]
    public void TextoTipoTema_DeberiaDistinguirOpeningsYEndingsConOSinNumero()
    {
        AdivinaOpEdViewModel.TextoTipoTema(new TemaParaJugar("OP", "OP2", "", "", "x")).Should().Contain("Opening").And.Contain("2");
        AdivinaOpEdViewModel.TextoTipoTema(new TemaParaJugar("ED", "ED1-TV", "", "", "x")).Should().Contain("Ending").And.Contain("1");
        AdivinaOpEdViewModel.TextoTipoTema(new TemaParaJugar("ED", "raro", "", "", "x")).Should().Contain("Ending").And.NotMatchRegex(@"\d");
    }

    [Fact]
    public void TextoTemaRevelado_DeberiaUnirTipoCancionYArtistaSegunLoQueSeConozca()
    {
        AdivinaOpEdViewModel.TextoTemaRevelado(new TemaParaJugar("OP", "OP1", "Guren no Yumiya", "Linked Horizon", "x"))
            .Should().Contain("Guren no Yumiya — Linked Horizon");
        AdivinaOpEdViewModel.TextoTemaRevelado(new TemaParaJugar("OP", "OP1", "Guren no Yumiya", "", "x"))
            .Should().Contain("Guren no Yumiya").And.NotContain("—");
        AdivinaOpEdViewModel.TextoTemaRevelado(new TemaParaJugar("OP", "OP1", "", "", "x"))
            .Should().NotContain("·");
    }
}
