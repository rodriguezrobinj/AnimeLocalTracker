#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Rediseño de Actualizaciones: resumen (contadores), filtros con chips, acción principal de la tarjeta,
/// "Descargar pendientes", "Marcar todo como visto" y actualización en vivo.
/// </summary>
public class ActualizacionesResumenYFiltrosTests
{
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDownloadService> _downloadMock = new();
    private readonly Mock<IFileScannerService> _fileScannerMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();

    /// <summary>
    /// Cuatro episodios nuevos, uno por estado:
    /// 11 = sin descargar · 12 = listo para ver · 13 = en progreso (5:00 de 23:20) · 14 = visto.
    /// </summary>
    private async Task<ActualizacionesViewModel> CargarConLosCuatroEstadosAsync(bool conDialogo = true)
    {
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(Enumerable.Range(1, 4)
            .Select(i => new AnimeItem { AniListId = i, Titulo = $"Anime {i}", Estado = "RELEASING", RutaCarpeta = $@"C:\Anime\A{i}" })
            .ToList());

        _trackingMock.Setup(t => t.ObtenerCalendarioEmisionAsync(It.IsAny<List<int>>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync((true, Enumerable.Range(1, 4)
                .Select(i => new AiringEpisode { AniListId = i, NumeroEpisodio = 10 + i, FechaEmision = DateTime.UtcNow.AddHours(-i) })
                .ToList()));

        // El anime 1 no tiene archivo; los demás sí
        _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        foreach (int i in new[] { 2, 3, 4 })
        {
            _fileScannerMock.Setup(f => f.EscanearEpisodiosAsync($@"C:\Anime\A{i}"))
                .ReturnsAsync(new List<EpisodioItem> { new() { NumeroEpisodio = 10 + i, RutaCompleta = $@"C:\Anime\A{i}\ep.mkv" } });
        }

        _dbMock.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 3, NumeroEpisodio = 13, ProgresoSegundos = 300, TotalSegundos = 1400, FavoritoLocal = true },
            new() { AniListId = 4, NumeroEpisodio = 14, VistoLocal = true }
        });

