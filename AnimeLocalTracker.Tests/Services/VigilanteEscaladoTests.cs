using System;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Cuándo se da por hecho que el escalado inteligente no da abasto: cuatro ventanas de 2 s seguidas perdiendo 3 fotogramas o más.</summary>
public class VigilanteEscaladoTests
{
    private static TimeSpan S(double segundos) => TimeSpan.FromSeconds(segundos);

    [Fact]
    public void SinPerdidas_NuncaApaga()
    {
        var vigilante = new VigilanteEscalado();

        for (int i = 0; i <= 60; i++) vigilante.Anotar(0, S(i * 0.25)).Should().BeFalse();
    }

    [Fact]
    public void CuatroVentanasSeguidasPerdiendo_Apaga_YNoAntes()
    {
        var vigilante = new VigilanteEscalado();
        vigilante.Anotar(0, S(0)).Should().BeFalse();

        vigilante.Anotar(18, S(2)).Should().BeFalse("primera ventana mala");
        vigilante.Anotar(36, S(4)).Should().BeFalse("segunda");
        vigilante.Anotar(54, S(6)).Should().BeFalse("tercera");
        vigilante.Anotar(72, S(8)).Should().BeTrue("cuarta seguida: 9 por segundo durante 8 s, lo medido con el nivel que no aguanta");
    }

    [Fact]
    public void UnTironSuelto_NoApaga()
    {
        // Un salto o la compilación de los shaders al abrir: dos ventanas malas, una buena y otras dos malas.
        var vigilante = new VigilanteEscalado();
        vigilante.Anotar(0, S(0));

        vigilante.Anotar(10, S(2)).Should().BeFalse();
        vigilante.Anotar(20, S(4)).Should().BeFalse();
        vigilante.Anotar(21, S(6)).Should().BeFalse("ventana buena: solo 1 perdido");
        vigilante.Anotar(31, S(8)).Should().BeFalse();
        vigilante.Anotar(41, S(10)).Should().BeFalse("la racha empezó de nuevo");
    }

    [Fact]
    public void LasMuestrasDentroDeUnaVentana_NoCuentanComoVentanas()
    {
        // El reproductor pregunta cada 250 ms: cuatro preguntas seguidas no son cuatro ventanas.
        var vigilante = new VigilanteEscalado();
        vigilante.Anotar(0, S(0));

        for (int i = 1; i <= 7; i++) vigilante.Anotar(i * 5, S(i * 0.25)).Should().BeFalse();
    }

    [Fact]
    public void SiElContadorBaja_EmpiezaDeNuevo()
    {
        // Archivo nuevo: libmpv pone el contador a cero. No se cuenta en negativo ni se arrastra la racha.
        var vigilante = new VigilanteEscalado();
        vigilante.Anotar(0, S(0));
        vigilante.Anotar(18, S(2));
        vigilante.Anotar(36, S(4));
        vigilante.Anotar(54, S(6));

        vigilante.Anotar(2, S(8)).Should().BeFalse("el contador bajó");
        vigilante.Anotar(20, S(10)).Should().BeFalse("primera mala de la racha nueva");
    }

    [Fact]
    public void TrasReiniciar_EmpiezaDeNuevo()
    {
        var vigilante = new VigilanteEscalado();
        vigilante.Anotar(0, S(0));
        vigilante.Anotar(18, S(2));
        vigilante.Anotar(36, S(4));
        vigilante.Anotar(54, S(6));

        vigilante.Reiniciar();

        vigilante.Anotar(72, S(8)).Should().BeFalse("primera muestra tras reiniciar");
        vigilante.Anotar(90, S(10)).Should().BeFalse();
    }
}
