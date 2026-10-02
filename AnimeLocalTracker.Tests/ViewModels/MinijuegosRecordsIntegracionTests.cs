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

/// <summary>Récords y logros desde los juegos: qué se guarda al terminar una partida y qué se muestra.</summary>
public class MinijuegosRecordsIntegracionTests
{
    private static readonly string[] Titulos = ["Naruto", "Bleach", "Death Note", "Clannad", "Monster", "Gintama", "Berserk", "Trigun"];

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IMinijuegosRecordsService> _records = new();
    private readonly List<PartidaMinijuego> _guardadas = new();
    private int _cantidadAnimes = Titulos.Length;

    public MinijuegosRecordsIntegracionTests()
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(() => Titulos.Take(_cantidadAnimes).Select((t, i) => new AnimeItem
        {
            AniListId = i + 1, Titulo = t, Generos = "Action, Drama", AnioLanzamiento = 2000 + i, Temporada = "FALL", TotalEpisodios = 12,
            UrlPortada = $"https://img/{i + 1}.jpg"
        }).ToList());
        _records.Setup(r => r.ObtenerAsync(It.IsAny<string>())).ReturnsAsync(RecordsMinijuego.Vacio);
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>()))
            .ReturnsAsync((PartidaMinijuego p) =>
            {
                _guardadas.Add(p);
                return new ResultadoPartida(false, 0, RecordsMinijuego.Calcular([p]));
            });
    }

    // === Adivina el anime ===

    private async Task<AdivinaAnimeViewModel> CrearAnimeAsync(int animes = 5)
    {
        _cantidadAnimes = animes; // la partida dura tantas rondas como animes (hasta 10)
        var sut = new AdivinaAnimeViewModel(_db.Object, _records.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        return sut;
    }

    private static void Responder(AdivinaAnimeViewModel sut, bool bien) =>
        sut.ResponderCommand.Execute(bien ? sut.Opciones[sut.IndiceCorrecto] : sut.Opciones.First(o => o.Indice != sut.IndiceCorrecto));

    private static async Task JugarAsync(AdivinaAnimeViewModel sut, params bool[] aciertos)
    {
        foreach (bool acierto in aciertos)
        {
            Responder(sut, acierto);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }
    }

    [Fact]
    public async Task Anime_AlTerminarUnaPartida_DeberiaGuardarloTodo()
    {
        var sut = await CrearAnimeAsync();

        await JugarAsync(sut, true, true, true, true, true);

        sut.EsResumen.Should().BeTrue();
        var p = _guardadas.Should().ContainSingle().Subject;
        p.JuegoId.Should().Be(JuegosMinijuego.AdivinaAnime);
        (p.Puntos, p.Rondas, p.Aciertos, p.RachaMaxima).Should().Be((500, 5, 5, 5));
        p.FechaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
        p.FechaUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Anime_LaRachaEsLaMayorCadenaDeAciertosSeguidos()
    {
        var sut = await CrearAnimeAsync();

        await JugarAsync(sut, true, false, true, true, true);

        var p = _guardadas.Single();
        p.RachaMaxima.Should().Be(3);
        p.Aciertos.Should().Be(4);
    }

    [Fact]
    public async Task Anime_UnaPartidaSinAciertos_SeGuardaConRachaCero()
    {
        var sut = await CrearAnimeAsync();

        await JugarAsync(sut, false, false, false, false, false);

        var p = _guardadas.Single();
        (p.Puntos, p.Aciertos, p.RachaMaxima).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Anime_LaRachaNoSeArrastraDeUnaPartidaALaSiguiente()
    {
        var sut = await CrearAnimeAsync();
        await JugarAsync(sut, true, true, true, true, true);

        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        await JugarAsync(sut, false, true, false, false, false);

        _guardadas.Should().HaveCount(2);
        _guardadas[1].RachaMaxima.Should().Be(1);
        _guardadas[1].Aciertos.Should().Be(1);
    }

    [Fact]
    public async Task Anime_UnaPartidaAbandonada_NoSeGuarda()
    {
        var sut = await CrearAnimeAsync();
        await JugarAsync(sut, true, true);

        await sut.VolverAlInicioCommand.ExecuteAsync(null);

        _guardadas.Should().BeEmpty("solo cuentan las partidas que llegan al resumen");
    }

    [Fact]
    public async Task Anime_SiElRegistroDeRecordFalla_ElResumenSeMuestraIgual()
    {
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>())).ThrowsAsync(new InvalidOperationException("disco lleno"));
        var sut = await CrearAnimeAsync();

        var act = async () => await JugarAsync(sut, true, true, true, true, true);

        await act.Should().NotThrowAsync();
        sut.EsResumen.Should().BeTrue();
        sut.Puntos.Should().Be(500);
        sut.EsNuevoRecord.Should().BeFalse();
    }

    [Fact]
    public async Task Anime_UnNuevoRecord_SeMuestraConLaMarcaAnterior()
    {
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>()))
            .ReturnsAsync(new ResultadoPartida(true, 380, new RecordsMinijuego(500, DateTime.UtcNow, 4, 22, 40, 0, 5)));
        var sut = await CrearAnimeAsync();

        await JugarAsync(sut, true, true, true, true, true);

        sut.EsNuevoRecord.Should().BeTrue();
        sut.MejorAnterior.Should().Be(380);
        sut.MostrarRecordEnResumen.Should().BeTrue();
        sut.ResumenRecordTexto.Should().Contain("380");
        sut.HayRecord.Should().BeTrue();
    }

    [Fact]
    public async Task Anime_SinRecordNuevo_SeMuestraElRecordVigenteComoReferencia()
    {
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>()))
            .ReturnsAsync(new ResultadoPartida(false, 900, new RecordsMinijuego(900, DateTime.UtcNow, 6, 30, 60, 0, 8)));
        var sut = await CrearAnimeAsync();

        await JugarAsync(sut, false, false, false, false, false);

        sut.EsNuevoRecord.Should().BeFalse();
        sut.MostrarRecordEnResumen.Should().BeTrue();
        sut.ResumenRecordTexto.Should().Contain("900");
    }

    [Fact]
    public async Task Anime_ElNuevoRecordSeReiniciaAlEmpezarOtraPartida()
    {
        _records.Setup(r => r.RegistrarAsync(It.IsAny<PartidaMinijuego>()))
            .ReturnsAsync(new ResultadoPartida(true, 100, new RecordsMinijuego(500, DateTime.UtcNow, 2, 5, 10, 0, 5)));
        var sut = await CrearAnimeAsync();
        await JugarAsync(sut, true, true, true, true, true);
        sut.EsNuevoRecord.Should().BeTrue();

        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        sut.EsNuevoRecord.Should().BeFalse("la insignia de la partida anterior no debe verse en la nueva");
    }

    [Fact]
    public async Task Anime_AlPrepararse_DeberiaCargarElRecordGuardado()
    {
        _records.Setup(r => r.ObtenerAsync(JuegosMinijuego.AdivinaAnime))
            .ReturnsAsync(new RecordsMinijuego(760, DateTime.UtcNow, 12, 82, 120, 1, 7));
        var sut = new AdivinaAnimeViewModel(_db.Object, _records.Object);

        sut.HayRecord.Should().BeFalse();
        sut.RecordTexto.Should().BeEmpty();

        await sut.PrepararAsync();

        sut.HayRecord.Should().BeTrue();
        sut.RecordTexto.Should().Contain("760").And.Contain("12").And.Contain("68");
    }

    [Fact]
    public async Task Anime_SiNoSePuedenLeerLosRecords_NoDeberiaFallar()
    {
        _records.Setup(r => r.ObtenerAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("db"));
        var sut = new AdivinaAnimeViewModel(_db.Object, _records.Object);

        var act = async () => await sut.PrepararAsync();

        await act.Should().NotThrowAsync();
        sut.HayRecord.Should().BeFalse();
        sut.PuedeJugar.Should().BeTrue("un fallo con los récords no impide jugar");
    }

    [Fact]
    public async Task Anime_SinServicioDeRecords_TodoFuncionaIgual()
    {
        _cantidadAnimes = 5;
        var sut = new AdivinaAnimeViewModel(_db.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        await sut.IniciarPartidaCommand.ExecuteAsync(null);

        await JugarAsync(sut, true, true, true, true, true);

        sut.EsResumen.Should().BeTrue();
        sut.HayRecord.Should().BeFalse();
        sut.ResumenRecordTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task Anime_CambioDeIdioma_NoDeberiaPerderElRecord()
    {
        _records.Setup(r => r.ObtenerAsync(It.IsAny<string>())).ReturnsAsync(new RecordsMinijuego(760, DateTime.UtcNow, 12, 82, 120, 1, 7));
        var sut = new AdivinaAnimeViewModel(_db.Object, _records.Object);
        await sut.PrepararAsync();

        sut.Receive(new IdiomaCambiadoMensaje());

        sut.RecordTexto.Should().Contain("760");
    }

    // === Adivina el OP/ED ===

    private async Task<AdivinaOpEdViewModel> CrearOpEdAsync(bool conTemas = true)
    {
        var themes = new Mock<IAnimeThemesService>();
        themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => conTemas
                ? new List<AnimeThemeInfo> { new() { Slug = "OP1", Tipo = "OP", TituloCancion = $"Song {id}", AudioUrlOgg = "https://a/x.ogg" } }
                : new List<AnimeThemeInfo>());
        var descargas = new Mock<IAnimeThemesDownloadService>();
        descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);
        descargas.Setup(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, AnimeThemeInfo t, IProgress<double>? _, CancellationToken _) => $@"C:\Music\{id}\{t.Slug}.mp3");
        descargas.Setup(d => d.ListarDescargasLocales(It.IsAny<int>())).Returns(new List<TemaLocalDisponible>());

        var sut = new AdivinaOpEdViewModel(_db.Object, themes.Object, descargas.Object, Mock.Of<IClipPlayer>(), _records.Object) { Rng = new Random(42) };
        await sut.PrepararAsync();
        await sut.IniciarPartidaCommand.ExecuteAsync(null);
        return sut;
    }

    [Fact]
    public async Task OpEd_AlTerminarUnaPartida_DeberiaGuardarlaConSuPropioJuegoId()
    {
        var sut = await CrearOpEdAsync();

        for (int i = 0; i < 8 && sut.EsJugando; i++)
        {
            sut.ResponderCommand.Execute(sut.Opciones[sut.IndiceCorrecto]);
            await sut.SiguienteCommand.ExecuteAsync(null);
        }

        sut.EsResumen.Should().BeTrue();
        var p = _guardadas.Should().ContainSingle().Subject;
        p.JuegoId.Should().Be(JuegosMinijuego.AdivinaOpEd);
        (p.Puntos, p.Rondas, p.Aciertos, p.RachaMaxima).Should().Be((800, 8, 8, 8));
    }

    [Fact]
    public async Task OpEd_SiNoSePudoPrepararNingunaRonda_NoSeGuardaNada()
    {
        var sut = await CrearOpEdAsync(conTemas: false);

        sut.EsInicio.Should().BeTrue();
        sut.HayError.Should().BeTrue();
        _guardadas.Should().BeEmpty("una partida sin rondas no es una partida");
    }

    [Fact]
    public async Task OpEd_UnaPartidaAbandonada_NoSeGuarda()
    {
        var sut = await CrearOpEdAsync();
        sut.ResponderCommand.Execute(sut.Opciones[sut.IndiceCorrecto]);

        await sut.VolverAlInicioCommand.ExecuteAsync(null);

        _guardadas.Should().BeEmpty();
    }

    // === Menú ===

    [Fact]
    public async Task Menu_AlEntrar_DeberiaRefrescarLosRecordsDeLosDosJuegos()
    {
        _records.Setup(r => r.ObtenerAsync(JuegosMinijuego.AdivinaAnime)).ReturnsAsync(new RecordsMinijuego(760, DateTime.UtcNow, 3, 20, 30, 0, 4));
        _records.Setup(r => r.ObtenerAsync(JuegosMinijuego.AdivinaOpEd)).ReturnsAsync(new RecordsMinijuego(430, DateTime.UtcNow, 2, 9, 20, 0, 3));
        _records.Setup(r => r.ObtenerAsync(JuegosMinijuego.AdivinaPersonaje)).ReturnsAsync(new RecordsMinijuego(310, DateTime.UtcNow, 1, 6, 10, 0, 2));
        var hub = new MinijuegosViewModel(
            new AdivinaAnimeViewModel(_db.Object, _records.Object),
            new AdivinaOpEdViewModel(_db.Object, Mock.Of<IAnimeThemesService>(), Mock.Of<IAnimeThemesDownloadService>(), Mock.Of<IClipPlayer>(), _records.Object),
            new AdivinaPersonajeViewModel(_db.Object, Mock.Of<IPersonajesService>(), _records.Object));

        await hub.PrepararAsync();

        hub.AdivinaAnime.RecordTexto.Should().Contain("760");
        hub.AdivinaOpEd.RecordTexto.Should().Contain("430");
        hub.AdivinaPersonaje.RecordTexto.Should().Contain("310");
        _db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Never, "en el menú solo se leen los récords, no la biblioteca");
    }
}
