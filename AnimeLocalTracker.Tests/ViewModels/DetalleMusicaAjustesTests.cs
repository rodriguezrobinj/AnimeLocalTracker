using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Música de la ficha: volumen y reproducción continua guardados en los ajustes, paso automático al siguiente tema
/// y duración visible antes de reproducir. Audio, descargas, ajustes y ffprobe simulados.</summary>
public class DetalleMusicaAjustesTests : IDisposable
{
    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _downloadService = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly Mock<IAudioDurationService> _duraciones = new();
    private readonly FakeAudioTrackPlayer _player = new();
    private readonly AppSettings _config = new() { VolumenMusica = 0.8, ReproduccionContinuaMusica = true };
    private readonly List<double> _volumenesGuardados = new();

    private readonly AnimeThemeInfo _op1 = Tema("OP1");
    private readonly AnimeThemeInfo _ed1 = Tema("ED1");
    private readonly AnimeThemeInfo _ed2 = Tema("ED2");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private static AnimeThemeInfo Tema(string slug) => new()
    {
        Slug = slug,
        Tipo = slug.StartsWith("ED", StringComparison.Ordinal) ? "ED" : "OP",
        TituloCancion = $"Canción {slug}",
        AudioUrlOgg = $"https://a.animethemes.moe/{slug}.ogg"
    };

    private static string RutaGuardada(AnimeThemeInfo t) => $@"C:\Music\7\{t.Slug}.mp3";
    private static string RutaPrevia(AnimeThemeInfo t) => $@"C:\Previews\7\{t.Slug}.mp3";

