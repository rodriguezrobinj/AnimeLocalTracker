using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Logros;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class LogrosViewModelTests
{
    private readonly Mock<ILogrosService> _servicioMock = new();

    private static ResumenLogros Resumen(Dictionary<string, double>? metricas = null) =>
        MotorLogros.Evaluar(metricas ?? new Dictionary<string, double>(), new Dictionary<string, (int, DateTime?)>());

    private LogrosViewModel CrearSut(ResumenLogros? resumen = null)
    {
        _servicioMock.Setup(s => s.EvaluarAsync(It.IsAny<bool>())).ReturnsAsync(resumen ?? Resumen());
        return new LogrosViewModel(_servicioMock.Object);
    }

    private static List<LogroItemViewModel> Tarjetas(LogrosViewModel sut) =>
        sut.ItemsAgrupados.OfType<LogroItemViewModel>().ToList();

    private static List<string> Cabeceras(LogrosViewModel sut) =>
        sut.ItemsAgrupados.OfType<string>().ToList();

    [Fact]
    public async Task Cargar_AgrupaPorCategoria_CabeceraYTarjetas()
    {
        var sut = CrearSut();

        await sut.CargarAsync();

        Cabeceras(sut).Should().HaveCount(Enum.GetValues<CategoriaLogro>().Length);
        Tarjetas(sut).Should().HaveCount(CatalogoLogros.Todos.Count);
        sut.TieneElementos.Should().BeTrue();
        sut.EstaVacio.Should().BeFalse();
        sut.EstaCargando.Should().BeFalse();
    }

    [Fact]
    public async Task Cargar_ConservaElOrdenDelCatalogoDentroDeCadaCategoria()
    {
        var sut = CrearSut();

        await sut.CargarAsync();

        var idsMaraton = Tarjetas(sut).Where(t => t.Categoria == CategoriaLogro.Maraton).Select(t => t.Estado.Id);
        idsMaraton.Should().Equal("maraton_dia", "horas", "episodios");
    }

    [Fact]
    public async Task FiltrarPorCategoria_MuestraSoloEsaCategoriaYMarcaElChip()
    {
        var sut = CrearSut();
        await sut.CargarAsync();

        sut.SeleccionarCategoriaCommand.Execute(nameof(CategoriaLogro.Secretos));

        Tarjetas(sut).Should().OnlyContain(t => t.Categoria == CategoriaLogro.Secretos).And.HaveCount(2);
        Cabeceras(sut).Should().HaveCount(1);
        sut.Categorias.Single(c => c.EsActivo).Clave.Should().Be(nameof(CategoriaLogro.Secretos));
    }

    [Fact]
    public async Task FiltrarPorTodas_RestauraLaLista()
    {
        var sut = CrearSut();
        await sut.CargarAsync();
        sut.SeleccionarCategoriaCommand.Execute(nameof(CategoriaLogro.Secretos));

        sut.SeleccionarCategoriaCommand.Execute(LogrosViewModel.TodasLasCategorias);

        Tarjetas(sut).Should().HaveCount(CatalogoLogros.Todos.Count);
    }

    [Fact]
    public async Task FiltroDesbloqueados_SoloLosQueTienenAlgunNivel()
    {
        var sut = CrearSut(Resumen(new() { ["horas"] = 60, ["racha"] = 3 }));
        await sut.CargarAsync();

        sut.SeleccionarEstadoCommand.Execute(LogrosViewModel.EstadoDesbloqueados);

        Tarjetas(sut).Select(t => t.Estado.Id).Should().BeEquivalentTo("horas", "racha");
    }

    [Fact]
    public async Task FiltroBloqueados_ExcluyeLosDesbloqueados()
    {
        var sut = CrearSut(Resumen(new() { ["horas"] = 60 }));
        await sut.CargarAsync();

        sut.SeleccionarEstadoCommand.Execute(LogrosViewModel.EstadoBloqueados);

        Tarjetas(sut).Should().NotContain(t => t.Estado.Id == "horas");
        Tarjetas(sut).Should().HaveCount(CatalogoLogros.Todos.Count - 1);
    }

    [Fact]
    public async Task FiltroEnProgreso_ExcluyeCompletosOcultosYSinAvance()
    {
        var sut = CrearSut(Resumen(new() { ["horas"] = 5000, ["episodios"] = 10, ["insomnio"] = 0 }));
        await sut.CargarAsync();

        sut.SeleccionarEstadoCommand.Execute(LogrosViewModel.EstadoEnProgreso);

        // "horas" está completo, "insomnio" oculto y el resto sin avance: solo queda "episodios"
        Tarjetas(sut).Select(t => t.Estado.Id).Should().Equal("episodios");
    }

    [Fact]
    public async Task SinCoincidencias_MuestraSinResultadosYNoElVacio()
    {
        var sut = CrearSut(Resumen(new() { ["horas"] = 5000 }));
        await sut.CargarAsync();

        sut.SeleccionarCategoriaCommand.Execute(nameof(CategoriaLogro.Secretos));
        sut.SeleccionarEstadoCommand.Execute(LogrosViewModel.EstadoDesbloqueados);

        sut.TieneElementos.Should().BeFalse();
        sut.SinResultados.Should().BeTrue();
        sut.EstaVacio.Should().BeFalse();
    }

    [Fact]
    public async Task Cargar_PublicaElResumenDeRangoYContadores()
    {
        var sut = CrearSut(Resumen(new() { ["horas"] = 100, ["racha"] = 7 }));

        await sut.CargarAsync();

        sut.RangoNombre.Should().NotBeNullOrWhiteSpace();
        sut.PuntosTexto.Should().NotBeNullOrWhiteSpace();
        sut.NivelesTexto.Should().Contain("5");
        sut.ProgresoTotal.Should().BeGreaterThan(0);
        sut.Contadores.Should().HaveCount(5);
        sut.Contadores.Single(c => c.Color == LogroItemViewModel.ColorDeNivel(1)).Cantidad.Should().Be(2);
    }

    [Fact]
    public async Task ElFiltroSeConservaAlRecargar()
    {
        var sut = CrearSut();
        await sut.CargarAsync();
        sut.SeleccionarCategoriaCommand.Execute(nameof(CategoriaLogro.Horarios));

        await sut.CargarAsync();

        Tarjetas(sut).Should().OnlyContain(t => t.Categoria == CategoriaLogro.Horarios);
        sut.Categorias.Single(c => c.EsActivo).Clave.Should().Be(nameof(CategoriaLogro.Horarios));
    }

    [Fact]
    public async Task NecesitaRecargar_TrueAlPrincipio_FalseTrasCargarDentroDelCooldown()
    {
        var sut = CrearSut();
        sut.NecesitaRecargar().Should().BeTrue();

        await sut.CargarAsync();

        sut.NecesitaRecargar().Should().BeFalse();
    }

    [Fact]
    public async Task ErrorDelServicio_NoRompeLaVistaYSeQuitaElSpinner()
    {
        _servicioMock.Setup(s => s.EvaluarAsync(It.IsAny<bool>())).ThrowsAsync(new InvalidOperationException("BD bloqueada"));
        var sut = new LogrosViewModel(_servicioMock.Object);

        var act = async () => await sut.CargarAsync();

        await act.Should().NotThrowAsync();
        sut.EstaCargando.Should().BeFalse();
        sut.EstaVacio.Should().BeTrue();
    }

    // ───────────────────────────── Tarjeta individual ─────────────────────────────

    [Fact]
    public void SecretoSinDesbloquear_NoRevelaNombreNiDescripcionYMuestraCandado()
    {
        var estado = Resumen().Logros.Single(l => l.Id == "insomnio");

        var item = new LogroItemViewModel(estado);

        item.Oculto.Should().BeTrue();
        item.Icono.Should().Be("LockQuestion");
        item.Titulo.Should().NotContain("Insomnio").And.NotContain("Insomnia");
        item.MostrarProgreso.Should().BeFalse();
        item.ProgresoTexto.Should().BeEmpty("no debe delatar la meta del secreto");
        item.PuntosTexto.Should().BeEmpty();
    }

    [Fact]
    public void SecretoDesbloqueado_MuestraSuNombreReal()
    {
        var estado = Resumen(new() { ["insomnio"] = 3 }).Logros.Single(l => l.Id == "insomnio");

        var item = new LogroItemViewModel(estado);

        item.Oculto.Should().BeFalse();
        item.Icono.Should().Be("BedClock");
    }

    [Fact]
    public void Tarjeta_ProgresoYNivelesReflejanElEstado()
    {
        var estado = Resumen(new() { ["episodios"] = 40 }).Logros.Single(l => l.Id == "episodios"); // nivel 1 (25), siguiente 100

        var item = new LogroItemViewModel(estado);

        item.Desbloqueado.Should().BeTrue();
        item.ProgresoTexto.Should().Be("40 / 100");
        item.ProgresoPorcentaje.Should().BeApproximately(40.0, 1e-9);
        item.MostrarProgreso.Should().BeTrue();
        item.Niveles.Should().HaveCount(5);
        item.Niveles.Count(n => n.Conseguido).Should().Be(1);
        item.NivelColor.Should().Be(LogroItemViewModel.ColorDeNivel(1));
        item.TieneFecha.Should().BeFalse("un logro anterior a esta función no tiene fecha real");
    }

    [Fact]
    public void Tarjeta_ConFecha_LaMuestra()
    {
        var definicion = CatalogoLogros.Todos.Single(l => l.Id == "horas");
        var estado = new LogroEstado(definicion, 60, 2, new DateTime(2026, 9, 12, 15, 0, 0, DateTimeKind.Utc));

        new LogroItemViewModel(estado).TieneFecha.Should().BeTrue();
    }

    [Fact]
    public void Tarjeta_Completa_NoMuestraBarraYMarcaMaximo()
    {
        var estado = Resumen(new() { ["horas"] = 5000 }).Logros.Single(l => l.Id == "horas");

        var item = new LogroItemViewModel(estado);

        item.Completo.Should().BeTrue();
        item.MostrarProgreso.Should().BeFalse();
        item.Niveles.Should().OnlyContain(n => n.Conseguido);
    }

    [Theory]
    [InlineData(25.0, "25")]
    [InlineData(1500.0, "1500")]
    [InlineData(1234.4, "1234")]
    [InlineData(10.0, "10")]
    public void FormatoNumero_EnterosYValoresGrandesSinDecimales(double valor, string esperado)
    {
        LogroItemViewModel.FormatoNumero(valor).Should().Be(esperado);
    }
}
