using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Ventana "Orden de visionado" de la ficha: qué se pinta, "Siguiente", "Estás aquí", añadir y abrir.</summary>
public sealed class OrdenVisionadoFichaViewModelTests
{
    private readonly Mock<IOrdenVisionadoService> _servicio = new();
    private readonly Mock<IDialogService> _dialogos = new();

    private static ParteOrdenVisionado P(int id, int numero, EstadoParteSaga estado, bool extra = false, bool aparte = false) =>
        new(id, numero, extra, aparte, estado == EstadoParteSaga.NoLaTienes ? null : new AnimeItem { AniListId = id, Titulo = $"A{id}" },
            $"A{id}", "TV", 2013, null, estado);

    private OrdenVisionadoFichaViewModel CrearSut(int animeId, OrdenVisionadoLeido guardado, OrdenVisionadoLeido actualizado)
    {
        _servicio.Setup(s => s.LeerGuardadoAsync(animeId)).ReturnsAsync(guardado);
        _servicio.Setup(s => s.ActualizarAsync(animeId, It.IsAny<CancellationToken>())).ReturnsAsync(actualizado);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(animeId);
        return sut;
    }

    private static async Task AbrirYEsperar(OrdenVisionadoFichaViewModel sut)
    {
        await sut.AbrirCommand.ExecuteAsync(null);
        await sut.Carga;
    }

    private static List<FilaOrdenVisionado> Filas(OrdenVisionadoFichaViewModel sut) => sut.Filas.OfType<FilaOrdenVisionado>().ToList();

    [Fact]
    public async Task Abrir_MarcaEstasAqui_YLaSiguienteEsLaPrimeraDeLaLineaSinVer()
    {
        var leido = new OrdenVisionadoLeido(new[]
        {
            P(1, 1, EstadoParteSaga.Vista), P(10, 0, EstadoParteSaga.NoLaTienes, extra: true),
            P(2, 2, EstadoParteSaga.AMedias), P(3, 3, EstadoParteSaga.Descargada), P(20, 0, EstadoParteSaga.NoLaTienes, aparte: true),
        }, false);
        var sut = CrearSut(2, leido, leido);

        await AbrirYEsperar(sut);

        sut.MostrandoOrdenVisionado.Should().BeTrue();
        Filas(sut).Single(f => f.EsActual).AniListId.Should().Be(2);
        Filas(sut).Single(f => f.EsSiguiente).AniListId.Should().Be(2, "la de la ficha está a medias: es la siguiente");
        sut.Filas.OfType<string>().Should().ContainSingle("cabecera del grupo aparte");
        sut.Filas.IndexOf(sut.Filas.OfType<string>().Single()).Should().Be(sut.Filas.Count - 2, "la cabecera va justo antes del grupo");
        sut.SinPartes.Should().BeFalse();
        sut.MostrarVacio.Should().BeFalse();
    }

    [Fact]
    public async Task ExtrasYAparte_NuncaSonLaSiguiente_YTodoVisto_NoMarcaNinguna()
    {
        var leido = new OrdenVisionadoLeido(new[]
        {
            P(1, 1, EstadoParteSaga.Vista), P(10, 0, EstadoParteSaga.NoLaTienes, extra: true), P(20, 0, EstadoParteSaga.NoLaTienes, aparte: true),
        }, false);
        var sut = CrearSut(1, leido, leido);

        await AbrirYEsperar(sut);

        Filas(sut).Should().NotContain(f => f.EsSiguiente);
    }