    public DetalleMusicaAjustesTests()
    {
        _themes.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { _op1, _ed1, _ed2 });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns((string?)null);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(7, It.IsAny<AnimeThemeInfo>())).Returns((int _, AnimeThemeInfo t) => RutaGuardada(t));

        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()))
            .Returns((AppSettings c) => { lock (_volumenesGuardados) _volumenesGuardados.Add(c.VolumenMusica); return Task.CompletedTask; });
    }

    private void Descargados(params AnimeThemeInfo[] temas)
    {
        foreach (var t in temas) _descargas.Setup(d => d.EstaDescargado(7, t)).Returns(true);
    }

    private void ConVistaPrevia(params AnimeThemeInfo[] temas)
    {
        foreach (var t in temas) _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, t)).Returns(RutaPrevia(t));
    }

    private async Task<DetalleViewModel> AbrirFichaAsync(bool conAjustes = true, bool conDuraciones = false)
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _downloadService.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(
            _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _downloadService.Object,
            settingsService: conAjustes ? _ajustes.Object : null,
            animeThemesService: _themes.Object, animeThemesDownload: _descargas.Object, audioTrackPlayer: _player,
            audioDuration: conDuraciones ? _duraciones.Object : null);
        sut.Musica.RetrasoGuardadoAjustesMusica = TimeSpan.FromMilliseconds(60);
        await sut.InicializarAsync(new AnimeItem { AniListId = 7, Titulo = "Grand Blue" });
        await sut.Musica.CargarTemasMusicalesAsync();
        return sut;
    }

    private static async Task EsperarAsync(Func<bool> condicion, int milisegundos = 3000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milisegundos);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(10);
        condicion().Should().BeTrue("la condición esperada no se cumplió a tiempo");
    }

    private int Guardados() { lock (_volumenesGuardados) return _volumenesGuardados.Count; }

    // === Volumen y reproducción continua guardados ===

    [Fact]
    public async Task Ajustes_AlAbrirLaFicha_DeberiaUsarElVolumenYLaContinuaGuardados()
    {
        _config.VolumenMusica = 0.35;
        _config.ReproduccionContinuaMusica = false;

        var sut = await AbrirFichaAsync();

        sut.Musica.VolumenMusica.Should().Be(0.35);
        sut.Musica.ReproduccionContinuaMusica.Should().BeFalse();
    }

    [Fact]
    public async Task Ajustes_UnVolumenGuardadoFueraDeRango_DeberiaAjustarseYNoLanzar()
    {
        _config.VolumenMusica = 9;

        var sut = await AbrirFichaAsync();

        sut.Musica.VolumenMusica.Should().Be(1);
    }

    [Fact]
    public async Task Ajustes_CargarLosAjustesAlAbrir_NoDeberiaVolverAEscribirElArchivo()
    {
        _config.VolumenMusica = 0.4;

        await AbrirFichaAsync();
        await Task.Delay(200);

        Guardados().Should().Be(0, "leer lo guardado no es un cambio del usuario");
    }

    [Fact]
    public async Task Ajustes_ElVolumenElegidoSeAplicaAlReproductorYSeGuardaDespuesDeUnRato()
    {
        Descargados(_op1);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);

        sut.Musica.VolumenMusica = 0.25;

        _player.Volumen.Should().Be(0.25);
        await EsperarAsync(() => Guardados() == 1);
        _volumenesGuardados.Single().Should().Be(0.25);
        _config.VolumenMusica.Should().Be(0.25);
    }

    [Fact]
    public async Task Ajustes_MoverElSliderVariasVeces_DeberiaGuardarSoloElUltimoValor()
    {
        var sut = await AbrirFichaAsync();

        foreach (var v in new[] { 0.7, 0.6, 0.5, 0.4, 0.3 }) sut.Musica.VolumenMusica = v;

        await EsperarAsync(() => Guardados() >= 1);
        await Task.Delay(250);
        Guardados().Should().Be(1, "cada guardado reescribe todo settings.json: solo cuando se deja de mover");
        _volumenesGuardados.Single().Should().Be(0.3);
    }

    [Fact]
    public async Task Ajustes_AlSalirDeLaFicha_DeberiaGuardarYaUnCambioReciente()
    {
        var sut = await AbrirFichaAsync();
        sut.Musica.RetrasoGuardadoAjustesMusica = TimeSpan.FromSeconds(30);

        sut.Musica.VolumenMusica = 0.15;
        Guardados().Should().Be(0);
        sut.Musica.DetenerMusica();

        await EsperarAsync(() => Guardados() == 1);
        _volumenesGuardados.Single().Should().Be(0.15);
    }

    [Fact]
    public async Task Ajustes_AlDescartarLaFicha_DeberiaGuardarYaUnCambioReciente()
    {
        var sut = await AbrirFichaAsync();
        sut.Musica.RetrasoGuardadoAjustesMusica = TimeSpan.FromSeconds(30);
        sut.Musica.VolumenMusica = 0.55;

        sut.Dispose();

        await EsperarAsync(() => Guardados() == 1);
        _volumenesGuardados.Single().Should().Be(0.55);
    }

    [Fact]
    public async Task Ajustes_SalirDeLaFichaSinCambios_NoDeberiaEscribirNada()
    {
        var sut = await AbrirFichaAsync();

        sut.Musica.DetenerMusica();
        await Task.Delay(150);

        Guardados().Should().Be(0);
    }

    [Fact]
    public async Task Ajustes_AlternarLaReproduccionContinua_DeberiaGuardarseEnSeguida()
    {
        var sut = await AbrirFichaAsync();
        sut.Musica.RetrasoGuardadoAjustesMusica = TimeSpan.FromSeconds(30);

        sut.Musica.AlternarReproduccionContinuaMusicaCommand.Execute(null);

        sut.Musica.ReproduccionContinuaMusica.Should().BeFalse();
        await EsperarAsync(() => _config.ReproduccionContinuaMusica == false);
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(_config), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Ajustes_SinServicioDeAjustes_TodoFuncionaSinGuardar()
    {
        var sut = await AbrirFichaAsync(conAjustes: false);

        var act = () => { sut.Musica.VolumenMusica = 0.2; sut.Musica.AlternarReproduccionContinuaMusicaCommand.Execute(null); sut.Musica.DetenerMusica(); };

        act.Should().NotThrow();
        await Task.Delay(150);
        Guardados().Should().Be(0);
    }

    [Fact]
    public async Task Ajustes_SiGuardarFalla_NoDeberiaHundirLaFicha()
    {
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).ThrowsAsync(new InvalidOperationException("disco lleno"));
        var sut = await AbrirFichaAsync();

        var act = async () => { sut.Musica.VolumenMusica = 0.45; await Task.Delay(250); };

        await act.Should().NotThrowAsync();
        sut.Musica.VolumenMusica.Should().Be(0.45);
    }

    // === Pasar solo al siguiente ===

    [Fact]
    public async Task Continua_AlTerminarUnTema_DeberiaPasarAlSiguienteQueSePuedaEscuchar_SaltandoLosQueNoTienenArchivo()
    {
        Descargados(_op1, _ed2); // ED1 (el del medio) no tiene archivo
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);
        _player.Llamadas.Clear();

        _player.DispararTerminado();

        _player.RutaAbierta.Should().Be(RutaGuardada(_ed2));
        sut.Musica.TemasMusicales[0].Reproduciendo.Should().BeFalse();
        sut.Musica.TemasMusicales[0].EsPistaActual.Should().BeFalse();
        sut.Musica.TemasMusicales[2].Reproduciendo.Should().BeTrue();
        sut.Musica.TemasMusicales[2].EsPistaActual.Should().BeTrue();
        _player.EstaSonando.Should().BeTrue();
    }

    [Fact]
    public async Task Continua_UnaVistaPreviaYaLista_TambienCuentaComoSiguiente()
    {
        Descargados(_op1);
        ConVistaPrevia(_ed1);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);

        _player.DispararTerminado();

        _player.RutaAbierta.Should().Be(RutaPrevia(_ed1));
        sut.Musica.TemasMusicales[1].Reproduciendo.Should().BeTrue();
    }

    [Fact]
    public async Task Continua_NoDeberiaBajarNiPrepararNadaPorSuCuenta()
    {
        Descargados(_op1); // ED1 y ED2 sin archivo
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);

        _player.DispararTerminado();

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _player.VecesQue("Abrir").Should().Be(1, "no hay ningún otro tema que se pueda escuchar");
    }

    [Fact]
    public async Task Continua_AlTerminarElUltimoTema_NoDeberiaEmpezarDeNuevo()
    {
        Descargados(_op1, _ed1, _ed2);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[2]); // el último
        _player.Llamadas.Clear();

        _player.DispararTerminado();

        _player.Llamadas.Should().NotContain(l => l.StartsWith("Abrir", StringComparison.Ordinal), "no da la vuelta a la lista");
        sut.Musica.TemasMusicales[2].Reproduciendo.Should().BeFalse();
    }

    [Fact]
    public async Task Continua_Desactivada_NoDeberiaPasarAlSiguiente()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproduccionContinuaMusica = false;
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);
        _player.Llamadas.Clear();

        _player.DispararTerminado();

        _player.Llamadas.Should().NotContain(l => l.StartsWith("Abrir", StringComparison.Ordinal));
        sut.Musica.TemasMusicales[1].Reproduciendo.Should().BeFalse();
    }

    [Fact]
    public async Task Continua_EncadenaTodosLosTemasEscuchablesEnOrden()
    {
        Descargados(_op1, _ed1, _ed2);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);

        _player.DispararTerminado();
        _player.DispararTerminado();

        _player.Llamadas.Where(l => l.StartsWith("Abrir", StringComparison.Ordinal)).Should().Equal(
            $"Abrir:{RutaGuardada(_op1)}", $"Abrir:{RutaGuardada(_ed1)}", $"Abrir:{RutaGuardada(_ed2)}");
    }

    [Fact]
    public async Task Continua_UnaPistaPausadaNoPasaAlSiguiente()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirFichaAsync();
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]);
        sut.Musica.ReproducirTemaCommand.Execute(sut.Musica.TemasMusicales[0]); // pausa

        sut.Musica.TemasMusicales[1].Reproduciendo.Should().BeFalse();
        _player.VecesQue("Abrir").Should().Be(1);
    }

    // === Duración antes de reproducir ===

    [Fact]
    public async Task Duracion_DeberiaLeerseParaLosTemasQueYaTienenArchivo_YSoloParaEsos()
    {
        Descargados(_op1);
        ConVistaPrevia(_ed1); // ED2 sin nada
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaGuardada(_op1), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.FromSeconds(92));
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaPrevia(_ed1), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.FromSeconds(88.6));

        var sut = await AbrirFichaAsync(conDuraciones: true);
        await sut.Musica.CargaDuracionesTarea;

        sut.Musica.TemasMusicales[0].DuracionArchivoTexto.Should().Be("1:32");
        sut.Musica.TemasMusicales[1].DuracionArchivoTexto.Should().Be("1:28");
        sut.Musica.TemasMusicales[2].TieneDuracionArchivo.Should().BeFalse("no hay archivo del que leerla");
        sut.Musica.TemasMusicales[2].DuracionArchivoTexto.Should().BeEmpty();
        // Solo se pregunta por los temas con archivo; nunca por ED2 (sin nada). (Se lee más de una vez porque la ficha ya
        // cargó los temas al abrirse y el ayudante de la prueba los carga otra vez.)
        _duraciones.Verify(d => d.ObtenerDuracionAsync(RutaGuardada(_op1), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _duraciones.Verify(d => d.ObtenerDuracionAsync(RutaPrevia(_ed1), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _duraciones.Verify(d => d.ObtenerDuracionAsync(RutaGuardada(_ed2), It.IsAny<CancellationToken>()), Times.Never);
        _duraciones.Verify(d => d.ObtenerDuracionAsync(RutaPrevia(_ed2), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Duracion_SiNoSePuedeLeer_DeberiaQuedarVaciaSinFallar()
    {
        Descargados(_op1, _ed1);
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaGuardada(_op1), It.IsAny<CancellationToken>())).ReturnsAsync((TimeSpan?)null);
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaGuardada(_ed1), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("ffprobe"));

        var sut = await AbrirFichaAsync(conDuraciones: true);
        var act = async () => await sut.Musica.CargaDuracionesTarea;

        await act.Should().NotThrowAsync();
        sut.Musica.TemasMusicales.Should().OnlyContain(t => !t.TieneDuracionArchivo);
    }

    [Fact]
    public async Task Duracion_UnaVezPreparadaLaVistaPrevia_DeberiaMostrarseLaDuracion()
    {
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op1, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(RutaPrevia(_op1));
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op1)).Returns(RutaPrevia(_op1));
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaPrevia(_op1), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.FromSeconds(75));
        // Hasta que no se prepara, ObtenerRutaVistaPrevia no devuelve nada: se simula devolviéndola solo tras preparar.
        bool preparada = false;
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op1)).Returns(() => preparada ? RutaPrevia(_op1) : null);
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op1, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { preparada = true; return Task.FromResult<string?>(RutaPrevia(_op1)); });
        var sut = await AbrirFichaAsync(conDuraciones: true);
        var item = sut.Musica.TemasMusicales[0];
        item.TieneDuracionArchivo.Should().BeFalse();

        await sut.Musica.PrevisualizarTemaCommand.ExecuteAsync(item);

        await EsperarAsync(() => item.TieneDuracionArchivo);
        item.DuracionArchivoTexto.Should().Be("1:15");
    }

    [Fact]
    public async Task Duracion_UnaVezDescargadoElTema_DeberiaMostrarseLaDuracion()
    {
        bool descargado = false;
        _descargas.Setup(d => d.EstaDescargado(7, _op1)).Returns(() => descargado);
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, _op1, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { descargado = true; return Task.FromResult<string?>(RutaGuardada(_op1)); });
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaGuardada(_op1), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.FromSeconds(101));
        var sut = await AbrirFichaAsync(conDuraciones: true);
        var item = sut.Musica.TemasMusicales[0];

        await sut.Musica.DescargarTemaCommand.ExecuteAsync(item);

        await EsperarAsync(() => item.TieneDuracionArchivo);
        item.DuracionArchivoTexto.Should().Be("1:41");
    }

    [Fact]
    public async Task Duracion_AlEliminarElTema_DeberiaVolverASerDesconocida()
    {
        Descargados(_op1);
        _duraciones.Setup(d => d.ObtenerDuracionAsync(RutaGuardada(_op1), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.FromSeconds(92));
        var sut = await AbrirFichaAsync(conDuraciones: true);
        await sut.Musica.CargaDuracionesTarea;
        var item = sut.Musica.TemasMusicales[0];
        item.TieneDuracionArchivo.Should().BeTrue();

        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true); // acepta la confirmación
        sut.Musica.EliminarTemaCommand.Execute(item);

        item.TieneDuracionArchivo.Should().BeFalse();
        item.DuracionArchivoTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task Duracion_SinServicioDeDuraciones_LaFichaFuncionaIgual()
    {
        Descargados(_op1);

        var sut = await AbrirFichaAsync(conDuraciones: false);
        await sut.Musica.CargaDuracionesTarea;

        sut.Musica.TemasMusicales.Should().HaveCount(3);
        sut.Musica.TemasMusicales.Should().OnlyContain(t => !t.TieneDuracionArchivo);
    }

    [Fact]
    public async Task Duracion_AlAbrirLaPista_SiNoSeConocia_SeCompletaConLaDelReproductor()
    {
        Descargados(_op1);
        var sut = await AbrirFichaAsync(conDuraciones: false);
        var item = sut.Musica.TemasMusicales[0];

        sut.Musica.ReproducirTemaCommand.Execute(item);

        item.DuracionArchivoSegundos.Should().Be(90, "la del reproductor al abrir el archivo");
    }
}
