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

/// <summary>Sección de música de la ficha: escuchar antes de descargar, guardar la vista previa, reproducir con barra y volumen.
/// El audio y las descargas se simulan (no hay sonido real ni red en las pruebas).</summary>
public class DetalleMusicaVistaPreviaTests : IDisposable
{
    private const string RutaPrevia = @"C:\Previews\7\OP_OP1_v1_eptodos.mp3";
    private const string RutaGuardada = @"C:\Music\7\OP_OP1_v1_eptodos.mp3";

    private readonly Mock<IAnimeThemesService> _themes = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDownloadService> _downloadService = new();
    private readonly FakeAudioTrackPlayer _player = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private readonly AnimeThemeInfo _op = new() { Slug = "OP1", Tipo = "OP", TituloCancion = "Yuusha", Artistas = "YOASOBI", AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };

    public DetalleMusicaVistaPreviaTests()
    {
        _themes.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { _op });
        _descargas.Setup(d => d.EstaDescargado(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns(false);
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>())).Returns((string?)null);
        _descargas.Setup(d => d.ObtenerRutaLocalEsperada(7, _op)).Returns(RutaGuardada);
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(RutaPrevia);
    }

    private async Task<DetalleViewModel> AbrirFichaAsync(int aniListId = 7)
    {
        _escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _downloadService.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(
            _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _downloadService.Object,
            animeThemesService: _themes.Object, animeThemesDownload: _descargas.Object, audioTrackPlayer: _player);
        await sut.InicializarAsync(new AnimeItem { AniListId = aniListId, Titulo = "Frieren" });
        await sut.CargarTemasMusicalesAsync();
        return sut;
    }

    private static async Task EsperarAsync(Func<bool> condicion, int milisegundos = 3000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milisegundos);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(10);
        condicion().Should().BeTrue("la condición esperada no se cumplió a tiempo");
    }

    // === Carga de la lista ===

    [Fact]
    public async Task Cargar_UnTemaConVistaPreviaYaPreparada_DeberiaPoderEscucharseSinBajarloOtraVez()
    {
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op)).Returns(RutaPrevia);

        var sut = await AbrirFichaAsync();

        var item = sut.TemasMusicales.Single();
        item.VistaPreviaLista.Should().BeTrue();
        item.Descargado.Should().BeFalse();
        item.PuedeReproducir.Should().BeTrue();
    }

    [Fact]
    public async Task Cargar_UnTemaYaDescargado_NoDeberiaMarcarseComoVistaPrevia()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op)).Returns(RutaPrevia);

        var sut = await AbrirFichaAsync();

        var item = sut.TemasMusicales.Single();
        item.Descargado.Should().BeTrue();
        item.VistaPreviaLista.Should().BeFalse();
    }

    // === Escuchar antes de descargar ===

    [Fact]
    public async Task Previsualizar_DeberiaPrepararLaVistaPreviaYEmpezarASonar()
    {
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.PrevisualizarTemaCommand.ExecuteAsync(item);

        item.VistaPreviaLista.Should().BeTrue();
        item.PreparandoVistaPrevia.Should().BeFalse();
        item.Descargado.Should().BeFalse("escuchar no guarda nada en la carpeta de música");
        item.Reproduciendo.Should().BeTrue();
        item.EsPistaActual.Should().BeTrue();
        _player.RutaAbierta.Should().Be(RutaPrevia);
        _player.EstaSonando.Should().BeTrue();
        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Previsualizar_MientrasSePrepara_DeberiaMostrarElAvanceYNoPermitirOtraVez()
    {
        var espera = new TaskCompletionSource<string?>();
        IProgress<double>? progresoRecibido = null;
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, AnimeThemeInfo, IProgress<double>?, CancellationToken>((_, _, p, _) => { progresoRecibido = p; return espera.Task; });
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        var primera = sut.PrevisualizarTemaCommand.ExecuteAsync(item);
        item.PreparandoVistaPrevia.Should().BeTrue();
        item.PuedePrevisualizar.Should().BeFalse();

        progresoRecibido.Should().NotBeNull();
        progresoRecibido!.Report(0.5);
        await EsperarAsync(() => item.ProgresoPreparacion == 0.5);
        item.ProgresoPreparacionPorcentaje.Should().Be(50);

        await sut.PrevisualizarTemaCommand.ExecuteAsync(item); // segundo clic mientras prepara: se ignora
        _descargas.Verify(d => d.PrepararVistaPreviaAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Once);

        espera.SetResult(RutaPrevia);
        await primera;
        item.PreparandoVistaPrevia.Should().BeFalse();
    }

    [Fact]
    public async Task Previsualizar_SiFalla_DeberiaAvisarYNoDejarlaMarcadaComoLista()
    {
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.PrevisualizarTemaCommand.ExecuteAsync(item);

        item.VistaPreviaLista.Should().BeFalse();
        item.PreparandoVistaPrevia.Should().BeFalse();
        item.PuedePrevisualizar.Should().BeTrue("se puede volver a intentar");
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _player.VecesQue("Abrir").Should().Be(0);
    }

    [Fact]
    public async Task Previsualizar_ConLaVistaPreviaYaLista_SoloReproduceSinVolverAPrepararla()
    {
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op)).Returns(RutaPrevia);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.PrevisualizarTemaCommand.ExecuteAsync(item);

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _player.RutaAbierta.Should().Be(RutaPrevia);
    }

    [Fact]
    public async Task Previsualizar_UnTemaYaDescargado_SeIgnora()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.PrevisualizarTemaCommand.ExecuteAsync(item);

        _descargas.Verify(d => d.PrepararVistaPreviaAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Previsualizar_SiSeCambiaDeFichaMientrasSePrepara_NoDeberiaArrancarSonido()
    {
        var espera = new TaskCompletionSource<string?>();
        _descargas.Setup(d => d.PrepararVistaPreviaAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns(espera.Task);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        var preparando = sut.PrevisualizarTemaCommand.ExecuteAsync(item);

        _themes.Setup(s => s.ObtenerTemasAsync(8, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        await sut.InicializarAsync(new AnimeItem { AniListId = 8, Titulo = "Otro anime" });
        espera.SetResult(RutaPrevia);
        await preparando;

        _player.VecesQue("Abrir").Should().Be(0, "no debe sonar la música de un anime que ya no está en pantalla");
    }

    // === Guardar (descargar) ===

    [Fact]
    public async Task Guardar_ConVistaPrevia_DeberiaMoverElArchivoSinVolverADescargar()
    {
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op)).Returns(RutaPrevia);
        _descargas.Setup(d => d.GuardarVistaPrevia(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.DescargarTemaCommand.ExecuteAsync(item);

        item.Descargado.Should().BeTrue();
        item.VistaPreviaLista.Should().BeFalse();
        item.EsSoloVistaPrevia.Should().BeFalse();
        _descargas.Verify(d => d.GuardarVistaPrevia(7, _op), Times.Once);
        _descargas.Verify(d => d.DescargarYConvertirAsync(It.IsAny<int>(), It.IsAny<AnimeThemeInfo>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Guardar_MientrasSuenaLaVistaPrevia_NoDeberiaCortarLaEscuchaYSeguirEnElMismoPunto()
    {
        _descargas.Setup(d => d.GuardarVistaPrevia(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        await sut.PrevisualizarTemaCommand.ExecuteAsync(item);
        _player.Posicion = TimeSpan.FromSeconds(33);
        _player.Llamadas.Clear();

        await sut.DescargarTemaCommand.ExecuteAsync(item);

        _player.Llamadas.Should().ContainInOrder("Cerrar", $"Abrir:{RutaGuardada}", "Reproducir");
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(33));
        item.Reproduciendo.Should().BeTrue();
        item.Descargado.Should().BeTrue();
    }

    [Fact]
    public async Task Guardar_SiNoSePuedeMoverLaVistaPrevia_DeberiaDescargarNormalmente()
    {
        _descargas.Setup(d => d.ObtenerRutaVistaPrevia(7, _op)).Returns(RutaPrevia);
        _descargas.Setup(d => d.GuardarVistaPrevia(7, _op)).Returns(false);
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(RutaGuardada);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        await sut.DescargarTemaCommand.ExecuteAsync(item);

        item.Descargado.Should().BeTrue();
        _descargas.Verify(d => d.DescargarYConvertirAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Descargar_SinVistaPrevia_DeberiaMostrarElAvanceDeLaDescarga()
    {
        var espera = new TaskCompletionSource<string?>();
        IProgress<double>? progreso = null;
        _descargas.Setup(d => d.DescargarYConvertirAsync(7, _op, It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, AnimeThemeInfo, IProgress<double>?, CancellationToken>((_, _, p, _) => { progreso = p; return espera.Task; });
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        var descarga = sut.DescargarTemaCommand.ExecuteAsync(item);
        item.Descargando.Should().BeTrue();
        progreso!.Report(0.25);
        await EsperarAsync(() => item.ProgresoDescarga == 0.25);
        item.ProgresoDescargaPorcentaje.Should().Be(25);

        espera.SetResult(RutaGuardada);
        await descarga;
        item.Descargado.Should().BeTrue();
        item.Descargando.Should().BeFalse();
    }

    // === Reproducir con barra ===

    [Fact]
    public async Task Reproducir_UnTemaDescargado_DeberiaUsarLaRutaGuardada()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();

        sut.ReproducirTemaCommand.Execute(item);

        _player.RutaAbierta.Should().Be(RutaGuardada);
        item.Reproduciendo.Should().BeTrue();
    }

    [Fact]
    public async Task Reproducir_PausarYReanudar_DeberiaContinuarDondeSeDejoSinReabrirElArchivo()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        sut.ReproducirTemaCommand.Execute(item);
        _player.Posicion = TimeSpan.FromSeconds(50);

        sut.ReproducirTemaCommand.Execute(item); // pausa
        item.Reproduciendo.Should().BeFalse();
        sut.ReproducirTemaCommand.Execute(item); // reanuda

        item.Reproduciendo.Should().BeTrue();
        _player.VecesQue("Abrir").Should().Be(1);
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public async Task Reproducir_UnTemaSinArchivoNiVistaPrevia_NoHaceNada()
    {
        var sut = await AbrirFichaAsync();

        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales.Single());

        _player.VecesQue("Abrir").Should().Be(0);
    }

    [Fact]
    public async Task BuscarTema_DeberiaSaltarEnLaPistaCargada()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        sut.ReproducirTemaCommand.Execute(item);

        sut.BuscarTema(item, 61);

        _player.Posicion.Should().Be(TimeSpan.FromSeconds(61));
        item.PosicionTexto.Should().Be("1:01");
    }

    [Fact]
    public async Task BuscarTema_SinNadaCargado_NoDeberiaFallar()
    {
        var sut = await AbrirFichaAsync();

        var act = () => sut.BuscarTema(sut.TemasMusicales.Single(), 10);

        act.Should().NotThrow();
    }

    // === Eliminar, cambiar de ficha, salir ===

    [Fact]
    public async Task Eliminar_ElTemaQueSuena_DeberiaSoltarElArchivoAntesDeBorrarlo()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var ordenados = new List<string>();
        _descargas.Setup(d => d.Eliminar(7, _op)).Callback(() => ordenados.Add($"Eliminar(cerrado={_player.EstaCerrado})"));
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        sut.ReproducirTemaCommand.Execute(item);

        sut.EliminarTemaCommand.Execute(item);

        ordenados.Should().ContainSingle().Which.Should().Be("Eliminar(cerrado=True)");
        item.Descargado.Should().BeFalse();
        item.EsPistaActual.Should().BeFalse();
    }

    [Fact]
    public async Task CambiarDeFicha_DeberiaCortarLaMusicaYLimpiarLaLista()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales.Single());
        _themes.Setup(s => s.ObtenerTemasAsync(8, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());

        await sut.InicializarAsync(new AnimeItem { AniListId = 8, Titulo = "Otro" });

        _player.EstaCerrado.Should().BeTrue();
        _player.EstaSonando.Should().BeFalse();
        sut.TemasMusicales.Should().BeEmpty();
    }

    [Fact]
    public async Task DetenerMusica_AlSalirDeLaFicha_DeberiaCortarElSonidoSinVaciarLaLista()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        var item = sut.TemasMusicales.Single();
        sut.ReproducirTemaCommand.Execute(item);

        sut.DetenerMusica();

        _player.EstaSonando.Should().BeFalse();
        item.Reproduciendo.Should().BeFalse();
        sut.TemasMusicales.Should().HaveCount(1);
    }

    [Fact]
    public async Task DetenerMusica_SinHaberReproducidoNada_NoDeberiaFallar()
    {
        var sut = await AbrirFichaAsync();

        var act = () => sut.DetenerMusica();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_DeberiaLiberarElReproductor()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        var sut = await AbrirFichaAsync();
        sut.ReproducirTemaCommand.Execute(sut.TemasMusicales.Single());

        sut.Dispose();

        _player.Disposed.Should().BeTrue();
    }

    // === Volumen ===

    [Fact]
    public async Task Volumen_DeberiaAplicarseAlReproductorYRecordarseEnLaSesion()
    {
        _descargas.Setup(d => d.EstaDescargado(7, _op)).Returns(true);
        double original = 0.8;
        try
        {
            var sut = await AbrirFichaAsync();
            sut.ReproducirTemaCommand.Execute(sut.TemasMusicales.Single());

            sut.VolumenMusica = 0.35;

            _player.Volumen.Should().Be(0.35);

            var otraFicha = await AbrirFichaAsync();
            otraFicha.VolumenMusica.Should().Be(0.35, "el volumen elegido dura toda la sesión");
        }
        finally
        {
            var restaurar = await AbrirFichaAsync();
            restaurar.VolumenMusica = original;
        }
    }
}
