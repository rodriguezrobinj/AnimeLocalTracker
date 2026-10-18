using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Desfase de subtítulos y audio (teclas Z/X y J/K): se aplica al motor y se recuerda por capítulo.</summary>
public sealed class ReproductorDesfaseTests : IDisposable
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly MotorDePrueba _motor = new() { Duracion = TimeSpan.FromSeconds(1420) };
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_desfase_" + Guid.NewGuid().ToString("N"));
    private readonly string _ruta;

    public ReproductorDesfaseTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ruta = Path.Combine(_carpeta, "Ep05.mkv");
        File.WriteAllText(_ruta, "no es un video: solo tiene que existir");
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private ReproductorViewModel CrearSut() => new(
        _db.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), fabricaMotor: _ => _motor);

    [Fact]
    public async Task CadaPulsacion_MueveCincuentaMs_YLlegaAlMotor()
    {
        using var sut = CrearSut();
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        await sut.TareaDesfase;

        sut.RetrasarSubtitulosCommand.Execute(null);
        sut.RetrasarSubtitulosCommand.Execute(null);
        sut.AdelantarAudioCommand.Execute(null);

        _motor.DesfaseSubtitulosPuesto.Should().Be(100);
        _motor.DesfaseAudioPuesto.Should().Be(-50);
    }

    [Fact]
    public async Task NoPasaDeDiezSegundos()
    {
        using var sut = CrearSut();
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        await sut.TareaDesfase;

        for (int i = 0; i < 250; i++) sut.AdelantarSubtitulosCommand.Execute(null);

        _motor.DesfaseSubtitulosPuesto.Should().Be(-ReproductorViewModel.DesfaseMaximoMs);
    }

    [Fact]
    public async Task UnCambio_SeGuardaEnEseCapitulo()
    {
        using var sut = CrearSut();
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        await sut.TareaDesfase;

        sut.RetrasarSubtitulosCommand.Execute(null);
        sut.RetrasarAudioCommand.Execute(null);
        await Task.Delay(900);

        _db.Verify(d => d.GuardarDesfaseEpisodioAsync(101, 5, 50, 50), Times.Once);
    }

    [Fact]
    public async Task AlAbrir_RecuperaLoGuardado_YOtroCapituloSinNadaVuelveACero()
    {
        _db.Setup(d => d.ObtenerDesfaseEpisodioAsync(101, 5))
            .ReturnsAsync(new DesfaseEpisodio { AniListId = 101, NumeroEpisodio = 5, SubtitulosMs = 250, AudioMs = -100 });
        using var sut = CrearSut();

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        await sut.TareaDesfase;
        _motor.DesfaseSubtitulosPuesto.Should().Be(250);
        _motor.DesfaseAudioPuesto.Should().Be(-100);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 6);
        await sut.TareaDesfase;
        _motor.DesfaseSubtitulosPuesto.Should().Be(0);
        _motor.DesfaseAudioPuesto.Should().Be(0);
        _db.Verify(d => d.GuardarDesfaseEpisodioAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never,
            "leer lo guardado no es un cambio del usuario");
    }
}
