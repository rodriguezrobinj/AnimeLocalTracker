using System;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class AppLoggerTests
{
    [Fact]
    public void Sanitizar_DeberiaReemplazarLaCarpetaDeDatosYElPerfil()
    {
        // Arrange: rutas reales del entorno de ejecución
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(localApp) || string.IsNullOrEmpty(perfil)) return;

        // Act
        string resultado = AppLogger.Sanitizar($"{localApp}\\AnimeLocalTrackerData\\Backups\\x.db y {perfil}\\Desktop\\f.mkv");

        // Assert (SEC-12): sin rutas completas del usuario en el log
        resultado.Should().NotContain(localApp);
        resultado.Should().NotContain(perfil);
        resultado.Should().Contain("<datos>");
        resultado.Should().Contain("<perfil>");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitizar_ConTextoVacio_DeberiaDevolverVacio(string? texto)
    {
        AppLogger.Sanitizar(texto).Should().BeEmpty();
    }

    [Fact]
    public void Sanitizar_SinRutasConocidas_DeberiaDejarElTextoIntacto()
    {
        AppLogger.Sanitizar("Sincronizado AniListId=123 hasta episodio 5.").Should()
            .Be("Sincronizado AniListId=123 hasta episodio 5.");
    }
}
