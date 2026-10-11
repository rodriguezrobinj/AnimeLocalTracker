using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// El reproductor con un motor de mentira. Antes de la costura estas rutas (arranque, reanudación, reintentos) solo se podían ver en
/// la app: bajo pruebas no había motor.
/// </summary>
public sealed class ReproductorConMotorTests : IDisposable
{
    private readonly Mock<IPlaybackStateService> _estado = new();
    private readonly MotorDePrueba _motor = new() { Duracion = TimeSpan.FromSeconds(1420) };
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_motor_" + Guid.NewGuid().ToString("N"));
    private readonly string _ruta;

    public ReproductorConMotorTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ruta = Path.Combine(_carpeta, "Ep05.mkv");
        File.WriteAllText(_ruta, "no es un video: solo tiene que existir");
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private ReproductorViewModel CrearSut(IMotorVideo? motor) => new(
        Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
        playbackStateService: _estado.Object, fabricaMotor: _ => motor);

    private static async Task EsperarHasta(Func<bool> condicion, int ms = 8000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    [Fact]
    public async Task AlAbrirUnEpisodio_ArrancaEnSilencioYSeDestapaConElPrimerFotograma()
    {
        using var sut = CrearSut(_motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.Abiertos.Should().Equal(_ruta);
        _motor.SilencioPuesto.Should().BeTrue("el audio arrancaba antes que la imagen");
        sut.OcultarVideoInicio.Should().BeTrue();

        _motor.TerminarApertura();

        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
        sut.PlayPauseIcon.Should().Be("Pause");
        _motor.SilencioPuesto.Should().BeTrue("sigue en silencio hasta que se vea la imagen");

        _motor.FotogramasMostrados = 3;

        await EsperarHasta(() => !sut.OcultarVideoInicio);
        await EsperarHasta(() => !_motor.SilencioPuesto);
        _motor.Saltos.Should().BeEmpty("sin punto guardado no hay salto de reanudación");
    }

    [Fact]
    public async Task ConPuntoGuardado_SaltaAhiAntesDeDestaparElVideo()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        using var sut = CrearSut(_motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        _motor.Saltos.Should().BeEmpty("no se salta mientras el decodificador arranca: dejaba la pantalla en negro");
        _motor.FotogramasMostrados = 3;

        await EsperarHasta(() => !sut.OcultarVideoInicio);
        _motor.Saltos.Should().ContainSingle().Which.Segundos.Should().Be(300);
        sut.CurrentSeconds.Should().Be(300);
    }

    [Fact]
    public async Task SinMotor_AbrirUnEpisodioNoFalla()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        using var sut = CrearSut(null);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        sut.Motor.Should().BeNull();
        sut.CurrentSeconds.Should().Be(300, "el punto guardado se muestra aunque no haya motor");
        sut.TogglePlayPause();
        sut.Seek(60);
    }

    [Fact]
    public async Task SiLaAperturaFalla_ReintentaUnaVezYLuegoAvisa()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.TerminarApertura(exito: false, error: "formato no reconocido");

        await EsperarHasta(() => _motor.Abiertos.Count == 2);
        _motor.VecesDetenido.Should().Be(1);
        sut.OcultarVideoInicio.Should().BeTrue("aún se está intentando");

        _motor.TerminarApertura(exito: false, error: "formato no reconocido");

        sut.OcultarVideoInicio.Should().BeFalse();
        sut.PlayPauseIcon.Should().Be("Play");
        _motor.Abiertos.Should().HaveCount(2, "un archivo dañado de verdad no se reintenta sin fin");
    }