    [Fact]
    public async Task SoloElPropioAnime_EstadoVacio()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.EnBiblioteca) }, false);
        var sut = CrearSut(1, leido, leido);

        await AbrirYEsperar(sut);

        sut.SinPartes.Should().BeTrue();
        sut.MostrarVacio.Should().BeTrue();
    }

    [Fact]
    public async Task PintaLoGuardado_YLuegoLoActualizado_ConAvisoSinConexion()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista) }, false);
        var actualizado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, true);
        var sut = CrearSut(1, guardado, actualizado);

        await AbrirYEsperar(sut);

        Filas(sut).Select(f => f.AniListId).Should().Equal(1, 2);
        sut.SinConexion.Should().BeTrue();
        sut.EstaCargando.Should().BeFalse();
        sut.MostrarCargando.Should().BeFalse();
    }

    [Fact]
    public async Task ActualizadoVacio_ConservaLoGuardado_YAvisa()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, guardado, new OrdenVisionadoLeido(System.Array.Empty<ParteOrdenVisionado>(), true));

        await AbrirYEsperar(sut);

        Filas(sut).Should().HaveCount(2);
        sut.SinConexion.Should().BeTrue();
    }

    [Fact]
    public async Task CerrarMientrasCarga_NoPintaLoQueLlegaDespues()
    {
        var guardado = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista) }, false);
        var tardio = new TaskCompletionSource<OrdenVisionadoLeido>();
        _servicio.Setup(s => s.LeerGuardadoAsync(1)).ReturnsAsync(guardado);
        _servicio.Setup(s => s.ActualizarAsync(1, It.IsAny<CancellationToken>())).Returns(tardio.Task);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(1);

        await sut.AbrirCommand.ExecuteAsync(null);
        sut.CerrarCommand.Execute(null);
        tardio.SetResult(new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.Vista) }, false));
        await sut.Carga;

        Filas(sut).Should().HaveCount(1);
        sut.MostrandoOrdenVisionado.Should().BeFalse();
    }

    [Fact]
    public async Task Anadir_ConExito_LaFilaPasaAEnTuBiblioteca()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        _servicio.Setup(s => s.AnadirABibliotecaAsync(2)).ReturnsAsync(new AnimeItem { AniListId = 2, Titulo = "A2" });
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        await sut.AnadirCommand.ExecuteAsync(fila);

        fila.Estado.Should().Be(EstadoParteSaga.EnBiblioteca);
        fila.Anime.Should().NotBeNull();
        fila.PuedeAnadir.Should().BeFalse();
        fila.EsSiguiente.Should().BeTrue("sigue siendo la primera de la línea sin ver");
    }

    [Fact]
    public async Task Anadir_ConError_AvisaYNoCambia()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        await sut.AnadirCommand.ExecuteAsync(fila);

        fila.Estado.Should().Be(EstadoParteSaga.NoLaTienes);
        fila.EstaAnadiendo.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Det_OrdenAnadirErrorTitulo"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Anadir_MientrasAnade_NoRepite()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.NoLaTienes) }, false);
        var sut = CrearSut(1, leido, leido);
        var alta = new TaskCompletionSource<AnimeItem?>();
        _servicio.Setup(s => s.AnadirABibliotecaAsync(2)).Returns(alta.Task);
        await AbrirYEsperar(sut);
        var fila = Filas(sut).Single(f => f.AniListId == 2);

        var primera = sut.AnadirCommand.ExecuteAsync(fila);
        await sut.AnadirCommand.ExecuteAsync(fila);
        alta.SetResult(new AnimeItem { AniListId = 2 });
        await primera;

        _servicio.Verify(s => s.AnadirABibliotecaAsync(2), Times.Once);
    }

    [Fact]
    public async Task AbrirFila_DeLaBiblioteca_NavegaASuFichaYCierra()
    {
        var leido = new OrdenVisionadoLeido(new[] { P(1, 1, EstadoParteSaga.Vista), P(2, 2, EstadoParteSaga.EnBiblioteca) }, false);
        var sut = CrearSut(1, leido, leido);
        await AbrirYEsperar(sut);
        NavegarMensaje_Detalle? recibido = null;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Detalle>(receptor, (_, m) => recibido = m);
        try
        {
            sut.AbrirFilaCommand.Execute(Filas(sut).Single(f => f.AniListId == 2));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }

        recibido!.AnimeSeleccionado.AniListId.Should().Be(2);
        sut.MostrandoOrdenVisionado.Should().BeFalse();
    }

    [Fact]
    public async Task ComprobarDisponible_DaElValorDelServicio()
    {
        _servicio.Setup(s => s.TieneFranquiciaOSinConsultarAsync(7)).ReturnsAsync(true);
        var sut = new OrdenVisionadoFichaViewModel(_servicio.Object, _dialogos.Object);
        sut.Preparar(7);

        await sut.ComprobarDisponibleAsync(CancellationToken.None);

        sut.HayOrdenVisionado.Should().BeTrue();
    }

    [Fact]
    public void Fila_Subtitulo_TraduceElFormato_YSinDatosTieneTitulo()
    {
        var fila = new FilaOrdenVisionado(P(3, 3, EstadoParteSaga.Vista) with { Formato = "MOVIE", Anio = 2020 }, esActual: false);
        var sinDatos = new FilaOrdenVisionado(P(4, 4, EstadoParteSaga.NoLaTienes) with { Titulo = null, Formato = "", Anio = 0 }, esActual: false);

        fila.Subtitulo.Should().Be($"{LocalizationService.T("Fmt_MOVIE")} · 2020");
        sinDatos.Titulo.Should().Be(LocalizationService.T("Det_OrdenSinDatos"));
        sinDatos.Subtitulo.Should().BeEmpty();
    }
}
