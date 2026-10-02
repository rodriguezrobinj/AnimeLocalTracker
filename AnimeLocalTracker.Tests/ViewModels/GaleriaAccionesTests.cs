using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Acciones de la Galería que tocan los datos del usuario: "Actualizar biblioteca" y la selección múltiple.</summary>
public class GaleriaAccionesTests
{
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _database = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IDialogService> _dialogos = new();

    private async Task<GaleriaViewModel> CrearAsync(params AnimeItem[] animes)
    {
        _database.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes.ToList());
        _database.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        _database.Setup(d => d.ObtenerSeguimientosPendientesAsync()).ReturnsAsync(new List<SeguimientoLocal>());

        var sut = new GaleriaViewModel(_tracking.Object, _database.Object, _auth.Object, _dialogos.Object,
            new Mock<IHttpClientFactory>().Object, new Mock<IImageCacheService>().Object, new Mock<IFileScannerService>().Object)
        {
            PausaAviso = TimeSpan.Zero
        };
        await Task.Delay(100); // la biblioteca se carga desde el constructor
        return sut;
    }

    private void ConCuenta() => _auth.Setup(a => a.ObtenerTokenGuardado()).Returns("tok");

    private void AniListDevuelve(params AniListMedia[] medias) =>
        _tracking.Setup(t => t.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .ReturnsAsync(medias.ToDictionary(m => m.Id));

    private static void Seleccionar(GaleriaViewModel sut, params AnimeItem[] animes)
    {
        sut.ToggleModoSeleccionCommand.Execute(null);
        foreach (var anime in animes) sut.AbrirDetalleCommand.Execute(anime);
    }

    // === Actualizar biblioteca ===

    [Fact]
    public async Task ActualizarBiblioteca_SinProximoEpisodioNiTotalEnAniList_ConservaElTotalConocido()
    {
        var onePiece = new AnimeItem { AniListId = 21, Titulo = "One Piece", TotalEpisodios = 1180, Estado = "RELEASING" };
        var sut = await CrearAsync(onePiece);
        AniListDevuelve(new AniListMedia { Id = 21, Status = "RELEASING", Episodes = null, NextAiringEpisode = null });

        await sut.ActualizarBibliotecaCommand.ExecuteAsync(null);

        onePiece.TotalEpisodios.Should().Be(1180);
    }

    [Fact]
    public async Task ActualizarBiblioteca_SiFalla_DejaElBotonLibreYAvisa()
    {
        var sut = await CrearAsync(new AnimeItem { AniListId = 1, Titulo = "Bleach" });
        _tracking.Setup(t => t.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("base de datos ocupada"));

        await sut.ActualizarBibliotecaCommand.ExecuteAsync(null);

        sut.EstaActualizando.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ActualizarBiblioteca_NoPisaUnEstadoQueAunNoSeEnvioAAniList()
    {
        ConCuenta();
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "COMPLETED" };
        var sut = await CrearAsync(anime);
        _database.Setup(d => d.ObtenerSeguimientosPendientesAsync())
            .ReturnsAsync(new List<SeguimientoLocal> { new() { AniListId = 1, Estado = "COMPLETED", Pendiente = true } });
        AniListDevuelve(new AniListMedia { Id = 1, Status = "FINISHED", Episodes = 12, MediaListEntry = new() { Status = "PLANNING" } });

        await sut.ActualizarBibliotecaCommand.ExecuteAsync(null);

        anime.EstadoUsuario.Should().Be("COMPLETED");
    }

    // === Selección múltiple ===

    [Fact]
    public async Task CategorizarSeleccionados_ConCuenta_EnviaElEstadoAAniList()
    {
        ConCuenta();
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "PLANNING" };
        var sut = await CrearAsync(anime);
        _tracking.Setup(t => t.GuardarEstadoSeguimientoAsync(1, "COMPLETED", "tok")).ReturnsAsync(true);
        Seleccionar(sut, anime);

        await sut.CategorizarSeleccionadosCommand.ExecuteAsync("COMPLETED");

        anime.EstadoUsuario.Should().Be("COMPLETED");
        _tracking.Verify(t => t.GuardarEstadoSeguimientoAsync(1, "COMPLETED", "tok"), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CategorizarSeleccionados_SinCuenta_NoLlamaAAniList()
    {
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "PLANNING" };
        var sut = await CrearAsync(anime);
        Seleccionar(sut, anime);

        await sut.CategorizarSeleccionadosCommand.ExecuteAsync("COMPLETED");

        anime.EstadoUsuario.Should().Be("COMPLETED");
        _tracking.Verify(t => t.GuardarEstadoSeguimientoAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CategorizarSeleccionados_SiAniListNoRespondeYHayCopiaDelSeguimiento_QuedaPendienteConSuNota()
    {
        ConCuenta();
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" };
        var sut = await CrearAsync(anime);
        _tracking.Setup(t => t.GuardarEstadoSeguimientoAsync(1, "COMPLETED", "tok")).ReturnsAsync(false);
        _database.Setup(d => d.ObtenerSeguimientoLocalAsync(1))
            .ReturnsAsync(new SeguimientoLocal { AniListId = 1, Estado = "CURRENT", Puntaje = 80, Progreso = 5 });
        Seleccionar(sut, anime);

        await sut.CategorizarSeleccionadosCommand.ExecuteAsync("COMPLETED");

        _database.Verify(d => d.GuardarSeguimientoLocalAsync(
            It.Is<SeguimientoLocal>(s => s.AniListId == 1 && s.Pendiente && s.Estado == "COMPLETED" && s.Puntaje == 80)), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CategorizarSeleccionados_SiAniListNoRespondeYNoHayCopia_NoInventaUnSeguimiento()
    {
        ConCuenta();
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "CURRENT" };
        var sut = await CrearAsync(anime);
        _tracking.Setup(t => t.GuardarEstadoSeguimientoAsync(1, "COMPLETED", "tok")).ReturnsAsync(false);
        Seleccionar(sut, anime);

        await sut.CategorizarSeleccionadosCommand.ExecuteAsync("COMPLETED");

        // Un seguimiento pendiente inventado (nota 0, sin fechas) borraría en AniList la nota real al enviarse.
        _database.Verify(d => d.GuardarSeguimientoLocalAsync(It.IsAny<SeguimientoLocal>()), Times.Never);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task CategorizarSeleccionados_SoloCambiaLosQueSeVen()
    {
        var bleach = new AnimeItem { AniListId = 1, Titulo = "Bleach", EstadoUsuario = "PLANNING" };
        var naruto = new AnimeItem { AniListId = 2, Titulo = "Naruto", EstadoUsuario = "PLANNING" };
        var sut = await CrearAsync(bleach, naruto);
        Seleccionar(sut, bleach, naruto);
        sut.TextoBusqueda = "Bleach";

        await sut.CategorizarSeleccionadosCommand.ExecuteAsync("COMPLETED");

        bleach.EstadoUsuario.Should().Be("COMPLETED");
        naruto.EstadoUsuario.Should().Be("PLANNING");
        naruto.EstaSeleccionado.Should().BeFalse("al salir del modo selección no debe quedar nada marcado a escondidas");
    }

    [Fact]
    public async Task SeleccionarTodos_MarcaSoloLosQueSeVen()
    {
        var bleach = new AnimeItem { AniListId = 1, Titulo = "Bleach" };
        var naruto = new AnimeItem { AniListId = 2, Titulo = "Naruto" };
        var sut = await CrearAsync(bleach, naruto, new AnimeItem { AniListId = 3, Titulo = "Black Clover" });
        sut.ToggleModoSeleccionCommand.Execute(null);
        sut.TextoBusqueda = "B";

        sut.SeleccionarTodosCommand.Execute(null);

        bleach.EstaSeleccionado.Should().BeTrue();
        naruto.EstaSeleccionado.Should().BeFalse("el filtro lo oculta");
        sut.SeleccionadosTexto.Should().Be("2 seleccionados");
    }

    [Fact]
    public async Task SeleccionadosTexto_CuentaSoloLosSeleccionadosQueSeVen()
    {
        var bleach = new AnimeItem { AniListId = 1, Titulo = "Bleach" };
        var naruto = new AnimeItem { AniListId = 2, Titulo = "Naruto" };
        var sut = await CrearAsync(bleach, naruto);

        Seleccionar(sut, bleach, naruto);
        sut.SeleccionadosTexto.Should().Be("2 seleccionados");

        sut.TextoBusqueda = "Bleach";
        sut.SeleccionadosTexto.Should().Be("1 seleccionados");
    }
}