    [Fact]
    public async Task SiLaTarjetaNoDaImagen_ReabreUnaVezPorProcesador()
    {
        _motor.DecodificaPorHardware = true;
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.TerminarApertura(); // y no llega ni un fotograma

        await EsperarHasta(() => _motor.Abiertos.Count == 2, ms: 10000);
        _motor.AceleracionPuesta.Should().BeFalse();
        _motor.VecesDetenido.Should().Be(1);

        _motor.TerminarApertura(); // la reapertura tampoco pinta nada: no hay tercera
        await EsperarHasta(() => !sut.OcultarVideoInicio, ms: 15000); // se deja de esperar: mejor ver el fallo que una tapa eterna
        _motor.Abiertos.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnAvisoDeAperturaDelEpisodioAnterior_SeComportaComoHoy()
    {
        // El aviso del motor no dice de qué archivo es. Si llega el del episodio que se dejó atrás, el reproductor lo toma por el
        // del nuevo: se fija aquí para que la etapa 2 (libmpv sí sabe de qué archivo avisa) decida si lo corrige.
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        string otra = Path.Combine(_carpeta, "Ep06.mkv");
        File.WriteAllText(otra, "x");
        await sut.CargarVideoAsync(otra, 101, "Frieren", 6);

        _motor.TerminarApertura(); // el del 5, tardío

        _motor.Abiertos.Should().Equal(_ruta, otra);
        _motor.VecesReproducir.Should().Be(1);
    }

    [Fact]
    public async Task CerrarConLaAperturaAMedias_DesechaElMotorSinFallar()
    {
        var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        sut.Dispose();
        _motor.TerminarApertura(); // el aviso llega con el reproductor ya cerrado
        await Task.Delay(300);

        _motor.VecesDesechado.Should().Be(1);
        sut.Motor.Should().BeNull();
    }

    [Fact]
    public async Task PausarYReanudar_PasanPorElMotor_YAlTerminarSeVuelveAEmpezar()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.TogglePlayPause();
        _motor.Estado.Should().Be(EstadoMotor.EnPausa);
        sut.PlayPauseIcon.Should().Be("Play");

        sut.TogglePlayPause();
        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);

