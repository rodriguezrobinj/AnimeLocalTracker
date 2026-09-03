using System.Collections.Generic;
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
/// DEV-06: DescargasViewModel (cola de descargas, pausa/reanudación, cancelación
/// y navegación). El método Receive (progreso) depende del Dispatcher de WPF
/// (Application.Current es null en tests headless) y se cubre vía integración manual.
/// </summary>
public class DescargasViewModelTests
{
    private readonly Mock<IDownloadService> _downloadServiceMock = new();

    private DescargasViewModel CreateSut(List<DescargaItem>? activas = null)
    {
        _downloadServiceMock
            .Setup(d => d.ObtenerDescargasActivas())
            .Returns(activas ?? new List<DescargaItem>());
        return new DescargasViewModel(_downloadServiceMock.Object);
    }

    private static DescargaItem CrearItem(int aniListId, int episodio, bool pausada = false, bool descargando = true)
    {
        return new DescargaItem
        {
            AniListId = aniListId,
            AnimeTitulo = $"Anime {aniListId}",
            NumeroEpisodio = episodio,
            IsDownloading = descargando,
            IsPaused = pausada
        };
    }

    [Fact]
    public void CargarDescargas_DeberiaPoblarColaYCalcularConteos()
    {
        // Arrange
        var activas = new List<DescargaItem>
        {
            CrearItem(1, 1),
            CrearItem(1, 2, pausada: true)
        };

        // Act
        var sut = CreateSut(activas);

        // Assert
        sut.ColaDescargas.Should().HaveCount(2);
        sut.ConteoActivas.Should().Be(1); // solo la no pausada
        sut.TieneDescargas.Should().BeTrue();
        sut.TodasPausadas.Should().BeFalse();
    }

    [Fact]
    public void CargarDescargas_SinActivas_DeberiaDejarTodoVacio()
    {
        // Act
        var sut = CreateSut();

        // Assert
        sut.ColaDescargas.Should().BeEmpty();
        sut.ConteoActivas.Should().Be(0);
        sut.TieneDescargas.Should().BeFalse();
        sut.TodasPausadas.Should().BeFalse();
    }

    [Fact]
    public void CancelarDescarga_DeberiaLlamarAlServicioYRemoverItem()
    {
        // Arrange
        var item = CrearItem(42, 3);
        var sut = CreateSut(new List<DescargaItem> { item });

        // Act
        sut.CancelarDescargaCommand.Execute(item);

        // Assert
        _downloadServiceMock.Verify(d => d.CancelarDescarga(42, 3), Times.Once);
        sut.ColaDescargas.Should().BeEmpty();
        sut.TieneDescargas.Should().BeFalse();
    }

    [Fact]
    public void AlternarPausaDescarga_DeberiaPausarYReanudar()
    {
        // Arrange
        var item = CrearItem(7, 1);
        var sut = CreateSut(new List<DescargaItem> { item });

        // Act 1: pausar
        sut.AlternarPausaDescargaCommand.Execute(item);

        // Assert 1
        item.IsPaused.Should().BeTrue();
        _downloadServiceMock.Verify(d => d.PausarDescarga(7, 1), Times.Once);

        // Act 2: reanudar
        sut.AlternarPausaDescargaCommand.Execute(item);

        // Assert 2
        item.IsPaused.Should().BeFalse();
        _downloadServiceMock.Verify(d => d.ReanudarDescarga(7, 1), Times.Once);
    }

    [Fact]
    public void AlternarPausaTodas_DeberiaPausarTodoYLuegoReanudar()
    {
        // Arrange
        var sut = CreateSut(new List<DescargaItem> { CrearItem(1, 1), CrearItem(2, 1) });

        // Act 1: pausar todas (hay alguna sin pausar)
        sut.AlternarPausaTodasCommand.Execute(null);

        // Assert 1
        sut.ColaDescargas.Should().OnlyContain(d => d.IsPaused);
        sut.TodasPausadas.Should().BeTrue();
        _downloadServiceMock.Verify(d => d.PausarTodas(), Times.Once);

        // Act 2: reanudar todas (todas pausadas)
        sut.AlternarPausaTodasCommand.Execute(null);

        // Assert 2
        sut.ColaDescargas.Should().OnlyContain(d => !d.IsPaused);
        _downloadServiceMock.Verify(d => d.ReanudarTodas(), Times.Once);
    }

    [Fact]
    public void CancelarTodas_DeberiaVaciarLaCola()
    {
        // Arrange
        var sut = CreateSut(new List<DescargaItem> { CrearItem(1, 1), CrearItem(2, 1) });

        // Act
        sut.CancelarTodasCommand.Execute(null);

        // Assert
        _downloadServiceMock.Verify(d => d.CancelarTodas(), Times.Once);
        sut.ColaDescargas.Should().BeEmpty();
        sut.TieneDescargas.Should().BeFalse();
    }

    [Fact]
    public void Volver_DeberiaEnviarMensajeDeNavegacionAGaleria()
    {
        // Arrange
        var sut = CreateSut();
        bool mensajeRecibido = false;
        WeakReferenceMessenger.Default.Register<NavegarMensaje_Galeria>(this, (r, m) => mensajeRecibido = true);

        try
        {
            // Act
            sut.VolverCommand.Execute(null);

            // Assert
            mensajeRecibido.Should().BeTrue();
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<NavegarMensaje_Galeria>(this);
        }
    }
}
