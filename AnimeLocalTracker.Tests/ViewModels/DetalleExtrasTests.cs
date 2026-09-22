using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ficha del anime: espacio en disco (y liberarlo), etiquetas de AniList y preferencias de avisos / descarga automática.</summary>
public class DetalleExtrasTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IEmisionMonitorService> _monitor = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"ficha_{Guid.NewGuid():N}");

    public DetalleExtrasTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private DetalleViewModel CrearSut(IDatosExtraService? datosExtra = null) => new(
        _tracking.Object, _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object,
        datosExtra: datosExtra, monitorEmision: _monitor.Object);

    /// <summary>Crea archivos reales de N KB y devuelve los episodios correspondientes (los vistos, con registro en la BD).</summary>
    private async Task<DetalleViewModel> FichaConEpisodiosAsync(params (int Ep, int Kb, bool Visto)[] episodios)
    {
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "FINISHED", TotalEpisodios = episodios.Length };
        var items = new List<EpisodioItem>();
        var registros = new List<RegistroEpisodio>();
        foreach (var (ep, kb, visto) in episodios)
        {
            string ruta = Path.Combine(_carpeta, $"Episodio {ep:D2}.mp4");
            await File.WriteAllBytesAsync(ruta, new byte[kb * 1024]);
            items.Add(new EpisodioItem { NumeroEpisodio = ep, RutaCompleta = ruta, Descargado = true, TituloArchivo = $"ep{ep}" });
            if (visto) registros.Add(new RegistroEpisodio { AniListId = 7, NumeroEpisodio = ep, VistoLocal = true });
        }

        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(items);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(registros);
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = CrearSut();
        await sut.InicializarAsync(anime);
        await sut.CalcularEspacioEnDiscoAsync(); // la carga inicial lo lanza en segundo plano; aquí se espera el resultado
        return sut;
    }

    // ── Espacio en disco ──

    [Fact]
    public async Task Espacio_SumaLosArchivosYCalculaLoLiberable()
    {
        var sut = await FichaConEpisodiosAsync((1, 2048, true), (2, 1024, true), (3, 4096, false));

        sut.TieneEspacioEnDisco.Should().BeTrue();
        sut.EspacioEnDiscoTexto.Should().Contain("7"); // 2 + 1 + 4 MB
        sut.HayEspacioLiberable.Should().BeTrue();
        sut.LiberarEspacioDescripcion.Should().Contain("2").And.Contain("3", "2 episodios vistos que suman 3 MB");
    }

    [Fact]
    public async Task Espacio_ConTokenYaCancelado_NoEscaneaNiActualizaLaFicha()
    {
        // Navegación rápida entre fichas: si para cuando le toca correr ya se sabe que esta ficha
        // se abandonó, ni siquiera debe empezar a recorrer el disco.
        var anime = new AnimeItem { AniListId = 7, Titulo = "Frieren", RutaCarpeta = _carpeta, Estado = "FINISHED", TotalEpisodios = 1 };
        string ruta = Path.Combine(_carpeta, "Episodio 01.mp4");
        await File.WriteAllBytesAsync(ruta, new byte[2048]);
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, RutaCompleta = ruta, Descargado = true }
        });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(7)).ReturnsAsync(new List<RegistroEpisodio>());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = CrearSut();
        await sut.InicializarAsync(anime);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await sut.CalcularEspacioEnDiscoAsync(cts.Token);

        sut.TieneEspacioEnDisco.Should().BeFalse("el token ya estaba cancelado antes de recorrer el disco");
        sut.EspacioEnDiscoTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task InicializarAsync_LlamadoDeNuevoSobreLaMismaInstancia_CancelaLasCargasDeFondoAnteriores()
    {
        // DetalleViewModel.ActualizarAnimeActualAsync (refresco desde AniList) vuelve a llamar a
        // InicializarAsync sobre la MISMA instancia; una segunda carga no debe dejar corriendo las
        // tareas de fondo (espacio en disco, próximos episodios…) de la primera.
        var sut = await FichaConEpisodiosAsync((1, 1024, false));

        var campo = typeof(DetalleViewModel).GetField("_ctsCargaFicha", BindingFlags.NonPublic | BindingFlags.Instance);
        var ctsPrimeraCarga = (CancellationTokenSource)campo!.GetValue(sut)!;
        ctsPrimeraCarga.IsCancellationRequested.Should().BeFalse();

        await sut.InicializarAsync(sut.AnimeSeleccionado!);

        ctsPrimeraCarga.IsCancellationRequested.Should().BeTrue("la carga anterior debe cancelarse al empezar una nueva sobre la misma ficha");
    }

    [Fact]
    public async Task Dispose_CancelaLasCargasDeFondoEnCurso()
    {
        var sut = await FichaConEpisodiosAsync((1, 1024, false));
        var campo = typeof(DetalleViewModel).GetField("_ctsCargaFicha", BindingFlags.NonPublic | BindingFlags.Instance);
        var cts = (CancellationTokenSource)campo!.GetValue(sut)!;

        sut.Dispose();

        cts.IsCancellationRequested.Should().BeTrue("al descartar la ficha no debe seguir cargando datos para ella");
    }

    [Fact]
    public async Task Espacio_SinEpisodiosVistos_NoHayNadaQueLiberar()
    {
        var sut = await FichaConEpisodiosAsync((1, 1024, false), (2, 1024, false));

        sut.TieneEspacioEnDisco.Should().BeTrue();
        sut.HayEspacioLiberable.Should().BeFalse();
    }

    [Fact]
    public async Task LiberarEspacio_BorraSoloLosVistos_ConservaElHistorialYActualizaLaFicha()
    {
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = await FichaConEpisodiosAsync((1, 1024, true), (2, 1024, true), (3, 1024, false));

        await sut.LiberarEspacioCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_carpeta, "Episodio 01.mp4")).Should().BeFalse();
        File.Exists(Path.Combine(_carpeta, "Episodio 02.mp4")).Should().BeFalse();
        File.Exists(Path.Combine(_carpeta, "Episodio 03.mp4")).Should().BeTrue("el episodio sin ver no se toca");
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(7, 1), Times.Once);
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(7, 2), Times.Once);
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(7, 3), Times.Never);

        // La lista que ve el usuario debe reflejarlo AL MOMENTO (sin tener que recargar la pestaña).
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 1).Descargado.Should().BeFalse();
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 2).Descargado.Should().BeFalse();
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 3).Descargado.Should().BeTrue();
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 1).RutaCompleta.Should().BeEmpty();
        sut.HayEspacioLiberable.Should().BeFalse();
        sut.EspacioEnDiscoTexto.Should().NotBeEmpty("queda el episodio 3");
    }

    [Fact]
    public async Task LiberarEspacio_SiLaMiniaturaEstaBloqueada_ElEpisodioIgualDejaDeMostrarse()
    {
        // Regresión: la ficha tiene abierta la miniatura y Windows niega borrarla. Antes ese fallo saltaba el episodio
        // (con el video ya borrado) y la lista seguía mostrándolo hasta recargar la pestaña.
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var sut = await FichaConEpisodiosAsync((1, 1024, true), (2, 1024, true));
        string miniatura = AnimeLocalTracker.Services.Python.PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(Path.Combine(_carpeta, "Episodio 01.mp4"));
        Directory.CreateDirectory(Path.GetDirectoryName(miniatura)!);
        await File.WriteAllBytesAsync(miniatura, new byte[16]);
        using var bloqueo = new FileStream(miniatura, FileMode.Open, FileAccess.Read, FileShare.None);

        await sut.LiberarEspacioCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_carpeta, "Episodio 01.mp4")).Should().BeFalse();
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 1).Descargado.Should().BeFalse("el video ya no está: la lista debe reflejarlo");
        sut.EpisodiosDelAnime.Single(e => e.NumeroEpisodio == 2).Descargado.Should().BeFalse();
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(7, 1), Times.Once);
    }

    [Fact]
    public async Task LiberarEspacio_SiElUsuarioCancela_NoBorraNada()
    {
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var sut = await FichaConEpisodiosAsync((1, 1024, true));

        await sut.LiberarEspacioCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(_carpeta, "Episodio 01.mp4")).Should().BeTrue();
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    // ── Etiquetas de AniList ──

    [Fact]
    public void Etiquetas_MuestranNotaFormatoDuracionEstudioYFuente()
    {
        var sut = CrearSut();

        sut.AplicarDatosExtra(new DatosExtraAnime { NotaMedia = 82, Formato = "TV", DuracionMin = 24, Estudio = "Studio Comet", Fuente = "LIGHT_NOVEL", TrailerId = "abc", TrailerSitio = "youtube" });

        sut.EtiquetasAniList.Select(e => e.Texto).Should().Equal("82%", "TV · 24 min", "Studio Comet", LocalizationService.T("Src_LIGHT_NOVEL"));
        sut.TieneTrailer.Should().BeTrue();
    }

    [Fact]
    public void Etiquetas_SoloSeMuestraLoQueAniListSabe()
    {
        var sut = CrearSut();

        sut.AplicarDatosExtra(new DatosExtraAnime { NotaMedia = 0, Formato = "MOVIE", DuracionMin = 0, Estudio = "", Fuente = "" });

        sut.EtiquetasAniList.Should().ContainSingle().Which.Texto.Should().Be(LocalizationService.T("Fmt_MOVIE"));
        sut.TieneTrailer.Should().BeFalse();
    }

    [Fact]
    public void Etiquetas_SinDatos_SeVacian()
    {
        var sut = CrearSut();
        sut.AplicarDatosExtra(new DatosExtraAnime { NotaMedia = 70 });

        sut.AplicarDatosExtra(null);

        sut.EtiquetasAniList.Should().BeEmpty();
    }

    [Fact]
    public void TraducirValor_UnValorDesconocidoSeDejaLegible()
    {
        DetalleViewModel.TraducirValor("Src_", "COSA_RARA").Should().Be("COSA RARA");
        DetalleViewModel.TraducirValor("Src_", "").Should().BeEmpty();
    }

    // ── Preferencias de avisos y descarga automática ──

    private DetalleViewModel FichaEnEmision(string carpeta)
    {
        var sut = CrearSut();
        sut.AnimeSeleccionado = new AnimeItem { AniListId = 7, Titulo = "Frieren", Estado = "RELEASING", RutaCarpeta = carpeta };
        _monitor.Setup(m => m.UltimoEmitido(It.IsAny<ProximaEmision?>(), It.IsAny<AnimeItem>(), It.IsAny<DateTime>())).Returns(11);
        return sut;
    }

    [Fact]
    public async Task ActivarAvisos_FijaElPuntoDePartidaEnElUltimoEpisodioYaEmitido()
    {
        var sut = FichaEnEmision(_carpeta);
        sut.AvisarEpisodioNuevo = true;
        await sut.GuardarPreferenciasEmisionAsync();

        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.Is<PreferenciaEmision>(p => p.AniListId == 7 && p.Avisar && !p.AutoDescargar && p.UltimoAvisado == 11)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ActivarDescargaAutomatica_ConCarpeta_LaGuardaConSuPuntoDePartida()
    {
        var sut = FichaEnEmision(_carpeta);
        sut.DescargarAutomaticamente = true;
        await sut.GuardarPreferenciasEmisionAsync();

        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.Is<PreferenciaEmision>(p => p.AutoDescargar && p.UltimoDescargado == 11)), Times.AtLeastOnce);
        sut.DescargarAutomaticamente.Should().BeTrue();
    }

    [Fact]
    public async Task ActivarDescargaAutomatica_SinCarpeta_SeRevierteYSeExplica()
    {
        var sut = FichaEnEmision(string.Empty);
        sut.DescargarAutomaticamente = true;
        await sut.GuardarPreferenciasEmisionAsync();

        sut.DescargarAutomaticamente.Should().BeFalse("sin carpeta no hay dónde guardar los episodios");
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.Is<PreferenciaEmision>(p => p.AutoDescargar)), Times.Never);
    }

    [Fact]
    public async Task Preferencias_YaActivasNoReiniciaElPuntoDePartida()
    {
        // Si ya estaba activada, volver a guardar (p. ej. al tocar el otro interruptor) no debe mover el contador.
        _db.Setup(d => d.ObtenerPreferenciaEmisionAsync(7)).ReturnsAsync(new PreferenciaEmision { AniListId = 7, Avisar = true, UltimoAvisado = 8 });
        var sut = FichaEnEmision(_carpeta);
        sut.AvisarEpisodioNuevo = true;
        await sut.GuardarPreferenciasEmisionAsync();

        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.Is<PreferenciaEmision>(p => p.Avisar && p.UltimoAvisado == 8)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CargarPreferencias_RefleLasGuardadasEnLosInterruptores()
    {
        _db.Setup(d => d.ObtenerPreferenciaEmisionAsync(7)).ReturnsAsync(new PreferenciaEmision { AniListId = 7, Avisar = true, AutoDescargar = false });
        var sut = FichaEnEmision(_carpeta);

        await sut.CargarPreferenciasEmisionAsync();

        sut.AvisarEpisodioNuevo.Should().BeTrue();
        sut.DescargarAutomaticamente.Should().BeFalse();
        sut.TieneAvisosActivos.Should().BeTrue();
        _db.Verify(d => d.GuardarPreferenciaEmisionAsync(It.IsAny<PreferenciaEmision>()), Times.Never, "cargar no debe volver a guardar");
    }
}
