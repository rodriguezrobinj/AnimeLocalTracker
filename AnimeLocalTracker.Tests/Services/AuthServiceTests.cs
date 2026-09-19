using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class AuthServiceTests : IDisposable
{
    // Ruta temporal: NUNCA la real (%LocalAppData%\AnimeLocalTrackerData\anilist_token.txt). Antes,
    // CerrarSesion() en una prueba borraba el token del usuario en cada `dotnet test`.
    private readonly string _rutaToken = Path.Combine(Path.GetTempPath(), $"alt_token_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        try { File.Delete(_rutaToken); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ObtenerTokenGuardado_DeberiaDevolverVacio_SiNoExisteArchivo()
    {
        // Arrange
        var sut = new AuthService(_rutaToken);

        // Act
        var token = sut.ObtenerTokenGuardado();

        // Assert
        token.Should().BeEmpty();
        sut.EstaAutenticado().Should().BeFalse();
    }

    [Fact]
    public void ObtenerTokenGuardado_DesencriptaElTokenGuardadoConDpapi()
    {
        // Arrange
        byte[] cifrado = ProtectedData.Protect(Encoding.UTF8.GetBytes("token-de-prueba"), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_rutaToken, cifrado);
        var sut = new AuthService(_rutaToken);

        // Act / Assert
        sut.ObtenerTokenGuardado().Should().Be("token-de-prueba");
        sut.EstaAutenticado().Should().BeTrue();
    }

    [Fact]
    public void CerrarSesion_BorraSoloElArchivoDeTokenIndicado()
    {
        // Arrange
        File.WriteAllBytes(_rutaToken, ProtectedData.Protect(Encoding.UTF8.GetBytes("x"), null, DataProtectionScope.CurrentUser));
        var sut = new AuthService(_rutaToken);

        // Act
        sut.CerrarSesion();

        // Assert
        File.Exists(_rutaToken).Should().BeFalse();
    }

    [Fact]
    public void CerrarSesion_DeberiaEnviarMensajeUsuarioDesconectado()
    {
        // Arrange
        var sut = new AuthService(_rutaToken);
        bool mensajeRecibido = false;

        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this, (r, m) =>
        {
            mensajeRecibido = true;
        });

        // Act
        sut.CerrarSesion();

        // Assert
        sut.Token.Should().BeNull();
        mensajeRecibido.Should().BeTrue();
        
        WeakReferenceMessenger.Default.Unregister<UsuarioDesconectadoMensaje>(this);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5050/", true)]
    [InlineData("http://127.0.0.1:5050", true)]
    [InlineData("http://127.0.0.1:5050/callback", true)]
    [InlineData("http://127.0.0.1:5050.evil.com/callback", false)]
    [InlineData("http://127.0.0.1:5050x/", false)]
    [InlineData("http://127.0.0.1:5051/", false)]
    [InlineData("http://localhost:5050/", true)]
    [InlineData("https://127.0.0.1:5050/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EsOrigenLocal_DeberiaAceptarSoloElListenerExacto(string? valor, bool esperado)
    {
        // Act
        bool resultado = AuthService.EsOrigenLocal(valor);

        // Assert
        resultado.Should().Be(esperado);
    }
}
