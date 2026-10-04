using System.Threading;
using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class CancelacionTests
{
    [Fact]
    public void Reemplazar_CancelaElAnteriorYDejaUnoNuevoSinCancelar()
    {
        CancellationTokenSource? cts = new();
        var tokenAnterior = cts.Token;

        var nuevo = Cancelacion.Reemplazar(ref cts);

        tokenAnterior.IsCancellationRequested.Should().BeTrue();
        cts.Should().BeSameAs(nuevo);
        nuevo.IsCancellationRequested.Should().BeFalse();
        nuevo.Dispose();
    }

    [Fact]
    public void Reemplazar_SinAnterior_CreaUno()
    {
        CancellationTokenSource? cts = null;

        Cancelacion.Reemplazar(ref cts);

        cts.Should().NotBeNull();
        cts!.Dispose();
    }

    [Fact]
    public void Detener_CancelaYDejaElCampoVacio()
    {
        CancellationTokenSource? cts = new();
        var token = cts.Token;

        Cancelacion.Detener(ref cts);

        token.IsCancellationRequested.Should().BeTrue();
        cts.Should().BeNull();
    }

    [Fact]
    public void Detener_SinNada_NoHaceNada()
    {
        CancellationTokenSource? cts = null;

        Cancelacion.Detener(ref cts);

        cts.Should().BeNull();
    }
}
