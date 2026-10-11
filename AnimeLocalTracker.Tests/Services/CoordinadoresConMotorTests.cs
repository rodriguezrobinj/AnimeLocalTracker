using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Tests.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Los coordinadores del reproductor contra el contrato del motor (antes recibían el Player de Flyleaf y no se podían probar).</summary>
public class CoordinadoresConMotorTests
{
    private static async Task EsperarHasta(Func<bool> condicion, int ms = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    [Fact]
    public void Salto_ConElVideoAbierto_SeAplicaEnElMomento()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 120, () => true, preciso: true);

        motor.Saltos.Should().Equal((120d, true));
    }

    [Theory]
    [InlineData(EstadoMotor.Abriendo, true)]
    [InlineData(EstadoMotor.Detenido, true)]
    [InlineData(EstadoMotor.Reproduciendo, false)] // aún no terminó de abrir, aunque el estado diga otra cosa
    public void Salto_MientrasElVideoSeAbre_SeGuardaParaCuandoAbra(EstadoMotor estado, bool aperturaCompleta)
    {
        var motor = new MotorDePrueba { Estado = estado };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 300, () => aperturaCompleta);

        motor.Saltos.Should().BeEmpty("saltar mientras se crea el decodificador dejaba la pantalla en negro");
        saltos.ConsumirSeekPendienteAlAbrir().Should().Be(300);
        saltos.ConsumirSeekPendienteAlAbrir().Should().BeNull("se consume una vez");
    }

    [Fact]
    public async Task VariosSaltosSeguidos_AplicaElPrimeroYLuegoSoloElUltimo()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 10, () => true);
        saltos.SolicitarSeek(motor, 20, () => true);
        saltos.SolicitarSeek(motor, 30, () => true);

        await EsperarHasta(() => motor.Saltos.Count == 2);
        motor.Saltos.Should().Equal((10d, false), (30d, false));
    }

    [Fact]
    public void Salto_SinMotorOConElMotorDesechado_NoHaceNada()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        motor.Dispose();
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(null, 10, () => true);
        saltos.SolicitarSeek(motor, 10, () => true);

        motor.Saltos.Should().BeEmpty();
    }

    [Fact]
    public void Volumen_AlSubirloEstandoEnSilencio_QuitaElSilencio()
    {
        var motor = new MotorDePrueba { SilencioPuesto = true };
        var volumen = new PlaybackVolumeCoordinator();

        volumen.AplicarVolumen(motor, 40, estabaMuteado: true).Should().BeTrue();
        motor.VolumenPuesto.Should().Be(40);
        motor.SilencioPuesto.Should().BeFalse();

        volumen.AplicarVolumen(motor, 0, estabaMuteado: true).Should().BeFalse("bajar a cero no quita el silencio");
        volumen.AplicarVolumen(null, 40, estabaMuteado: true).Should().BeFalse();
    }

    [Fact]
    public void Volumen_SilenciarYQuitarElSilencio()
    {
        var motor = new MotorDePrueba();
        var volumen = new PlaybackVolumeCoordinator();

        volumen.Mutear(motor);
        motor.SilencioPuesto.Should().BeTrue();

        volumen.Desmutear(motor, 70);
        motor.SilencioPuesto.Should().BeFalse();
        motor.VolumenPuesto.Should().Be(70);
    }

    [Fact]
    public void Subtitulos_PistaPorDefecto_PrefiereElIdiomaYNoEligeSiElMotorYaTieneUna()
    {
        var motor = new MotorDePrueba();
        motor.Subtitulos.Add(new PistaSubtitulosMotor(2, "en", null, false, true, null));
        motor.Subtitulos.Add(new PistaSubtitulosMotor(3, "es", "Signs & Songs", false, true, null));
        motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "es", null, false, true, null));
        var subtitulos = new SubtitleCoordinator();

        subtitulos.DebenHabilitarsePorDefecto(motor, true).Should().BeTrue();
        subtitulos.DebenHabilitarsePorDefecto(motor, false).Should().BeFalse();
        subtitulos.DebenHabilitarsePorDefecto(new MotorDePrueba(), true).Should().BeFalse("sin pistas no hay nada que encender");
        subtitulos.PistaPorDefecto(motor, "es")!.Indice.Should().Be(4, "la de diálogo, no la de solo carteles");

        subtitulos.SeleccionarPista(motor, motor.Subtitulos[0]);
        motor.SubtitulosDelMotor.Should().Be(motor.Subtitulos[0]);
        subtitulos.PistaPorDefecto(motor, "es").Should().BeNull("el motor ya tiene una pista abierta");

        subtitulos.Deshabilitar(motor);
        motor.SubtitulosDelMotor.Should().BeNull();
    }

    [Fact]
    public void LecturaAdelantada_SePideUnaVezPorEpisodioYSoloCercaDelFinal()
    {
        using var navegador = new EpisodeNavigator();
        int pedidas = 0;
        void Precalentar(string ruta, CancellationToken ct) => pedidas++;

        navegador.ConsiderarPrecarga(0.50, "C:\\Anime\\Ep02.mkv", Precalentar);
        pedidas.Should().Be(0);
        navegador.ConsiderarPrecarga(0.96, "C:\\Anime\\Ep02.mkv", Precalentar);
        navegador.ConsiderarPrecarga(0.97, "C:\\Anime\\Ep02.mkv", Precalentar);
        pedidas.Should().Be(1);

        navegador.CancelarPrecargaYReiniciar();
        navegador.ConsiderarPrecarga(0.96, "C:\\Anime\\Ep03.mkv", Precalentar);
        pedidas.Should().Be(2);
    }
}
