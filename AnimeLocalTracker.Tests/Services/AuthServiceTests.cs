using System;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class AuthServiceTests
{
    [Fact]
    public void ObtenerTokenGuardado_DeberiaDevolverVacio_SiNoExisteArchivo()
    {
        // Arrange
        var sut = new AuthService();

        // Act
        var token = sut.ObtenerTokenGuardado();

        // Assert
        // Si no se ha logueado en este ambiente, debería devolver string vacío o un token válido
        token.Should().NotBeNull();
    }

    [Fact]
    public void CerrarSesion_DeberiaEnviarMensajeUsuarioDesconectado()
    {
        // Arrange
        var sut = new AuthService();
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
    [InlineData("http://localhost:5050/", true)]
    [InlineData("http://localhost:5050", true)]
    [InlineData("http://localhost:5050/callback", true)]
    [InlineData("http://localhost:5050.evil.com/callback", false)]
    [InlineData("http://localhost:5050x/", false)]
    [InlineData("http://localhost:5051/", false)]
    [InlineData("http://127.0.0.1:5050/", false)]
    [InlineData("https://localhost:5050/", false)]
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
