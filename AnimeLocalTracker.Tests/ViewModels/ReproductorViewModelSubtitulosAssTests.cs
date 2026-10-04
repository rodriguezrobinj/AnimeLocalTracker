using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Estado del dibujo de subtítulos ASS en el ViewModel, con el dibujante simulado (no se toca FFmpeg).</summary>
public class ReproductorViewModelSubtitulosAssTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly Mock<ISubtitleAssRenderer> _dibujante = new();
    private readonly AppSettings _config = new();

    private ReproductorViewModel Crear()
    {
        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(_config);
        return new ReproductorViewModel(_db.Object, _tracking.Object, _auth.Object, null, _ajustes.Object, subtitleAssRenderer: _dibujante.Object);
    }

    private void AbrirDevuelve(Task<bool> resultado) =>
        _dibujante.Setup(d => d.AbrirAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .Returns(resultado);

    [Fact]
    public void IniciarDibujoAss_CuandoElDibujanteAbre_ActivaElModoAss()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_SiElDibujanteFalla_SeQuedaEnTextoPlano()
    {
        AbrirDevuelve(Task.FromResult(false));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_SiSeCambiaDePistaMientrasAbre_ElResultadoTardioNoActivaElModo()
    {
        var apertura = new TaskCompletionSource<bool>();
        AbrirDevuelve(apertura.Task);
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.CerrarDibujoAss(); // cambio de episodio o de pista
        apertura.SetResult(true);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void UsarMiEstiloEnAss_AlEncenderlo_VuelveAlTextoPlanoSinCerrarElDibujante_YSeGuarda()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);
        _dibujante.Invocations.Clear();

        sut.UsarMiEstiloEnAss = true;

        sut.SubtitulosAssActivo.Should().BeFalse();
        _dibujante.Verify(d => d.Cerrar(), Times.Never, "volver al estilo original debe ser inmediato");
        _config.UsarMiEstiloEnAss.Should().BeTrue();
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(_config), Times.Once);

        sut.UsarMiEstiloEnAss = false;

        sut.SubtitulosAssActivo.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void Constructor_LeeElAjusteGuardado()
    {
        _config.UsarMiEstiloEnAss = true;

        var sut = Crear();

        sut.UsarMiEstiloEnAss.Should().BeTrue();
        sut.Dispose();
    }

    [Fact]
    public void IniciarDibujoAss_ConUsarMiEstiloEncendido_AbreElDibujantePeroNoActivaElModo()
    {
        _config.UsarMiEstiloEnAss = true;
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();

        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);

        sut.SubtitulosAssActivo.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void NotificarFalloDibujoAss_ApagaElModoYCierraElDibujante()
    {
        AbrirDevuelve(Task.FromResult(true));
        var sut = Crear();
        sut.IniciarDibujoAss(@"C:\video.mkv", 0, 1280, 720);
        _dibujante.Invocations.Clear();

        sut.NotificarFalloDibujoAss();

        sut.SubtitulosAssActivo.Should().BeFalse();
        _dibujante.Verify(d => d.Cerrar(), Times.Once);
        sut.Dispose();
    }

    [Fact]
    public void Dispose_CierraElDibujante()
    {
        var sut = Crear();
        _dibujante.Invocations.Clear();

        sut.Dispose();

        _dibujante.Verify(d => d.Cerrar(), Times.AtLeastOnce);
    }
}
