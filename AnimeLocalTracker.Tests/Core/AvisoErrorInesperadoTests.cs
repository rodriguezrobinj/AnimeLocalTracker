using AnimeLocalTracker;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>
/// El aviso de "error inesperado" del hilo de la interfaz. Su diálogo sigue procesando la cola de la ventana: un error que se
/// repite en cada pasada de diseño volvía a saltar dentro del diálogo y abría otro, hasta agotar la pila (2026-10-17).
/// </summary>
public class AvisoErrorInesperadoTests
{
    [Fact]
    public void UnErrorMientrasElAvisoEstaAbierto_NoAbreOtroDentro()
    {
        int abiertos = 0;
        bool anidadoMostrado = true;

        App.MostrarAvisoSinAnidar(() =>
        {
            abiertos++;
            anidadoMostrado = App.MostrarAvisoSinAnidar(() => abiertos++);
        });

        abiertos.Should().Be(1);
        anidadoMostrado.Should().BeFalse();
    }

    [Fact]
    public void TrasCerrarElAviso_ElSiguienteErrorSeAvisa()
    {
        App.MostrarAvisoSinAnidar(() => { }).Should().BeTrue();
        App.MostrarAvisoSinAnidar(() => { }).Should().BeTrue();
    }

    [Fact]
    public void SiElAvisoFalla_NoQuedaBloqueadoParaSiempre()
    {
        var accion = () => App.MostrarAvisoSinAnidar(() => throw new System.InvalidOperationException("sin ventana"));
        accion.Should().Throw<System.InvalidOperationException>();

        App.MostrarAvisoSinAnidar(() => { }).Should().BeTrue();
    }
}
