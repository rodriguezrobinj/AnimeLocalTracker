using System;
using System.Collections.Generic;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

public class SubtitulosSolapadosResolverTests
{
    private static SubtitleCue Cue(double inicioS, double finS, string texto) =>
        new(TimeSpan.FromSeconds(inicioS), TimeSpan.FromSeconds(finS), texto);

    [Fact]
    public void SinCues_DevuelveAmbosNulos()
    {
        var (abajo, arriba) = SubtitulosSolapadosResolver.Resolver(new List<SubtitleCue>(), TimeSpan.FromSeconds(5));

        abajo.Should().BeNull();
        arriba.Should().BeNull();
    }

    [Fact]
    public void NingunaLineaActivaEnEseInstante_DevuelveAmbosNulos()
    {
        var cues = new List<SubtitleCue> { Cue(1, 2, "Uno") };

        var (abajo, arriba) = SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(5));

        abajo.Should().BeNull();
        arriba.Should().BeNull();
    }

    [Fact]
    public void UnaSolaLineaActiva_VaAbajoYArribaQuedaVacio()
    {
        var cues = new List<SubtitleCue> { Cue(1, 4, "Hola") };

        var (abajo, arriba) = SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(2));

        abajo.Should().Be("Hola");
        arriba.Should().BeNull();
    }

    [Fact]
    public void DosLineasSolapadas_LaQueEmpiezaAntesVaAbajoYLaNuevaArriba()
    {
        var cues = new List<SubtitleCue>
        {
            Cue(1, 5, "Personaje A"),
            Cue(3, 6, "Personaje B") // empieza mientras A sigue sonando
        };

        var (abajo, arriba) = SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(4));

        abajo.Should().Be("Personaje A");
        arriba.Should().Be("Personaje B");
    }

    [Fact]
    public void TresOMasLineasActivas_SoloSeQuedanLasDosMasRecientes()
    {
        var cues = new List<SubtitleCue>
        {
            Cue(0, 10, "Vieja"),
            Cue(2, 10, "Media"),
            Cue(4, 10, "Nueva")
        };

        var (abajo, arriba) = SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(5));

        abajo.Should().Be("Media");
        arriba.Should().Be("Nueva");
    }

    [Fact]
    public void UsaLimitesMedioAbiertos_InicioIncluidoFinExcluido()
    {
        var cues = new List<SubtitleCue> { Cue(1, 2, "Línea") };

        SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(1)).Abajo.Should().Be("Línea");
        SubtitulosSolapadosResolver.Resolver(cues, TimeSpan.FromSeconds(2)).Abajo.Should().BeNull();
    }
}
