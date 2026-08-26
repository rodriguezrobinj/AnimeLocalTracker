using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using AnimeLocalTracker.Core.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class AgregarAnimeViewModelTests : IDisposable
{
    private readonly Mock<IAnimeTrackingService> _trackingMock = new();
    private readonly Mock<IDatabaseService> _dbMock = new();
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly Mock<IDialogService> _dialogMock = new();
    private readonly string _tempFolder;

    public AgregarAnimeViewModelTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "AnimeLocalTracker_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
        _settingsMock.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_tempFolder);

        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        _trackingMock.Setup(t => t.ObtenerAnimesTendenciaAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniListMedia>
            {
                new()
                {
                    Id = 1,
                    Title = new AniListTitle { Romaji = "Frieren", English = "Frieren: Beyond Journey's End" },
                    Status = "FINISHED",
                    Episodes = 28
                }
            });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, true);
            }
        }
        catch { }
    }

    private AgregarAnimeViewModel CreateSut()
    {
        return new AgregarAnimeViewModel(
            _trackingMock.Object,
            _dbMock.Object,
            _settingsMock.Object,
            _dialogMock.Object);
    }

    [Fact]
    public async Task CargarTendencias_DeberiaLlenarResultadosYActualizarEstado()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.CargarTendenciasAsync();

        // Assert
        sut.Resultados.Should().HaveCount(1);
        sut.Resultados[0].TituloPrincipal.Should().Be("Frieren");
        sut.MostrandoTendencias.Should().BeTrue();
        sut.TituloSeccion.Should().Be("Tendencias de la temporada");
        sut.BusquedaSinResultados.Should().BeFalse();
    }

    [Fact]
    public void LimpiarBusqueda_DeberiaResetearTextoYCargarTendencias()
    {
        // Arrange
        var sut = CreateSut();
        sut.TextoBusqueda = "Solo Leveling";

        // Act
        sut.LimpiarBusquedaCommand.Execute(null);

        // Assert
        sut.TextoBusqueda.Should().BeEmpty();
    }

    [Fact]
    public async Task AñadirAnime_AnimeNuevo_DeberiaGuardarEnDbYNotificar()
    {
        // Arrange
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem>());
        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia
            {
                Id = 100,
                Title = new AniListTitle { Romaji = "Sousou no Frieren" },
                Episodes = 28,
                Status = "FINISHED"
            }
        };

        AnimeAñadidoMensaje? mensajeRecibido = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<AgregarAnimeViewModelTests, AnimeAñadidoMensaje>(this, (r, m) =>
        {
            mensajeRecibido = m;
        });

        // Act
        await sut.AñadirAnimeAsync(item);

        // Assert
        item.EstaEnBiblioteca.Should().BeTrue();
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 100 && a.Titulo == "Sousou no Frieren")), Times.Once);
        mensajeRecibido.Should().NotBeNull();
        mensajeRecibido!.NuevoAnime.AniListId.Should().Be(100);

        GC.KeepAlive(this);
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    [Fact]
    public async Task AñadirAnime_AnimeExistente_DeberiaMostrarDialogoYNoDuplicar()
    {
        // Arrange
        var animeExistente = new AnimeItem { AniListId = 200, Titulo = "Dungeon Meshi" };
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { animeExistente });

        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia
            {
                Id = 200,
                Title = new AniListTitle { Romaji = "Dungeon Meshi" }
            }
        };

        // Act
        await sut.AñadirAnimeAsync(item);

        // Assert
        item.EstaEnBiblioteca.Should().BeTrue();
        _dbMock.Verify(d => d.GuardarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
        _dialogMock.Verify(d => d.MostrarDialogoAsync("Anime Existente", It.IsAny<string>(), false, "InformationOutline", "#FF9800"), Times.Once);
    }

    [Fact]
    public async Task VerEnBiblioteca_DeberiaEnviarNavegarMensajeDetalle()
    {
        // Arrange
        var animeExistente = new AnimeItem { AniListId = 300, Titulo = "Bleach" };
        _dbMock.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { animeExistente });

        var sut = CreateSut();
        var item = new AnimeBusquedaItem
        {
            Media = new AniListMedia { Id = 300, Title = new AniListTitle { Romaji = "Bleach" } }
        };

        NavegarMensaje_Detalle? mensajeDetalle = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Detalle>(this, (r, m) =>
        {
            mensajeDetalle = m;
        });

        // Act
        await sut.VerEnBibliotecaAsync(item);

        // Assert
        mensajeDetalle.Should().NotBeNull();
        mensajeDetalle!.AnimeSeleccionado.AniListId.Should().Be(300);

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
