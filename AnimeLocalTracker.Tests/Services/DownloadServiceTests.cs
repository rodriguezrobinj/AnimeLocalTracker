using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class DownloadServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly DownloadService _sut;

    public DownloadServiceTests()
    {
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        _sut = new DownloadService(_httpClientFactoryMock.Object);
    }

    [Fact]
    public void EstaDescargando_DeberiaDevolverFalse_CuandoNoHayDescargas()
    {
        // Act
        bool result = _sut.EstaDescargando(10, 1, out double prog);

        // Assert
        result.Should().BeFalse();
        prog.Should().Be(0);
    }

    [Fact]
    public void CancelarDescarga_Inexistente_NoDeberiaLanzarExcepcion()
    {
        // Act
        var act = () => _sut.CancelarDescarga(999, 1);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void CancelarTodas_NoDeberiaLanzarExcepcion_YDebeLimpiarDescargas()
    {
        // Act
        var act = () => _sut.CancelarTodas();

        // Assert
        act.Should().NotThrow();
        _sut.ObtenerDescargasActivas().Should().BeEmpty();
    }
}
