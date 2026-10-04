using AnimeLocalTracker.Controls;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>El aro de descarga único (ficha, Actualizaciones, música): cuándo gira en espera y cuándo muestra el porcentaje.</summary>
public class IndicadorDescargaTests
{
    [Theory]
    [InlineData(0, true)]      // en cola o buscando el enlace
    [InlineData(0.5, false)]
    [InlineData(99.9, false)]
    [InlineData(100, true)]    // bajado pero sin terminar: descifrando (Mega), convirtiendo (música) o moviendo el archivo
    public void EnEspera_SoloSinAvanceOYaAlCien(double progreso, bool esperado) =>
        IndicadorDescarga.EnEspera(progreso).Should().Be(esperado);
}
