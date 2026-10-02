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

/// <summary>
/// Fase 5 de la investigación de la ficha (docs/investigacion-ficha-y-musica.md): buscador y contador de la ventana de música,
/// barra de "sonando ahora" (anterior, siguiente, aleatorio, repetir) y estado sin conexión. Audio, descargas y ajustes simulados.
/// </summary>
public class DetalleMusicaControlesTests : IDisposable
{
    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _downloadService = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly FakeAudioTrackPlayer _player = new();
    private readonly AppSettings _config = new();

    private readonly AnimeThemeInfo _op1 = Tema("OP1", "We Are!", "Hiroshi Kitadani");
    private readonly AnimeThemeInfo _op2 = Tema("OP2", "Believe", "Folder5");
    private readonly AnimeThemeInfo _ed1 = Tema("ED1", "Memories", "Maki Otsuki");
    private readonly AnimeThemeInfo _ed2 = Tema("ED2", "Canción del corazón", "");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private static AnimeThemeInfo Tema(string slug, string titulo, string artistas) => new()
    {
        Slug = slug,
        Tipo = slug.StartsWith("ED", StringComparison.Ordinal) ? "ED" : "OP",
        TituloCancion = titulo,
        Artistas = artistas,
        AudioUrlOgg = $"https://a.animethemes.moe/{slug}.ogg"
    };

    private static string RutaGuardada(AnimeThemeInfo t) => $@"C:\Music\7\{t.Slug}.mp3";
    private static string RutaPrevia(AnimeThemeInfo t) => $@"C:\Previews\7\{t.Slug}.mp3";