        _motor.Estado = EstadoMotor.Terminado;
        sut.TogglePlayPause();
        _motor.Saltos.Should().Contain((0d, false), "reproducir un episodio terminado lo reinicia");
        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
    }

    [Fact]
    public async Task CambiarDePistaDeAudio_NoSeTrataComoUnEpisodioNuevo()
    {
        _motor.Audio.Add(new PistaAudioMotor(1, "ja", null));
        _motor.Audio.Add(new PistaAudioMotor(2, "es", "Latino"));
        _motor.IndicePistaAudioActual = 1;
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        _motor.FotogramasMostrados = 3;
        await EsperarHasta(() => !sut.OcultarVideoInicio);
        sut.PistasAudio.Should().HaveCount(2);
        sut.HayVariasPistasAudio.Should().BeTrue();

        sut.SeleccionarPistaAudioCommand.Execute(sut.PistasAudio[1]);
        _motor.AudioElegido.Should().Be(new PistaAudioMotor(2, "es", "Latino"));
        _motor.SilencioPuesto = true; // el motor reabre el audio y vuelve a avisar
        _motor.TerminarApertura();

        _motor.Abiertos.Should().HaveCount(1, "no se reabre el episodio");
        _motor.VecesReproducir.Should().Be(1, "ni se vuelve a dar a reproducir");
        _motor.SilencioPuesto.Should().BeFalse("se restaura el silencio que tenía el usuario");
        _motor.VolumenPuesto.Should().Be(sut.Volumen);
        sut.PistasAudio[1].EsActual.Should().BeTrue();
    }

    [Fact]
    public async Task ElEcualizador_MontaSusFiltrosEnElMotor_YUnaBandaSeCambiaEnVivo()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.BandasEcualizador[0].Ganancia = 6; // mover una banda con el ecualizador apagado lo enciende y monta la cadena
        sut.EcualizadorActivo.Should().BeTrue();
        _motor.Filtros.Should().NotBeEmpty();

        sut.BandasEcualizador[1].Ganancia = 3; // ya encendido: solo se toca el filtro que está sonando
        _motor.CambiosEnVivo.Should().Contain(c => c.Id == AnimeLocalTracker.Core.EcualizadorAudio.IdBanda(1) && c.Clave == "g");
    }

    [Fact]
    public async Task UnaPistaDeTexto_LaLeeLaApp_YNoSeLePideAlMotor()
    {
        // El lector de subtítulos ASS de Flyleaf cerraba la app con ciertas etiquetas: las pistas de texto las lee la app.
        var extractor = new Mock<ISubtitleCuesExtractorService>();
        IReadOnlyList<AnimeLocalTracker.Models.SubtitleCue> lineas = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Hola")];
        extractor.Setup(x => x.ExtraerAsync(_ruta, 2, null, It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(lineas);
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(2, LocalizationService.Cultura.TwoLetterISOLanguageName, null, false, false, null));
        using var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            playbackStateService: _estado.Object, subtitleCuesExtractorService: extractor.Object, fabricaMotor: _ => _motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        await EsperarHasta(() => sut.SubtitulosDobleLineaActivo);
        _motor.SubtitulosDelMotor.Should().BeNull();
        sut.SubtitulosHabilitados.Should().BeTrue();
    }

    [Fact]
    public async Task SiLaAppNoPuedeLeerUnaPistaDeTexto_SeLaDejaAlMotor()
    {
        var extractor = new Mock<ISubtitleCuesExtractorService>();
        extractor.Setup(x => x.ExtraerAsync(_ruta, 2, null, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AnimeLocalTracker.Models.SubtitleCue>)Array.Empty<AnimeLocalTracker.Models.SubtitleCue>());
        var texto = new PistaSubtitulosMotor(2, LocalizationService.Cultura.TwoLetterISOLanguageName, null, false, false, null);
        _motor.Subtitulos.Add(texto);
        using var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            playbackStateService: _estado.Object, subtitleCuesExtractorService: extractor.Object, fabricaMotor: _ => _motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        await EsperarHasta(() => _motor.SubtitulosDelMotor == texto); // mejor los del motor que quedarse sin subtítulos
    }

    [Fact]
    public async Task UnaPistaDeImagen_SeLePideAlMotor_YSuAvisoNoReiniciaElEpisodio()
    {
        var imagen = new PistaSubtitulosMotor(4, "es", null, EsImagen: true, EsAss: false, RutaExterna: null);
        _motor.Subtitulos.Add(imagen);
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        _motor.SubtitulosDelMotor.Should().Be(imagen, "la app no sabe dibujar subtítulos de imagen");

        _motor.TerminarApertura(clase: ClaseApertura.Subtitulos);

        _motor.VecesReproducir.Should().Be(1);
        _motor.Abiertos.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApagarLosSubtitulos_SeLoDiceAlMotor()
    {
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "es", null, true, false, null));
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.TurnOffSubtitles();

        _motor.SubtitulosDelMotor.Should().BeNull();
        sut.SubtitulosHabilitados.Should().BeFalse();
    }

    [Fact]
    public void ElMotorAvisaCuandoCambiaLaProporcionDelVideo()
    {
        int avisos = 0;
        _motor.ProporcionVideoCambiada += (_, _) => avisos++;

        _motor.CambiarProporcion(4 / 3.0);

        avisos.Should().Be(1);
        _motor.ProporcionVideo.Should().BeApproximately(1.333, 0.001);
    }

    // ── Etapa 5 de libmpv: el motor nuevo es el predeterminado ──

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Mpv", true)]
    [InlineData("mpv", true)]
    [InlineData("Nuevo", true)]
    [InlineData("x", true)]
    [InlineData("Flyleaf", false)]
    [InlineData(" flyleaf ", false)]
    public void ElegirMotor_TodoLoQueNoSeaElClasicoEsElNuevo(string? ajuste, bool esperado)
    {
        MotorVideo.ElegirMotor(ajuste, mpvDisponible: true).Should().Be(esperado);
        MotoresDeVideo.Normalizar(ajuste).Should().Be(esperado ? MotoresDeVideo.Nuevo : MotoresDeVideo.Clasico);
    }

    [Fact]
    public void ElegirMotor_SinBibliotecaOConElLienzoRoto_EsElClasico()
    {
        MotorVideo.ElegirMotor("Mpv", mpvDisponible: false).Should().BeFalse("falta la biblioteca: se sigue con el motor clásico");

        MotorVideo.LienzoNoDisponible = true;
        try { MotorVideo.ElegirMotor("Mpv", mpvDisponible: true).Should().BeFalse("el lienzo ya falló en esta sesión"); }
        finally { MotorVideo.LienzoNoDisponible = false; }
    }

    [Fact]
    public void AppSettings_PorDefectoPideElMotorNuevo()
    {
        new AnimeLocalTracker.Models.AppSettings().MotorVideo.Should().Be(MotoresDeVideo.Nuevo);
    }

    [Fact]
    public async Task ElMotorSeEligeAlCrearElReproductor_NoAMitadDeEpisodio()
    {
        // Cambiar «Motor de video» en Configuración con un episodio abierto (o en el mini reproductor) no toca al que suena.
        var ajustes = new AnimeLocalTracker.Models.AppSettings();
        var configuracion = new Mock<ISettingsService>();
        configuracion.Setup(c => c.ObtenerConfiguracion()).Returns(ajustes);
        int motoresCreados = 0;
        using var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            settingsService: configuracion.Object, playbackStateService: _estado.Object,
            fabricaMotor: _ => { motoresCreados++; return _motor; });
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        ajustes.MotorVideo = MotoresDeVideo.Clasico;
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 6);

        motoresCreados.Should().Be(1);
        _motor.VecesDesechado.Should().Be(0);
        _motor.Abiertos.Should().HaveCount(2);
    }

    // ── Modo de compatibilidad: el motor nuevo no puede pintar y se sigue con el clásico ──

    private (ReproductorViewModel Sut, MotorDePrueba Clasico) CrearSutConMotorNuevo(bool hayClasico = true)
    {
        var clasico = new MotorDePrueba { Duracion = TimeSpan.FromSeconds(1420) };
        var sut = CrearSut(_motor);
        sut.UsaMotorMpv = true;
        sut.FabricaMotorClasico = _ => hayClasico ? clasico : null;
        return (sut, clasico);
    }

    [Fact]
    public async Task SiElLienzoFalla_ElEpisodioSeReabreConElMotorClasicoDondeIba()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        var (sut, clasico) = CrearSutConMotorNuevo();
        using var _ = sut;
        try
        {
            await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

            sut.PasarAlMotorClasico("sin OpenGL");

            _motor.VecesDesechado.Should().Be(1);
            sut.Motor.Should().BeSameAs(clasico);
            sut.UsaMotorMpv.Should().BeFalse("la plantilla cambia a la vista clásica");
            MotorVideo.LienzoNoDisponible.Should().BeTrue("el resto de la sesión ya no lo intenta");
            clasico.Abiertos.Should().Equal(_ruta);
            clasico.SilencioPuesto.Should().BeTrue("arranca en silencio, como cualquier apertura");

            clasico.TerminarApertura();
            clasico.FotogramasMostrados = 3;

            await EsperarHasta(() => !sut.OcultarVideoInicio);
            clasico.Saltos.Should().ContainSingle().Which.Segundos.Should().Be(300);
        }
        finally { MotorVideo.LienzoNoDisponible = false; }
    }

    [Fact]
    public async Task SiElLienzoFallaConElEpisodioAMedias_SigueDesdeEsePunto()
    {
        var (sut, clasico) = CrearSutConMotorNuevo();
        using var _ = sut;
        try
        {
            await AbrirYVerLaImagen(sut);
            _motor.Posicion = TimeSpan.FromSeconds(640);
            await EsperarHasta(() => Math.Abs(sut.CurrentSeconds - 640) < 1); // la barra sigue al video

            sut.PasarAlMotorClasico("el lienzo dejó de pintar");

            sut.OcultarVideoInicio.Should().BeTrue("se tapa hasta que el clásico muestre su primer fotograma");
            clasico.TerminarApertura();
            clasico.FotogramasMostrados = 3;
            await EsperarHasta(() => !sut.OcultarVideoInicio);
            clasico.Saltos.Should().ContainSingle().Which.Segundos.Should().Be(640);
        }
        finally { MotorVideo.LienzoNoDisponible = false; }
    }

    [Fact]
    public async Task ElAvisoDosVeces_SoloCambiaUnaVez()
    {
        var (sut, clasico) = CrearSutConMotorNuevo();
        using var _ = sut;
        try
        {
            await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

            sut.PasarAlMotorClasico("sin OpenGL");
            sut.PasarAlMotorClasico("sin OpenGL");

            clasico.Abiertos.Should().HaveCount(1);
            clasico.VecesDesechado.Should().Be(0);
            _motor.VecesDesechado.Should().Be(1);
        }
        finally { MotorVideo.LienzoNoDisponible = false; }
    }

    [Fact]
    public async Task SiElLienzoFallaYNoHayMotorClasico_AvisaUnaVez()
    {
        var dialogos = new Mock<IDialogService>();
        var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            playbackStateService: _estado.Object, dialogService: dialogos.Object, fabricaMotor: _ => _motor)
        {
            UsaMotorMpv = true,
            FabricaMotorClasico = _ => null
        };
        using var _ = sut;
        try
        {
            await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

            sut.PasarAlMotorClasico("sin OpenGL");
            sut.PasarAlMotorClasico("sin OpenGL");

            sut.Motor.Should().BeNull();
            sut.OcultarVideoInicio.Should().BeFalse("no se deja la tapa esperando un video que no va a llegar");
            dialogos.Verify(d => d.MostrarToastReproductor(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
        finally { MotorVideo.LienzoNoDisponible = false; }
    }

    [Fact]
    public async Task ElMenuDeSubtitulos_ListaLasPistasDelMotor_ConNombreLegible()
    {
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(3, "es", null, true, false, null));
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "en", "Signs & Songs", true, false, null));
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.PistasSubtitulosMenu.Select(p => p.Pista.Indice).Should().Equal(3, 4);
        sut.PistasSubtitulosMenu[1].Nombre.Should().Contain("Signs & Songs");

        sut.SelectSubtitleStreamCommand.Execute(sut.PistasSubtitulosMenu[1].Pista);
        _motor.SubtitulosDelMotor!.Indice.Should().Be(4);
    }

    [Fact]
    public void ConUnMotorInyectado_NoSeUsaLaVistaDelMotorNuevo()
    {
        using var sut = CrearSut(_motor);

        sut.UsaMotorMpv.Should().BeFalse("solo el motor que elige la app por el ajuste decide la vista");
    }

    // ── Etapa 3 de libmpv: un motor que dibuja él mismo los subtítulos de texto ──

    private static readonly PistaSubtitulosMotor PistaAss = new(3, LocalizationService.Cultura.TwoLetterISOLanguageName, null, EsImagen: false, EsAss: true, RutaExterna: null);

    private (ReproductorViewModel Sut, Mock<ISubtitleCuesExtractorService> Extractor) CrearSutQueDibujaSubtitulos(AnimeLocalTracker.Models.AppSettings? ajustes = null)
    {
        _motor.DibujaSubtitulosDeTexto = true;
        _motor.Subtitulos.Add(PistaAss);
        var extractor = new Mock<ISubtitleCuesExtractorService>();
        var configuracion = new Mock<ISettingsService>();
        configuracion.Setup(c => c.ObtenerConfiguracion()).Returns(ajustes ?? new AnimeLocalTracker.Models.AppSettings());
        var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            settingsService: configuracion.Object, playbackStateService: _estado.Object,
            subtitleCuesExtractorService: extractor.Object, fabricaMotor: _ => _motor);
        return (sut, extractor);
    }

    [Fact]
    public async Task ConMotorQueDibujaLosSubtitulos_UnaPistaDeTextoSeLeDaAlMotorYLaAppNoLaExtrae()
    {
        var (sut, extractor) = CrearSutQueDibujaSubtitulos();
        using var _ = sut;
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        _motor.SubtitulosDelMotor.Should().Be(PistaAss, "este motor dibuja todas las pistas dentro del video");
        sut.PistaActualEsAss.Should().BeTrue("el menú sigue ofreciendo «Usar mi estilo»");
        sut.SubtitulosAssActivo.Should().BeFalse("la app no prepara su propio dibujo ASS");
        sut.SubtitulosDobleLineaActivo.Should().BeFalse();
        extractor.Verify(x => x.ExtraerAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConMotorQueDibujaLosSubtitulos_UsarMiEstiloSeLoPideAlMotor()
    {
        var (sut, _) = CrearSutQueDibujaSubtitulos();
        using var __ = sut;
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.UsarMiEstiloEnAss = true;
        _motor.EstiloPuesto!.Value.TambienEnAss.Should().BeTrue();

        sut.UsarMiEstiloEnAss = false;
        _motor.EstiloPuesto!.Value.TambienEnAss.Should().BeFalse();
    }

    [Fact]
    public async Task ConMotorQueDibujaLosSubtitulos_ApagarYEncenderVuelveAMostrarLaMismaPista()
    {
        var (sut, _) = CrearSutQueDibujaSubtitulos();
        using var __ = sut;
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.DeshabilitarSubtitulos();
        _motor.SubtitulosDelMotor.Should().BeNull();

        sut.HabilitarSubtitulos();
        _motor.SubtitulosDelMotor.Should().Be(PistaAss);
    }

    [Fact]
    public async Task AlCrearElMotor_SeLeDaElEstiloDeSubtitulosDeConfiguracion()
    {
        var ajustes = new AnimeLocalTracker.Models.AppSettings { EstiloSubtitulos = new AnimeLocalTracker.Models.EstiloSubtitulos { Tamano = 60 }, UsarMiEstiloEnAss = true };
        var (sut, _) = CrearSutQueDibujaSubtitulos(ajustes);
        using var __ = sut;

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.EstiloPuesto!.Value.Estilo.Tamano.Should().Be(60);
        _motor.EstiloPuesto!.Value.TambienEnAss.Should().BeTrue();
    }

    [Fact]
    public async Task CambiarUnaBandaEnVivo_RecuerdaLaCadenaParaElSiguienteArchivo()
    {
        // El cambio en vivo solo toca el filtro que suena: sin apuntar la cadena, el siguiente episodio volvía a las ganancias anteriores.
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        sut.BandasEcualizador[0].Ganancia = 6;

        sut.BandasEcualizador[1].Ganancia = 3;

        _motor.FiltrosRecordados.Should().NotBeNull();
        _motor.FiltrosRecordados!.Should().Contain(f => f.Id == AnimeLocalTracker.Core.EcualizadorAudio.IdBanda(1) && f.Argumentos.EndsWith("g=3.0"));
    }

    // ── Etapa 4 de libmpv: escalado con sombreadores (Anime4K), vigilado en automático ──

    private ReproductorViewModel CrearSutConSombreadores(string modo)
    {
        _motor.ClaseDeEscalado = ClaseEscalado.PorSombreadores;
        _motor.TarjetaEnUso = new TarjetaGrafica("Intel(R) HD Graphics 620", FabricanteGrafica.Intel, 0);
        var configuracion = new Mock<ISettingsService>();
        configuracion.Setup(c => c.ObtenerConfiguracion()).Returns(new AnimeLocalTracker.Models.AppSettings { EscaladoInteligenteVideo = modo });
        return new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            settingsService: configuracion.Object, playbackStateService: _estado.Object, fabricaMotor: _ => _motor)
        {
            VigilanteEscalado = new VigilanteEscalado { Ventana = TimeSpan.FromMilliseconds(40) }
        };
    }

    private async Task AbrirYVerLaImagen(ReproductorViewModel sut, int episodio = 5)
    {
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", episodio);
        _motor.TerminarApertura();
        _motor.FotogramasMostrados = 3;
        await EsperarHasta(() => !sut.OcultarVideoInicio);
    }

    /// <summary>El motor "pierde" fotogramas sin parar hasta que se cumple la condición (o se agota el tiempo).</summary>
    private async Task<bool> PerdiendoFotogramasHasta(Func<bool> condicion, int ms)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite)
        {
            _motor.FotogramasPerdidos += 10;
            await Task.Delay(20);
        }
        return condicion();
    }

    [Fact]
    public async Task ConSombreadores_EnAutomaticoSeEnciendeAunqueLaTarjetaSeaIntegrada()
    {
        using var sut = CrearSutConSombreadores(ModosRendimientoVideo.Automatico);

        await AbrirYVerLaImagen(sut);

        await EsperarHasta(() => _motor.EscaladoInteligente);
    }

    [Fact]
    public async Task EnAutomatico_SiPierdeFotogramasDeFormaSostenida_SeApagaEnEseEpisodio()
    {
        using var sut = CrearSutConSombreadores(ModosRendimientoVideo.Automatico);
        await AbrirYVerLaImagen(sut);
        await EsperarHasta(() => _motor.EscaladoInteligente);

        (await PerdiendoFotogramasHasta(() => !_motor.EscaladoInteligente, 8000)).Should().BeTrue("cuatro ventanas seguidas perdiendo fotogramas");

        // Y no vuelve a encenderse en el mismo episodio aunque deje de perder.
        await Task.Delay(600);
        _motor.EscaladoInteligente.Should().BeFalse();
    }

    [Fact]
    public async Task EnActivado_NoSeApagaAunquePierdaFotogramas()
    {
        using var sut = CrearSutConSombreadores(ModosRendimientoVideo.Activado);
        await AbrirYVerLaImagen(sut);
        await EsperarHasta(() => _motor.EscaladoInteligente);

        (await PerdiendoFotogramasHasta(() => !_motor.EscaladoInteligente, 2500)).Should().BeFalse("lo pidió el usuario: no se apaga solo");
    }

    [Fact]
    public async Task TrasApagarsePorRendimiento_AlAbrirOtroEpisodioVuelveAIntentarlo()
    {
        using var sut = CrearSutConSombreadores(ModosRendimientoVideo.Automatico);
        await AbrirYVerLaImagen(sut);
        await EsperarHasta(() => _motor.EscaladoInteligente);
        (await PerdiendoFotogramasHasta(() => !_motor.EscaladoInteligente, 8000)).Should().BeTrue();

        await AbrirYVerLaImagen(sut, episodio: 6);

        await EsperarHasta(() => _motor.EscaladoInteligente);
    }

    [Fact]
    public async Task ConElEscaladoDelControlador_UnaIntegradaSigueSinEscaladoEnAutomatico()
    {
        // El motor clásico: su escalado es el de la tarjeta y en automático solo se usa con una dedicada.
        _motor.TarjetaEnUso = new TarjetaGrafica("Intel(R) HD Graphics 620", FabricanteGrafica.Intel, 128);
        using var sut = CrearSut(_motor);

        await AbrirYVerLaImagen(sut);
        await Task.Delay(400);

        _motor.EscaladoInteligente.Should().BeFalse();
    }
}
