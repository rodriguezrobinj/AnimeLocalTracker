using System;
using AnimeLocalTracker;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// El aviso de "error inesperado" del hilo de la interfaz. Su diálogo sigue procesando la cola de la ventana: un error que se
/// repite en cada pasada de diseño volvía a saltar dentro del diálogo y abría otro, hasta agotar la pila (2026-10-17).
/// Cada prueba usa errores con mensaje único: los ya avisados se recuerdan durante toda la sesión.
/// </summary>
public class AvisoErrorInesperadoTests
{
    private static InvalidOperationException Error() => new("fallo " + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnErrorMientrasElAvisoEstaAbierto_NoAbreOtroDentro()
    {
        int abiertos = 0;
        bool anidadoMostrado = true;

        App.MostrarAvisoSinAnidar(Error(), () =>
        {
            abiertos++;
            anidadoMostrado = App.MostrarAvisoSinAnidar(Error(), () => abiertos++);
        });

        abiertos.Should().Be(1);
        anidadoMostrado.Should().BeFalse();
    }

    [Fact]
    public void TrasCerrarElAviso_OtroErrorSeAvisa()
    {
        App.MostrarAvisoSinAnidar(Error(), () => { }).Should().BeTrue();
        App.MostrarAvisoSinAnidar(Error(), () => { }).Should().BeTrue();
    }

    [Fact]
    public void ElMismoErrorTrasCerrarElAviso_NoVuelveAAbrirlo()
    {
        // Un error de diseño que se repite en cada fotograma: si volviera a avisar al cerrar, el usuario no saldría del bucle.
        var error = Error();
        App.MostrarAvisoSinAnidar(error, () => { }).Should().BeTrue();
        App.MostrarAvisoSinAnidar(new InvalidOperationException(error.Message), () => { }).Should().BeFalse();
    }

    [Fact]
    public void SiElAvisoFalla_NoQuedaBloqueadoParaSiempre()
    {
        var accion = () => App.MostrarAvisoSinAnidar(Error(), () => throw new InvalidOperationException("sin ventana"));
        accion.Should().Throw<InvalidOperationException>();

        App.MostrarAvisoSinAnidar(Error(), () => { }).Should().BeTrue();
    }
}