    public DetalleMusicaControlesTests()
    {
        _themes.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { _op1, _op2, _ed1, _ed2 });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns((string?)null);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(7, It.IsAny<AnimeThemeInfo>())).Returns((int _, AnimeThemeInfo t) => RutaGuardada(t));
        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
    }

    private void Descargados(params AnimeThemeInfo[] temas)
    {
        foreach (var t in temas) _descargas.Setup(d => d.EstaDescargado(7, t)).Returns(true);
    }

    private async Task<MusicaFichaViewModel> AbrirMusicaAsync()
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _downloadService.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var ficha = new DetalleViewModel(
            Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _downloadService.Object,
            settingsService: _ajustes.Object,
            animeThemesService: _themes.Object, animeThemesDownload: _descargas.Object, audioTrackPlayer: _player);
        await ficha.InicializarAsync(new AnimeItem { AniListId = 7, Titulo = "One Piece", EstadoUsuario = "COMPLETED" });
        await ficha.Musica.CargarTemasMusicalesAsync();
        return ficha.Musica;
    }

    private static IEnumerable<string> Visibles(MusicaFichaViewModel sut) => sut.TemasMusicalesVisibles.Select(t => t.Slug);

    private IEnumerable<string> Abiertos() =>
        _player.Llamadas.Where(l => l.StartsWith("Abrir:", StringComparison.Ordinal)).Select(l => System.IO.Path.GetFileNameWithoutExtension(l));

    private static async Task EsperarAsync(Func<bool> condicion, int milisegundos = 3000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milisegundos);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(10);
        condicion().Should().BeTrue("la condición esperada no se cumplió a tiempo");
    }

    // ── Buscador ──

    [Theory]
    [InlineData("we are", "OP1")]
    [InlineData("OTSUKI", "ED1")]
    [InlineData("ed", "ED1,ED2")]
    [InlineData("cancion del corazon", "ED2")] // sin acentos
    [InlineData("  believe ", "OP2")]
    [InlineData("zzz", "")]
    public async Task Buscar_FiltraPorTituloArtistaOEtiqueta_SinDistinguirMayusculasNiAcentos(string texto, string esperados)
    {
        var sut = await AbrirMusicaAsync();

        sut.BusquedaTemas = texto;

        Visibles(sut).Should().Equal(esperados.Split(',', StringSplitOptions.RemoveEmptyEntries));
        sut.SinResultadosDeTemas.Should().Be(esperados.Length == 0);
    }

    [Fact]
    public async Task Buscar_SeCombinaConElFiltro_YLimpiarLaDevuelveEntera()
    {
        var sut = await AbrirMusicaAsync();
        sut.FiltroTemasMusicales = "Endings";
        sut.BusquedaTemas = "e"; // "We Are!", "Believe", "Memories"... pero solo los endings

        Visibles(sut).Should().Equal("ED1", "ED2");

        sut.LimpiarBusquedaTemasCommand.Execute(null);
        sut.FiltroTemasMusicales = "Todos";
        Visibles(sut).Should().Equal("OP1", "OP2", "ED1", "ED2");
        sut.HayBusquedaTemas.Should().BeFalse();
    }

    [Fact]
    public async Task Buscar_UnTemaTapadoPorSpoiler_SoloSeEncuentraPorSuEtiqueta()
    {
        var sut = await AbrirMusicaAsync();
        sut.TemasMusicales[2].OcultoPorSpoiler = true; // ED1 "Memories"

        sut.BusquedaTemas = "memories";
        Visibles(sut).Should().BeEmpty("buscar por el título destaparía el spoiler");

        sut.BusquedaTemas = "ed1";
        Visibles(sut).Should().Equal("ED1");
    }

    [Fact]
    public async Task Buscador_SoloSeOfreceConListasLargas()
    {
        var sut = await AbrirMusicaAsync();
        sut.MostrarBuscadorTemas.Should().BeFalse("4 temas se ven enteros");

        _themes.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, MusicaFichaViewModel.MinimoTemasParaBuscar).Select(n => Tema($"OP{n}", $"Tema {n}", "")).ToList());
        await sut.CargarTemasMusicalesAsync();

        sut.MostrarBuscadorTemas.Should().BeTrue();
    }

    [Fact]
    public async Task Buscar_AlAbrirOtroAnime_LaBusquedaNoSeHereda()
    {
        var sut = await AbrirMusicaAsync();
        sut.BusquedaTemas = "believe";

        sut.ReiniciarMusica();

        sut.BusquedaTemas.Should().BeEmpty();
    }

    // ── Contador y filtro de guardados ──

    [Fact]
    public async Task Contador_CuentaLosGuardados_YSeActualizaAlDescargarYAlEliminar()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.TemasGuardados.Should().Be(2);
        sut.TotalTemas.Should().Be(4);

        _descargas.Setup(d => d.DescargarYConvertirAsync(7, _op2, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(RutaGuardada(_op2));
        await sut.DescargarTemaCommand.ExecuteAsync(sut.TemasMusicales[1]);
        sut.TemasGuardados.Should().Be(3);

        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        await sut.EliminarTemaCommand.ExecuteAsync(sut.TemasMusicales[0]);
        sut.TemasGuardados.Should().Be(2);
    }

    [Fact]
    public async Task FiltroGuardados_SoloMuestraLosQueEstanEnElDisco()
    {
        Descargados(_op2, _ed2);
        var sut = await AbrirMusicaAsync();

        sut.CambiarFiltroTemasCommand.Execute("Guardados");

        Visibles(sut).Should().Equal("OP2", "ED2");
        sut.EsFiltroTemasGuardados.Should().BeTrue();
    }

    // ── Sonando ahora ──

    [Fact]
    public async Task TemaActual_EsElQueEstaCargado_YDesapareceAlDetener()
    {
        Descargados(_op1, _op2);
        var sut = await AbrirMusicaAsync();
        sut.HayTemaActual.Should().BeFalse();

        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[1]);
        sut.TemaActual.Should().BeSameAs(sut.TemasMusicales[1]);
        sut.MusicaSonando.Should().BeTrue();

        sut.AlternarTemaActualCommand.Execute(null); // pausa: sigue siendo el tema de la barra
        sut.TemaActual.Should().BeSameAs(sut.TemasMusicales[1]);
        sut.MusicaSonando.Should().BeFalse();

        sut.DetenerMusica();
        sut.HayTemaActual.Should().BeFalse();
    }

    [Fact]
    public async Task Siguiente_SaltaLosQueNoTienenArchivo_YAlFinalVuelveAlPrimero()
    {
        Descargados(_op1, _ed1); // OP2 y ED2 sin archivo
        var sut = await AbrirMusicaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);

        sut.TemaSiguienteCommand.Execute(null);
        sut.TemaActual!.Slug.Should().Be("ED1");

        sut.TemaSiguienteCommand.Execute(null);
        sut.TemaActual!.Slug.Should().Be("OP1", "el botón da la vuelta a la lista");
    }

    [Fact]
    public async Task Siguiente_NoDependeDelFiltroNiDeLaBusqueda()
    {
        Descargados(_op1, _op2, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.FiltroTemasMusicales = "Endings";
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);

        sut.TemaSiguienteCommand.Execute(null);

        sut.TemaActual!.Slug.Should().Be("OP2", "el filtro es para mirar la lista, no cambia qué suena");
    }

    [Fact]
    public async Task Anterior_AlPrincipioDelTema_PasaAlAnteriorEscuchable()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[2]);

        sut.TemaAnteriorCommand.Execute(null);

        sut.TemaActual!.Slug.Should().Be("OP1");
    }

    [Fact]
    public async Task Anterior_ConElTemaAvanzado_VuelveASuPrincipioSinCambiarDeTema()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[2]);
        _player.Posicion = TimeSpan.FromSeconds(40);
        sut.ControlMusica.ActualizarPosicion();

        sut.TemaAnteriorCommand.Execute(null);

        sut.TemaActual!.Slug.Should().Be("ED1");
        _player.Posicion.Should().Be(TimeSpan.Zero);
        _player.VecesQue("Abrir").Should().Be(1);
    }

    [Fact]
    public async Task Anterior_EnElPrimerTema_SoloVuelveAlPrincipio()
    {
        Descargados(_op1, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);

        sut.TemaAnteriorCommand.Execute(null);

        sut.TemaActual!.Slug.Should().Be("OP1");
        _player.VecesQue("Abrir").Should().Be(1);
    }

    [Fact]
    public async Task MostrarTemaActual_QuitaElFiltroQueLoTapa_YPideALaVentanaQueLoMuestre()
    {
        Descargados(_op1);
        var sut = await AbrirMusicaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);
        sut.FiltroTemasMusicales = "Endings";
        sut.BusquedaTemas = "memories";
        TemaAnimeItem? pedido = null;
        sut.TemaActualSolicitado += t => pedido = t;

        sut.MostrarTemaActualCommand.Execute(null);

        pedido.Should().BeSameAs(sut.TemasMusicales[0]);
        sut.FiltroTemasMusicales.Should().Be("Todos");
        sut.BusquedaTemas.Should().BeEmpty();
        Visibles(sut).Should().Contain("OP1");
    }

    // ── Repetir y aleatorio ──

    [Fact]
    public async Task Repetir_AlTerminar_VuelveAEmpezarElMismoTema()
    {
        Descargados(_op1, _op2);
        var sut = await AbrirMusicaAsync();
        sut.RepetirMusica = true;
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);

        _player.DispararTerminado();

        sut.TemaActual!.Slug.Should().Be("OP1");
        sut.TemasMusicales[0].Reproduciendo.Should().BeTrue();
        _player.EstaSonando.Should().BeTrue();
        _player.VecesQue("Abrir").Should().Be(1, "es el mismo archivo: no se abre otro");
    }

    [Fact]
    public async Task Aleatorio_ConReproduccionContinua_SuenanTodosUnaVezYSeDetiene()
    {
        Descargados(_op1, _op2, _ed1, _ed2);
        var sut = await AbrirMusicaAsync();
        sut.Azar = new Random(1234);
        sut.AleatorioMusica = true;
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[1]);

        for (int i = 0; i < 6; i++) _player.DispararTerminado();

        Abiertos().Should().HaveCount(4).And.OnlyHaveUniqueItems("no repite hasta que hayan sonado todos, y entonces se detiene");
        Abiertos().First().Should().Be("OP2");
        _player.EstaSonando.Should().BeFalse();
    }

    [Fact]
    public async Task Aleatorio_ElBotonSiguiente_NuncaRepiteElQueSuena_YSigueTrasSonarTodos()
    {
        Descargados(_op1, _op2, _ed1);
        var sut = await AbrirMusicaAsync();
        sut.Azar = new Random(7);
        sut.AleatorioMusica = true;
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[0]);

        for (int i = 0; i < 12; i++)
        {
            var antes = sut.TemaActual;
            sut.TemaSiguienteCommand.Execute(null);
            sut.TemaActual.Should().NotBeNull().And.NotBeSameAs(antes);
        }
    }

    [Fact]
    public async Task Aleatorio_Anterior_VuelveAlQueSonoAntes()
    {
        Descargados(_op1, _op2, _ed1, _ed2);
        var sut = await AbrirMusicaAsync();
        sut.Azar = new Random(99);
        sut.AleatorioMusica = true;
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales[3]);
        sut.TemaSiguienteCommand.Execute(null);
        var segundo = sut.TemaActual;
        sut.TemaSiguienteCommand.Execute(null);

        sut.TemaAnteriorCommand.Execute(null);
        sut.TemaActual.Should().BeSameAs(segundo);

        sut.TemaAnteriorCommand.Execute(null);
        sut.TemaActual!.Slug.Should().Be("ED2");
    }

    [Fact]
    public async Task AleatorioYRepetir_SeGuardanYSeRecuperan()
    {
        var sut = await AbrirMusicaAsync();
        sut.AlternarAleatorioMusicaCommand.Execute(null);
        sut.AlternarRepetirMusicaCommand.Execute(null);

        await EsperarAsync(() => _config.AleatorioMusica && _config.RepetirMusica);

        var otra = await AbrirMusicaAsync();
        otra.AleatorioMusica.Should().BeTrue();
        otra.RepetirMusica.Should().BeTrue();
    }

    // ── Sin conexión ──

    [Fact]
    public async Task SinConexion_SoloSeOfreceLoQueYaEstaEnElDisco()
    {
        Descargados(_op1);
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op2)).Returns(RutaPrevia(_op2)); // vista previa ya lista
        var sut = await AbrirMusicaAsync();

        sut.SinConexion = true;

        var (op1, op2, ed1) = (sut.TemasMusicales[0], sut.TemasMusicales[1], sut.TemasMusicales[2]);
        op1.PuedeReproducir.Should().BeTrue();
        op2.PuedeReproducir.Should().BeTrue();
        op2.PuedeDescargar.Should().BeTrue("guardar una vista previa ya lista es mover un archivo: no necesita internet");
        ed1.PuedePrevisualizar.Should().BeFalse();
        ed1.PuedeDescargar.Should().BeFalse();
        sut.HayConexion.Should().BeFalse();

        sut.SinConexion = false;
        ed1.PuedePrevisualizar.Should().BeTrue();
        ed1.PuedeDescargar.Should().BeTrue();
    }

    [Fact]
    public async Task SinConexion_PedirUnaDescargaOUnaVistaPrevia_AvisaYNoLlamaAInternet()
    {
        var sut = await AbrirMusicaAsync();
        sut.SinConexion = true;
        var tema = sut.TemasMusicales[0];

        await sut.PrevisualizarTemaCommand.ExecuteAsync(tema);
        await sut.DescargarTemaCommand.ExecuteAsync(tema);
        await sut.DescargarTodosCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_MusicaSinConexionTitulo"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task SinConexion_LosTemasQueSeCarganDespues_TambienLoSaben()
    {
        var sut = await AbrirMusicaAsync();
        sut.SinConexion = true;

        await sut.CargarTemasMusicalesAsync();

        sut.TemasMusicales.Should().OnlyContain(t => t.SinConexion && !t.PuedePrevisualizar);
    }

    [Fact]
    public async Task Fila_SinArtista_NoReservaSuLinea()
    {
        var sut = await AbrirMusicaAsync();

        sut.TemasMusicales[0].TieneArtistasVisible.Should().BeTrue();
        sut.TemasMusicales[3].TieneArtistasVisible.Should().BeFalse("AnimeThemes no da el artista de muchos temas");
    }
}
