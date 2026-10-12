using System;
using System.Collections.Generic;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>Dónde se pinta el video de libmpv. Si la biblioteca no está descargada (tools\Get-Libmpv.ps1), las pruebas no hacen nada.</summary>
[Collection("WpfSmoke")]
public class SuperficieMpvTests
{
    private const string VariableSinOpenGl = "ANIMELOCALTRACKER_SIN_OPENGL";
    private readonly WpfHostFixture _host;

    public SuperficieMpvTests(WpfHostFixture host) => _host = host;

    [Fact]
    public void SiElLienzoNoArranca_LaSuperficiePintaPorSoftware()
    {
        // Máquina virtual, escritorio remoto o controlador antiguo: antes el episodio se reabría con el motor clásico.
        if (!LibMpv.Disponible) return;
        string? antes = Environment.GetEnvironmentVariable(VariableSinOpenGl);
        Environment.SetEnvironmentVariable(VariableSinOpenGl, "1");
        MotorMpv? motor = null;
        var sinSalida = new List<string>();
        try
        {
            _host.Ejecutar(() =>
            {
                motor = MotorMpv.Crear(new AjustesInicialesMotor(false, null, []), sinSonido: true)!;
                motor.SinSalida += sinSalida.Add;

                SuperficieMpv.Para(motor).Should().NotBeNull();

                motor.PintaPorSoftware.Should().BeTrue();
            });
            _host.Ejecutar(() => { }); // el aviso de «sin salida» se da en diferido: si lo hubiera, ya habría llegado

            sinSalida.Should().BeEmpty("hay dónde pintar: el reproductor no tiene que enterarse de nada");
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableSinOpenGl, antes);
            if (motor != null) _host.Ejecutar(motor.Dispose);
        }
    }
}