        var sut = new ActualizacionesViewModel(_dbMock.Object, _trackingMock.Object, _downloadMock.Object, _fileScannerMock.Object,
            dialogService: conDialogo ? _dialogMock.Object : null);
        await sut.CargarActualizacionesAsync();
        return sut;
    }

    private static List<ActualizacionItemViewModel> Visibles(ActualizacionesViewModel sut) =>
        sut.ItemsAgrupados.OfType<ActualizacionItemViewModel>().ToList();

    private static ActualizacionItemViewModel Episodio(ActualizacionesViewModel sut, int numero) =>
        sut.Items.Single(i => i.NumeroEpisodio == numero);

    // ───────────── Resumen ─────────────

    [Fact]
    public async Task Resumen_CuentaNuevosPorDescargarListosYSinVer()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.TotalNuevos.Should().Be(4);
        sut.TotalPorDescargar.Should().Be(1);
        sut.TotalListos.Should().Be(2, "el listo para ver y el que está a medias");
        sut.TotalSinVer.Should().Be(3);
        sut.PendientesEncolables.Should().Be(1);
        sut.PuedeDescargarPendientes.Should().BeTrue();
        sut.PuedeMarcarVistos.Should().BeTrue();
    }

    [Fact]
    public async Task LosTextosDeLosBotonesLlevanLaCantidad()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.DescargarPendientesTexto.Should().Be(string.Format(LocalizationService.T("Act_DescargarPendientesFormato"), 1));
        sut.MarcarVistosTexto.Should().Be(string.Format(LocalizationService.T("Act_MarcarVistosFormato"), 3));
    }

    [Fact]
    public async Task SinEpisodios_LosContadoresSonCeroYLosBotonesEstanDeshabilitados()
    {
        _dbMock.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>());
        var sut = new ActualizacionesViewModel(_dbMock.Object, _trackingMock.Object, _downloadMock.Object, _fileScannerMock.Object);

        await sut.CargarActualizacionesAsync();

        sut.TotalNuevos.Should().Be(0);
        sut.PuedeDescargarPendientes.Should().BeFalse();
        sut.PuedeMarcarVistos.Should().BeFalse();
        sut.EstaVacio.Should().BeTrue();
        sut.SinResultados.Should().BeFalse("no hay episodios: es el estado vacío, no un filtro sin coincidencias");
    }

    // ───────────── Filtros ─────────────

    [Fact]
    public async Task Filtros_SonCuatroChips_ConSuContador_YTodosActivoAlPrincipio()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.Filtros.Select(f => f.Clave).Should().Equal(
            ActualizacionesViewModel.FiltroTodos, ActualizacionesViewModel.FiltroPorDescargar,
            ActualizacionesViewModel.FiltroListos, ActualizacionesViewModel.FiltroSinVer);
        sut.Filtros.Single(f => f.EsActivo).Clave.Should().Be(ActualizacionesViewModel.FiltroTodos);
        sut.Filtros[0].Etiqueta.Should().Contain("4");
        sut.Filtros[1].Etiqueta.Should().Contain("1");
        sut.Filtros[2].Etiqueta.Should().Contain("2");
        sut.Filtros[3].Etiqueta.Should().Contain("3");
    }

    [Theory]
    [InlineData(ActualizacionesViewModel.FiltroTodos, new[] { 11, 12, 13, 14 })]
    [InlineData(ActualizacionesViewModel.FiltroPorDescargar, new[] { 11 })]
    [InlineData(ActualizacionesViewModel.FiltroListos, new[] { 12, 13 })]
    [InlineData(ActualizacionesViewModel.FiltroSinVer, new[] { 11, 12, 13 })]
    public async Task CadaFiltroMuestraSoloLosEpisodiosQueCumplen(string filtro, int[] esperados)
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.SeleccionarFiltroCommand.Execute(filtro);

        Visibles(sut).Select(i => i.NumeroEpisodio).Should().BeEquivalentTo(esperados);
        sut.Filtros.Single(f => f.EsActivo).Clave.Should().Be(filtro);
    }

    [Fact]
    public async Task ElFiltroConservaLasCabecerasDeFecha()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.SeleccionarFiltroCommand.Execute(ActualizacionesViewModel.FiltroListos);

        sut.ItemsAgrupados.OfType<string>().Should().NotBeEmpty("cada grupo de fecha conserva su cabecera");
    }

    [Fact]
    public async Task SiElFiltroNoTieneCoincidencias_MuestraSinResultadosYNoElVacio()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        // Descargar lo único pendiente: el filtro "Por descargar" se queda sin nada
        Episodio(sut, 11).Descargado = true;
        sut.Receive(new EpisodioActualizadoMensaje(1, 11, false, 0, 0)); // fuerza el recálculo de contadores y lista

        sut.SeleccionarFiltroCommand.Execute(ActualizacionesViewModel.FiltroPorDescargar);

        sut.TieneResultados.Should().BeFalse();
        sut.SinResultados.Should().BeTrue();
        sut.EstaVacio.Should().BeFalse();
    }

    [Fact]
    public async Task ElFiltroElegidoSeConservaAlRecargar()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        sut.SeleccionarFiltroCommand.Execute(ActualizacionesViewModel.FiltroPorDescargar);

        await sut.CargarActualizacionesAsync();

        Visibles(sut).Select(i => i.NumeroEpisodio).Should().Equal(11);
        sut.Filtros.Single(f => f.EsActivo).Clave.Should().Be(ActualizacionesViewModel.FiltroPorDescargar);
    }

    // ───────────── Acción principal de la tarjeta ─────────────

    [Fact]
    public async Task AccionPrincipal_SinArchivo_Descarga()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        await sut.EjecutarAccionCommand.ExecuteAsync(Episodio(sut, 11));

        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(1, "Anime 1", @"C:\Anime\A1", 11, null), Times.Once);
    }

    [Fact]
    public async Task AccionPrincipal_ConArchivo_ReproduceEnLugarDeDescargar()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        string ruta = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".mkv");
        System.IO.File.WriteAllText(ruta, "x");
        var item = Episodio(sut, 12);
        item.RutaArchivo = ruta;
        NavegarMensaje_Reproductor? recibido = null;
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(this, (_, m) => recibido = m);

        try
        {
            await sut.EjecutarAccionCommand.ExecuteAsync(item);

            recibido.Should().NotBeNull();
            recibido!.Episodio.Should().Be(12);
            _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            try { System.IO.File.Delete(ruta); } catch { /* ignore */ }
        }
    }

    // ───────────── Descargar pendientes ─────────────

    [Fact]
    public async Task DescargarPendientes_ConfirmadoEncolaSoloLoPendiente_YAvisa()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await sut.DescargarPendientesCommand.ExecuteAsync(null);

        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(1, "Anime 1", @"C:\Anime\A1", 11, null), Times.Once);
        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsIn(2, 3, 4), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
        _dialogMock.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        sut.PendientesEncolables.Should().Be(0, "ya quedó en cola");
        sut.PuedeDescargarPendientes.Should().BeFalse();
    }

    [Fact]
    public async Task DescargarPendientes_PideConfirmacionConLaCantidad()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        await sut.DescargarPendientesCommand.ExecuteAsync(null);

        _dialogMock.Verify(d => d.MostrarDialogoAsync(
            It.IsAny<string>(), It.Is<string>(m => m.Contains('1')), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DescargarPendientes_SiElUsuarioCancela_NoEncolaNada()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        await sut.DescargarPendientesCommand.ExecuteAsync(null);

        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
        sut.PendientesEncolables.Should().Be(1);
    }

    [Fact]
    public async Task DescargarPendientes_NoReencolaLosQueYaSeEstanDescargando()
    {
        var sut = await CargarConLosCuatroEstadosAsync(conDialogo: false);
        Episodio(sut, 11).IsDownloading = true;

        await sut.DescargarPendientesCommand.ExecuteAsync(null);

        _downloadMock.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    // ───────────── Marcar todo como visto ─────────────

    [Fact]
    public async Task MarcarTodoVisto_GuardaSoloLosNoVistos_ConservandoFavoritoYDuracion_SinFabricarFecha()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        List<RegistroEpisodio>? guardados = null;
        _dbMock.Setup(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>()))
            .Callback<IEnumerable<RegistroEpisodio>>(r => guardados = r.ToList())
            .Returns(Task.CompletedTask);

        await sut.MarcarTodoVistoCommand.ExecuteAsync(null);

        guardados.Should().NotBeNull();
        guardados!.Select(r => r.NumeroEpisodio).Should().BeEquivalentTo(new[] { 11, 12, 13 }, "el 14 ya estaba visto");
        guardados.Should().OnlyContain(r => r.VistoLocal && r.ProgresoSegundos == 0 && r.UltimaReproduccion == null);

        var enProgreso = guardados.Single(r => r.NumeroEpisodio == 13);
        enProgreso.FavoritoLocal.Should().BeTrue("no se pierde el favorito que ya tenía");
        enProgreso.TotalSegundos.Should().Be(1400, "no se pierde la duración conocida");
    }

    [Fact]
    public async Task MarcarTodoVisto_ActualizaLaListaYLosContadores()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await sut.MarcarTodoVistoCommand.ExecuteAsync(null);

        sut.Items.Should().OnlyContain(i => i.Visto);
        sut.TotalSinVer.Should().Be(0);
        sut.TotalListos.Should().Be(0);
        sut.PuedeMarcarVistos.Should().BeFalse();
        Episodio(sut, 13).ProgresoSegundos.Should().Be(0);
    }

    [Fact]
    public async Task MarcarTodoVisto_SiElUsuarioCancela_NoTocaNada()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        _dialogMock.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        await sut.MarcarTodoVistoCommand.ExecuteAsync(null);

        _dbMock.Verify(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>()), Times.Never);
        sut.TotalSinVer.Should().Be(3);
    }

    [Fact]
    public async Task MarcarTodoVisto_SiFallaLaBaseDeDatos_NoDejaLaListaMarcada()
    {
        var sut = await CargarConLosCuatroEstadosAsync(conDialogo: false);
        _dbMock.Setup(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>())).ThrowsAsync(new InvalidOperationException("BD bloqueada"));

        await sut.MarcarTodoVistoCommand.ExecuteAsync(null);

        sut.TotalSinVer.Should().Be(3, "si no se pudo guardar, la lista no debe mostrarse como vista");
    }

    // ───────────── Actualización en vivo ─────────────

    [Fact]
    public async Task ElReproductorMarcaVisto_LaTarjetaYLosContadoresSeActualizan()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.Receive(new EpisodioActualizadoMensaje(2, 12, true, 0, 1400));

        Episodio(sut, 12).Estado.Should().Be(EstadoActualizacion.Visto);
        sut.TotalSinVer.Should().Be(2);
        sut.TotalListos.Should().Be(1);
    }

    [Fact]
    public async Task ElReproductorGuardaProgreso_LaTarjetaPasaAEnProgreso()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        sut.Receive(new EpisodioActualizadoMensaje(2, 12, false, 600, 1400));

        var item = Episodio(sut, 12);
        item.Estado.Should().Be(EstadoActualizacion.EnProgreso);
        item.ProgresoSegundos.Should().Be(600);
        item.TotalSegundos.Should().Be(1400);
    }

    [Fact]
    public async Task UnMensajeDeUnEpisodioQueNoEstaEnLaLista_SeIgnora()
    {
        var sut = await CargarConLosCuatroEstadosAsync();

        var act = () => sut.Receive(new EpisodioActualizadoMensaje(99, 1, true, 0, 0));

        act.Should().NotThrow();
        sut.TotalSinVer.Should().Be(3);
    }

    [Fact]
    public async Task UnCambioEnBloque_ForzaRecargarLaProximaVez()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        sut.NecesitaRecargar().Should().BeFalse("acaba de cargarse");

        sut.Receive(new EpisodioActualizadoMensaje(2, 0, false, 0, 0));

        sut.NecesitaRecargar().Should().BeTrue();
    }

    [Fact]
    public async Task UnaDescargaQueTermina_ActualizaLosContadores()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        Episodio(sut, 11).IsDownloading = true;

        sut.Receive(new DescargaProgresoMensaje(1, 11, 100, isDownloading: false, isCompleted: true, isPaused: false, @"C:\Anime\A1\ep.mkv", null, "Anime 1"));

        Episodio(sut, 11).Estado.Should().Be(EstadoActualizacion.ListoParaVer);
        sut.TotalPorDescargar.Should().Be(0);
        sut.TotalListos.Should().Be(3);
    }

    [Fact]
    public async Task AlEmpezarUnaDescarga_ElBotonDePendientesSeActualiza()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        sut.PendientesEncolables.Should().Be(1);

        sut.Receive(new DescargaProgresoMensaje(1, 11, 5, isDownloading: true, isCompleted: false, isPaused: false, string.Empty, null, "Anime 1"));

        sut.PendientesEncolables.Should().Be(0, "ese episodio ya está descargándose");
    }

    // ───────────── Idioma ─────────────

    [Fact]
    public async Task AlCambiarDeIdioma_SeRehacenLosTextosSinPerderLaLista()
    {
        var sut = await CargarConLosCuatroEstadosAsync();
        var item = Episodio(sut, 12);
        var cambios = new List<string?>();
        item.PropertyChanged += (_, e) => cambios.Add(e.PropertyName);

        sut.Receive(new IdiomaCambiadoMensaje());

        cambios.Should().Contain(string.Empty);
        sut.Items.Should().HaveCount(4);
        sut.Filtros.Should().HaveCount(4);
    }
}
