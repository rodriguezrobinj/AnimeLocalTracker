using System;
using System.Collections.Generic;
using System.Diagnostics;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// La prioridad del proceso es estado global: estas pruebas no pueden correr en paralelo con otras
/// que la toquen, y usan un proceso "falso" para no cambiar la prioridad real del runner de tests.
/// </summary>
[Collection("PrioridadReproduccion")]
public class PrioridadReproduccionTests : IDisposable
{
    private ProcessPriorityClass _actual = ProcessPriorityClass.Normal;
    private readonly List<ProcessPriorityClass> _escrituras = new();

    public PrioridadReproduccionTests()
    {
        PrioridadReproduccion.ReiniciarParaPruebas();
        PrioridadReproduccion.Leer = () => _actual;
        PrioridadReproduccion.Escribir = p => { _actual = p; _escrituras.Add(p); };
    }

    public void Dispose()
    {
        PrioridadReproduccion.ReiniciarParaPruebas();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Elevar_ProcesoEnNormal_SubeAPorEncimaDeLoNormal()
    {
        PrioridadReproduccion.Elevar();

        _actual.Should().Be(ProcessPriorityClass.AboveNormal);
    }

    [Fact]
    public void Restaurar_DevuelveLaPrioridadANormal()
    {
        PrioridadReproduccion.Elevar();

        PrioridadReproduccion.Restaurar();

        _actual.Should().Be(ProcessPriorityClass.Normal);
    }

    [Fact]
    public void VariosReproductores_SoloRestauraElUltimo()
    {
        PrioridadReproduccion.Elevar();
        PrioridadReproduccion.Elevar();

        PrioridadReproduccion.Restaurar();
        _actual.Should().Be(ProcessPriorityClass.AboveNormal);

        PrioridadReproduccion.Restaurar();
        _actual.Should().Be(ProcessPriorityClass.Normal);
        _escrituras.Should().Equal(ProcessPriorityClass.AboveNormal, ProcessPriorityClass.Normal);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.High)]
    [InlineData(ProcessPriorityClass.BelowNormal)]
    [InlineData(ProcessPriorityClass.Idle)]
    public void PrioridadFijadaPorElUsuario_SeRespetaYNoSeRestaura(ProcessPriorityClass delUsuario)
    {
        _actual = delUsuario;

        PrioridadReproduccion.Elevar();
        PrioridadReproduccion.Restaurar();

        _escrituras.Should().BeEmpty();
        _actual.Should().Be(delUsuario);
    }

    [Fact]
    public void RestaurarSinElevar_NoHaceNada()
    {
        PrioridadReproduccion.Restaurar();

        _escrituras.Should().BeEmpty();
    }

    [Fact]
    public void ErrorAlCambiarPrioridad_NoPropagaExcepcion()
    {
        PrioridadReproduccion.Escribir = _ => throw new UnauthorizedAccessException("sin permiso");

        var act = () =>
        {
            PrioridadReproduccion.Elevar();
            PrioridadReproduccion.Restaurar();
        };

        act.Should().NotThrow();
    }
}
