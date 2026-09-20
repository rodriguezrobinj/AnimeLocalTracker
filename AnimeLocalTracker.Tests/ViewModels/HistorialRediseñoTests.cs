using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class HistorialRediseñoTests
{
    private static HistorialViewModel CrearSut(List<RegistroEpisodio> registros)
    {
        WeakReferenceMessenger.Default.Reset();
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerHistorialEpisodiosAsync(It.IsAny<int>())).ReturnsAsync(registros);
        db.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>());
        return new HistorialViewModel(db.Object, new Mock<IPlaybackStateService>().Object,
            new Mock<IDialogService>().Object, new Mock<IFileScannerService>().Object);
    }

    private static List<RegistroEpisodio> Registros() =>
    [
        new() { AniListId = 1, NumeroEpisodio = 1, ProgresoSegundos = 600, TotalSegundos = 1400, UltimaReproduccion = DateTime.UtcNow },
        new() { AniListId = 2, NumeroEpisodio = 2, VistoLocal = true, TotalSegundos = 1200, UltimaReproduccion = DateTime.UtcNow },
        new() { AniListId = 3, NumeroEpisodio = 3, VistoLocal = true, TotalSegundos = 1200, UltimaReproduccion = DateTime.UtcNow },
    ];

    [Fact]
    public async Task Filtros_MuestranContadoresPorEstado()
    {
        var sut = CrearSut(Registros());
        await sut.CargarHistorialAsync();

        sut.Filtros.Select(f => f.Clave).Should().Equal("Todos", "EnProgreso", "Completados");
        sut.Filtros[0].Etiqueta.Should().EndWith("3");
        sut.Filtros[1].Etiqueta.Should().EndWith("1");
        sut.Filtros[2].Etiqueta.Should().EndWith("2");
        sut.Filtros[0].EsActivo.Should().BeTrue();
    }

    [Fact]
    public async Task CambiarFiltro_MarcaElChipActivoYFiltraLaLista()
    {
        var sut = CrearSut(Registros());
        await sut.CargarHistorialAsync();

        sut.CambiarFiltro("Completados");

        sut.Filtros.Single(f => f.EsActivo).Clave.Should().Be("Completados");
        sut.ItemsFiltrados.Should().HaveCount(2);
    }

    [Fact]
    public async Task TiempoVisto_SumaCompletosYProgresoParcial()
    {
        var sut = CrearSut(Registros());
        await sut.CargarHistorialAsync();

        // 600 s (a medias) + 1200 + 1200 = 3000 s = 50 min
        sut.TiempoVistoTexto.Should().Be("50 min");
    }

    [Fact]
    public void Item_EstadoYAccion_SegunProgreso()
    {
        var enProgreso = new HistorialItemViewModel { NumeroEpisodio = 4, ProgresoSegundos = 300, TotalSegundos = 1400 };
        enProgreso.TieneEstado.Should().BeTrue();
        enProgreso.AccionEsPrimaria.Should().BeTrue();
        enProgreso.EpisodioCorto.Should().Contain("4");

        var visto = new HistorialItemViewModel { NumeroEpisodio = 4, VistoLocal = true };
        visto.TieneEstado.Should().BeTrue();
        visto.AccionEsPrimaria.Should().BeFalse();
        visto.EstadoColor.Should().Be("#A78BFA");

        var abierto = new HistorialItemViewModel { NumeroEpisodio = 4, ProgresoSegundos = 2 };
        abierto.PorEmpezar.Should().BeTrue();
        abierto.TieneEstado.Should().BeTrue();
        abierto.MostrarProgreso.Should().BeFalse();
        abierto.EstadoColor.Should().Be("#94A3B8");
        enProgreso.PorEmpezar.Should().BeFalse();
    }

    [Fact]
    public void Item_AlMarcarVisto_CambiaAccion()
    {
        var item = new HistorialItemViewModel { NumeroEpisodio = 1, ProgresoSegundos = 300, TotalSegundos = 1400 };
        string antes = item.AccionTexto;

        item.VistoLocal = true;

        item.AccionTexto.Should().NotBe(antes);
        item.AccionEsPrimaria.Should().BeFalse();
    }
}
