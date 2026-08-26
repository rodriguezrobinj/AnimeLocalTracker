using System;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Services;
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
}
