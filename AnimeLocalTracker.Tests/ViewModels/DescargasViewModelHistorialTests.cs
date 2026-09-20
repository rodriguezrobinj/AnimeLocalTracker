using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Pestaña Descargas: historial persistido (agrupación, filtros, búsqueda, reintento) y estado de la cola en vivo.</summary>
public class DescargasViewModelHistorialTests
{
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IDatabaseService> _db = new();
    private static readonly string[] TitulosGuardados = { "Sousou no Frieren", "Frieren Beyond" };

    private static DescargaHistorial Fila(int id, int ep, bool ok, DateTime fechaUtc, string titulo = "Frieren", string? error = null) => new()
    {
        Id = id,
        AniListId = 10,
        AnimeTitulo = titulo,
        NumeroEpisodio = ep,
        CarpetaDestino = @"C:\Anime\Frieren",
        RutaArchivo = ok ? $@"C:\NoExiste\Ep{ep}.mp4" : string.Empty,
        TamanoBytes = ok ? 300L * 1024 * 1024 : 0,
        FechaUtc = fechaUtc,
        Completada = ok,
        Error = error,
        TitulosAlternativos = "Sousou no Frieren | Frieren Beyond"
    };

    private async Task<DescargasViewModel> CrearAsync(params DescargaHistorial[] filas)
    {
        _descargas.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());
        _db.Setup(d => d.ObtenerDescargasHistorialAsync(It.IsAny<int>())).ReturnsAsync(filas.ToList());
        var sut = new DescargasViewModel(_descargas.Object, _db.Object);
        await sut.CargarHistorialAsync();
        return sut;
    }

    [Fact]
    public async Task CargarHistorial_AgrupaPorDiaYCalculaContadores()
    {
        var sut = await CrearAsync(
            Fila(3, 3, true, DateTime.UtcNow),
            Fila(2, 2, false, DateTime.UtcNow.AddMinutes(-5), error: "sin enlace"),
            Fila(1, 1, true, DateTime.UtcNow.AddDays(-3)));

        sut.TotalHistorial.Should().Be(3);
        sut.TotalCompletadas.Should().Be(2);
        sut.TotalFallidas.Should().Be(1);
        sut.TieneHistorial.Should().BeTrue();
        sut.ItemsAgrupados.OfType<string>().Should().HaveCountGreaterThanOrEqualTo(2, "hoy y hace 3 días son grupos distintos");
        sut.ItemsAgrupados[0].Should().BeOfType<string>("la lista empieza con una cabecera de día");
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Select(i => i.NumeroEpisodio).Should().Equal(3, 2, 1);
    }

    [Fact]
    public async Task ArchivoQueYaNoExiste_SeMarcaEnLasCompletadas_PeroNoEnLasFallidas()
    {
        var sut = await CrearAsync(Fila(1, 1, true, DateTime.UtcNow), Fila(2, 2, false, DateTime.UtcNow, error: "x"));

        var items = sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().ToList();
        items.Single(i => i.Completada).ArchivoExiste.Should().BeFalse("la ruta de la prueba no existe en disco");
        items.Single(i => !i.Completada).ArchivoExiste.Should().BeTrue("una fallida no tiene archivo que echar en falta");
    }

    [Fact]
    public async Task Filtros_SoloFallidasYSoloCompletadas()
    {
        var sut = await CrearAsync(Fila(1, 1, true, DateTime.UtcNow), Fila(2, 2, false, DateTime.UtcNow, error: "x"));

        sut.CambiarFiltroCommand.Execute(DescargasViewModel.FiltroFallidas);
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Should().ContainSingle(i => !i.Completada);

        sut.CambiarFiltroCommand.Execute(DescargasViewModel.FiltroCompletadas);
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Should().ContainSingle(i => i.Completada);

        sut.CambiarFiltroCommand.Execute(DescargasViewModel.FiltroTodas);
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Busqueda_FiltraPorTituloOAlternativoYAvisaSiNoHayResultados()
    {
        var sut = await CrearAsync(
            Fila(1, 1, true, DateTime.UtcNow, "Frieren"),
            Fila(2, 1, true, DateTime.UtcNow, "One Piece"));

        sut.TextoBusqueda = "piece";
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Should().ContainSingle(i => i.AnimeTitulo == "One Piece");

        sut.TextoBusqueda = "beyond"; // solo aparece en los títulos alternativos
        sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Should().HaveCount(2);

        sut.TextoBusqueda = "zzz";
        sut.ItemsAgrupados.Should().BeEmpty();
        sut.SinResultados.Should().BeTrue();
    }

    [Fact]
    public async Task Reintentar_EncolaDeNuevoConLosTitulosGuardadosYQuitaLaFilaAntigua()
    {
        var sut = await CrearAsync(Fila(5, 4, false, DateTime.UtcNow, error: "timeout"));
        var item = sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Single();
        _descargas.Setup(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()))
            .Returns(Task.CompletedTask);
        _db.Setup(d => d.EliminarDescargaHistorialAsync(5)).Returns(Task.CompletedTask);

        await sut.ReintentarCommand.ExecuteAsync(item);

        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(10, "Frieren", @"C:\Anime\Frieren", 4,
            It.Is<IEnumerable<string>?>(t => t != null && t.SequenceEqual(TitulosGuardados))), Times.Once);
        _db.Verify(d => d.EliminarDescargaHistorialAsync(5), Times.Once);
        sut.EsPestanaActivas.Should().BeTrue("tras reintentar se muestra la cola");
    }

    [Fact]
    public async Task ReintentarFallidas_SoloVuelveAEncolarLasFallidas()
    {
        var sut = await CrearAsync(
            Fila(1, 1, true, DateTime.UtcNow),
            Fila(2, 2, false, DateTime.UtcNow, error: "x"),
            Fila(3, 3, false, DateTime.UtcNow, error: "y"));
        _descargas.Setup(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()))
            .Returns(Task.CompletedTask);

        await sut.ReintentarFallidasCommand.ExecuteAsync(null);

        _descargas.Verify(d => d.IniciarDescargaEpisodioAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LimpiarFallidas_PideALaBaseDeDatosBorrarSoloLasFallidas()
    {
        var sut = await CrearAsync(Fila(1, 1, false, DateTime.UtcNow, error: "x"));

        await sut.LimpiarFallidasCommand.ExecuteAsync(null);

        _db.Verify(d => d.LimpiarDescargasHistorialAsync(true), Times.Once);
    }

    [Fact]
    public void Priorizar_LlamaAlServicioYSubeLaFilaAlPrimerPuestoDeLaCola()
    {
        var activa = new DescargaItem { AniListId = 1, NumeroEpisodio = 1, EnCola = false };
        var espera2 = new DescargaItem { AniListId = 1, NumeroEpisodio = 2, EnCola = true };
        var espera3 = new DescargaItem { AniListId = 1, NumeroEpisodio = 3, EnCola = true };
        _descargas.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem> { activa, espera2, espera3 });
        _descargas.Setup(d => d.PriorizarDescarga(1, 3)).Returns(true);
        var sut = new DescargasViewModel(_descargas.Object);

        sut.PriorizarCommand.Execute(espera3);

        _descargas.Verify(d => d.PriorizarDescarga(1, 3), Times.Once);
        sut.ColaDescargas.Select(d => d.NumeroEpisodio).Should().Equal(1, 3, 2);
    }

    [Fact]
    public void AplicarMensaje_ActualizaEnColaVelocidadTotalYReintentos()
    {
        _descargas.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, EnCola = true },
            new() { AniListId = 1, NumeroEpisodio = 2, EnCola = true }
        });
        var sut = new DescargasViewModel(_descargas.Object);
        sut.ConteoEnCola.Should().Be(2);

        sut.AplicarMensaje(new DescargaProgresoMensaje(1, 1, 25, true, false, false, "", null, "A", "2,0 MB/s", velocidadBps: 2 * 1048576, enCola: false, reintentos: 1));

        var item = sut.ColaDescargas.Single(d => d.NumeroEpisodio == 1);
        item.EnCola.Should().BeFalse();
        item.Reintentos.Should().Be(1);
        sut.ConteoEnCola.Should().Be(1);
        sut.VelocidadTotalTexto.Should().EndWith("MB/s");
    }

    [Fact]
    public void DescargaItem_EstadoYDetalle_ReflejanColaPausaYVelocidad()
    {
        var item = new DescargaItem { EnCola = true };
        item.EstadoTexto.Should().Be(LocalizationService.T("Desc_EstadoEnCola"));
        item.DetalleTexto.Should().BeEmpty();

        item.EnCola = false;
        item.Progreso = 40;
        item.VelocidadDescarga = "3,1 MB/s";
        item.EstadoTexto.Should().Be(LocalizationService.T("Desc_EstadoDescargando"));
        item.DetalleTexto.Should().Contain("3,1 MB/s");
        item.ProgresoTexto.Should().Be("40%");

        item.IsPaused = true;
        item.EstadoTexto.Should().Be(LocalizationService.T("Desc_EnPausa"));
        item.DetalleTexto.Should().BeEmpty();
    }

    [Fact]
    public void AplicarMensaje_ConProgresoRepetido_NoRecreaLosChips()
    {
        // Regresión: los botones Activas/Historial/filtros se regeneraban en cada tick de progreso y los clics se perdían.
        _descargas.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem> { new() { AniListId = 1, NumeroEpisodio = 1 } });
        var sut = new DescargasViewModel(_descargas.Object);
        var pestanas = sut.Pestanas;
        var filtros = sut.Filtros;

        for (int p = 1; p <= 20; p++)
            sut.AplicarMensaje(new DescargaProgresoMensaje(1, 1, p, true, false, false, "", null, "A", "1,0 MB/s", velocidadBps: 1048576));

        sut.Pestanas.Should().BeSameAs(pestanas);
        sut.Filtros.Should().BeSameAs(filtros);
    }
}
