using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
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
/// Fase 1 de la investigación de la ficha (docs/investigacion-ficha-y-musica.md): el aviso de episodios faltantes, el filtro
/// por clave fija, las confirmaciones de las acciones en bloque y la liberación de la ficha al salir de ella.
/// </summary>
public class DetalleEpisodiosSeguridadTests
{
    private const int Id = 21;

    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();

    /// <summary>Abre una ficha de <paramref name="total"/> episodios con esos archivos en disco y esos episodios ya vistos.</summary>
    private async Task<DetalleViewModel> AbrirFichaAsync(int total, int[] enDisco, int[] vistos, ISettingsService? ajustes = null)
    {
        var anime = new AnimeItem { AniListId = Id, Titulo = "One Piece", TotalEpisodios = total, RutaCarpeta = @"C:\Anime\OP" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta))
            .ReturnsAsync(enDisco.Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = $@"C:\Anime\OP\Episodio {n}.mp4" }).ToList());
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id))
            .ReturnsAsync(vistos.Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, _auth.Object, _escaner.Object, _dialogos.Object, _descargas.Object,
            settingsService: ajustes);
        await sut.InicializarAsync(anime);
        return sut;
    }

    private void ResponderConfirmacion(bool respuesta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(respuesta);

    private void VerificarConfirmaciones(Times veces) =>
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), veces);

    // ── Aviso de episodios faltantes ──

    [Fact]
    public void CalcularFaltantes_SoloCuentaLosNoVistosPorDebajoDelMasAltoEnDisco()
    {
        var episodios = new List<EpisodioItem>
        {
            new() { NumeroEpisodio = 1, Visto = true },                      // visto y liberado: no es un hueco
            new() { NumeroEpisodio = 2 },                                    // sin ver y sin archivo: hueco
            new() { NumeroEpisodio = 3, Descargado = true },
            new() { NumeroEpisodio = 4 },                                    // hueco
            new() { NumeroEpisodio = 5, Descargado = true },
            new() { NumeroEpisodio = 6 },                                    // por encima del más alto en disco: aún no toca
        };

        EpisodiosOrganizador.CalcularFaltantes(episodios).Should().Equal(2, 4);
    }

    [Fact]
    public async Task Faltantes_TodoVistoYSoloElUltimoEnDisco_NoAvisa()
    {
        // El caso real de One Piece: 1180 vistos, solo el último en disco → antes "Faltan 1179 episodios".
        var sut = await AbrirFichaAsync(total: 50, enDisco: [50], vistos: Enumerable.Range(1, 50).ToArray());

        sut.Episodios.HayEpisodiosFaltantes.Should().BeFalse();
        sut.Episodios.EpisodiosFaltantesTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task Faltantes_AlMarcarComoVistoUnHueco_DejaDeContar()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [3], vistos: [1]);
        sut.Episodios.HayEpisodiosFaltantes.Should().BeTrue("el 2 no está en disco ni visto");

        await sut.Episodios.AlternarVistoEpisodioCommand.ExecuteAsync(sut.Episodios.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 2));

        sut.Episodios.HayEpisodiosFaltantes.Should().BeFalse();
    }

    [Fact]
    public async Task DescargarFaltantes_Pocos_NoPregunta()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [4], vistos: []);

        await sut.Episodios.DescargarFaltantesCommand.ExecuteAsync(null);

        VerificarConfirmaciones(Times.Never());
        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 9)]
    public async Task DescargarFaltantes_Muchos_PreguntaAntesDePonerLaColaEnMarcha(bool confirma, int descargasEsperadas)
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: [10], vistos: []);
        ResponderConfirmacion(confirma);

        await sut.Episodios.DescargarFaltantesCommand.ExecuteAsync(null);

        VerificarConfirmaciones(Times.Once());
        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Exactly(descargasEsperadas));
    }

    // ── Filtro por clave fija (no por el texto traducido del desplegable) ──

    [Fact]
    public async Task Filtro_LasOpcionesLlevanClaveFijaYTextoTraducido()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [], vistos: []);

        sut.Episodios.OpcionesFiltro.Select(o => o.Clave).Should().Equal(
            EpisodiosOrganizador.FiltroTodos, EpisodiosOrganizador.FiltroDescargados, EpisodiosOrganizador.FiltroVistos,
            EpisodiosOrganizador.FiltroNoVistos, EpisodiosOrganizador.FiltroFavoritos);
        sut.Episodios.OpcionesFiltro.Single(o => o.Clave == EpisodiosOrganizador.FiltroNoVistos).Texto.Should().Be(LocalizationService.T("Filtro_NoVistos"));
        sut.Episodios.FiltroSeleccionado.Clave.Should().Be(EpisodiosOrganizador.FiltroTodos);
    }

    [Fact]
    public async Task Filtro_AlElegirUnaOpcionDelDesplegable_FiltraPorSuClave()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [4], vistos: [1, 2]);

        sut.Episodios.FiltroSeleccionado = sut.Episodios.OpcionesFiltro.Single(o => o.Clave == EpisodiosOrganizador.FiltroVistos);
        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().BeEquivalentTo([1, 2]);

        sut.Episodios.FiltroSeleccionado = sut.Episodios.OpcionesFiltro.Single(o => o.Clave == EpisodiosOrganizador.FiltroDescargados);
        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().Equal(4);

        sut.Episodios.FiltroEpisodios = EpisodiosOrganizador.FiltroNoVistos;
        sut.Episodios.FiltroSeleccionado.Clave.Should().Be(EpisodiosOrganizador.FiltroNoVistos, "el desplegable sigue a la clave");
        sut.Episodios.EpisodiosDelAnime.Select(e => e.NumeroEpisodio).Should().BeEquivalentTo([3, 4]);
    }

    [Fact]
    public async Task Filtro_SinResultados_ElMensajeNombraLaOpcionConSuTextoTraducido()
    {
        var sut = await AbrirFichaAsync(total: 2, enDisco: [], vistos: []);

        sut.Episodios.FiltroEpisodios = EpisodiosOrganizador.FiltroFavoritos;

        sut.Episodios.EpisodiosDelAnime.Should().BeEmpty();
        sut.Episodios.SubtituloSinEpisodios.Should().Contain(LocalizationService.T("Filtro_Favoritos"));
    }

    // ── Marcar en bloque ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarcarNoVistos_SinSeleccion_PreguntaAntesDeTocarTodaLaLista(bool confirma)
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [], vistos: [1, 2, 3]);
        ResponderConfirmacion(confirma);

        await sut.Episodios.MarcarNoVistosCommand.ExecuteAsync(null);

        VerificarConfirmaciones(Times.Once());
        sut.Episodios.EpisodiosDelAnime.Count(e => e.Visto).Should().Be(confirma ? 0 : 3);
        _db.Verify(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>()), confirma ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task MarcarVistos_SinSeleccion_TambienPregunta()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [], vistos: []);
        ResponderConfirmacion(false);

        await sut.Episodios.MarcarVistosCommand.ExecuteAsync(null);

        VerificarConfirmaciones(Times.Once());
        sut.Episodios.EpisodiosDelAnime.Should().OnlyContain(e => !e.Visto);
    }

    [Fact]
    public async Task MarcarNoVistos_ConSeleccion_NoPregunta()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [], vistos: [1, 2, 3]);
        var seleccion = sut.Episodios.EpisodiosDelAnime.Where(e => e.NumeroEpisodio <= 2).ToList();

        await sut.Episodios.MarcarNoVistosCommand.ExecuteAsync(seleccion);

        VerificarConfirmaciones(Times.Never());
        sut.Episodios.EpisodiosDelAnime.Where(e => e.Visto).Select(e => e.NumeroEpisodio).Should().Equal(3);
    }

    [Fact]
    public async Task MarcarVistos_ConservaLaDuracionConocidaDelEpisodio()
    {
        var sut = await AbrirFichaAsync(total: 2, enDisco: [], vistos: []);
        var episodio = sut.Episodios.EpisodiosDelAnime.First(e => e.NumeroEpisodio == 1);
        episodio.ProgresoSegundos = 600;
        episodio.TotalSegundos = 1420;
        List<RegistroEpisodio>? guardados = null;
        _db.Setup(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>()))
            .Callback<IEnumerable<RegistroEpisodio>>(r => guardados = r.ToList()).Returns(Task.CompletedTask);

        await sut.Episodios.MarcarVistosCommand.ExecuteAsync(new List<EpisodioItem> { episodio });

        guardados.Should().ContainSingle();
        guardados![0].VistoLocal.Should().BeTrue();
        guardados[0].ProgresoSegundos.Should().Be(0, "visto a mano: sin punto de reanudación");
        guardados[0].TotalSegundos.Should().Be(1420, "antes se guardaba a cero y se perdía la duración");
    }

    // ── Reproducir un episodio que no está en disco ──

    [Fact]
    public async Task Reproducir_EpisodioSinArchivo_SoloAvisa()
    {
        var sut = await AbrirFichaAsync(total: 2, enDisco: [], vistos: []);
        bool navegado = false;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Reproductor>(receptor, (_, _) => navegado = true);
        try
        {
            await sut.Episodios.ReproducirEpisodioCommand.ExecuteAsync(sut.Episodios.EpisodiosDelAnime[0]);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }

        navegado.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_EpisodioNoEncontradoTitulo"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Liberación de la ficha ──

    [Fact]
    public async Task Dispose_DejaDeEscucharLaConfiguracionYLosMensajes()
    {
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(a => a.ObtenerConfiguracion()).Returns(new AppSettings());
        var sut = await AbrirFichaAsync(total: 1, enDisco: [], vistos: [], ajustes.Object);
        ajustes.VerifyAdd(a => a.ConfiguracionModificada += It.IsAny<Action<AppSettings>>(), Times.Once());
        WeakReferenceMessenger.Default.IsRegistered<DescargaProgresoMensaje>(sut.Episodios).Should().BeTrue();

        sut.Dispose();

        ajustes.VerifyRemove(a => a.ConfiguracionModificada -= It.IsAny<Action<AppSettings>>(), Times.Once());
        WeakReferenceMessenger.Default.IsRegistered<DescargaProgresoMensaje>(sut.Episodios).Should().BeFalse();
        WeakReferenceMessenger.Default.IsRegistered<EpisodioActualizadoMensaje>(sut.Episodios).Should().BeFalse();
        sut.EstaLiberado.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_DosVeces_NoFalla()
    {
        var sut = await AbrirFichaAsync(total: 1, enDisco: [], vistos: []);

        sut.Dispose();
        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Inicializar_SobreUnaFichaYaLiberada_NoArrancaCargas()
    {
        var sut = await AbrirFichaAsync(total: 2, enDisco: [], vistos: []);
        sut.Dispose();

        await sut.InicializarAsync(sut.AnimeSeleccionado!);

        sut.Episodios.EpisodiosDelAnime.Should().BeEmpty("se salió de la ficha: no se rellena ni se lanzan cargas de fondo");
    }
}
