using AnimeLocalTracker;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class CompositionRootTests
{
    [Fact]
    public void ConfigureServices_IFileScannerService_DeberiaRegistrarUnaSolaImplementacion()
    {
        // Arrange
        var services = new ServiceCollection();
        App.ConfigureServices(services);

        // Act
        using var provider = services.BuildServiceProvider();
        var implementaciones = provider.GetServices<IFileScannerService>().ToList();

        // Assert
        // Un doble registro en MS.DI se resuelve en silencio a favor del último:
        // este test evita que vuelva a aparecer un escáner duplicado (ARC-001).
        implementaciones.Should().HaveCount(1);
        provider.GetService<IFileScannerService>().Should().NotBeNull();
    }
}
