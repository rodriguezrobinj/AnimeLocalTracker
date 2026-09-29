using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Ubicación de opening/ending por audio oficial de AnimeThemes: mejor candidato con confianza mínima, AniSkip solo como respaldo de lo
/// que falta y análisis guardado por episodio (segunda vez al instante).
/// </summary>
public class SkipTimesCoordinatorAudioTests : IDisposable
{
    private readonly Mock<IAniSkipService> _aniSkip = new();
    private readonly Mock<IPythonBridgeService> _python = new();
    private readonly Mock<IAnimeThemesDownloadService> _descargas = new();
    private readonly Mock<IReferenciasAudioService> _referencias = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeTracker_SkipAudio_" + Guid.NewGuid().ToString("N"));
    private readonly string _episodio;
    private readonly List<object> _llamadas = new();
    private readonly List<(string Ruta, string Tipo, int Prioridad)> _enviadas = new();
    private readonly string _carpetaHuellas = Path.Combine(Path.GetTempPath(), "huellas-que-no-se-crean");

    private AnalisisSkipEpisodio? _analisisGuardado;
    private List<SegmentoSkipGuardado> _segmentosGuardados = new();

    public SkipTimesCoordinatorAudioTests()
    {
        Directory.CreateDirectory(_carpeta);
        _episodio = Path.Combine(_carpeta, "Episodio 05.mkv");
        File.WriteAllBytes(_episodio, new byte[2048]);

        _python.Setup(p => p.IsAvailableAsync()).ReturnsAsync(true);
        _aniSkip.Setup(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(9999);
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<AniSkipResult>());

        _db.Setup(d => d.GuardarAnalisisSkipAsync(It.IsAny<AnalisisSkipEpisodio>(), It.IsAny<IReadOnlyList<SegmentoSkipGuardado>>()))
            .Callback((AnalisisSkipEpisodio a, IReadOnlyList<SegmentoSkipGuardado> s) => { _analisisGuardado = a; _segmentosGuardados = s.ToList(); })
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private SkipTimesCoordinator CrearSut(bool conBaseDeDatos = false) =>
        new(_aniSkip.Object, _python.Object, _descargas.Object, _referencias.Object, conBaseDeDatos ? _db.Object : null, _carpetaHuellas);

    private void Referencias(bool completa, params TemaLocalDisponible[] temas) =>
        _referencias.Setup(r => r.ObtenerAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReferenciasEpisodio(temas, completa));

    /// <summary>El motor Python "encuentra" estos tramos (segment "op"/"ed"); guarda la llamada y las referencias que se le mandaron.</summary>
    private void Deteccion(params (string Tramo, double Inicio, double Fin, double Confianza)[] tramos) =>
        RespuestaDelMotor(new SkipTimesCoordinator.DeteccionTemasResult
        {
            Success = true,
            Matches = tramos.Select(t => new SkipTimesCoordinator.TemaDetectado
            {
                Segment = t.Tramo, Start = t.Inicio, End = t.Fin, Confidence = t.Confianza, Mode = "full", ReferencePath = "tema.ogg"
            }).ToList()
        });

    private void RespuestaDelMotor(SkipTimesCoordinator.DeteccionTemasResult? resultado, bool exito = true) =>
        _python.Setup(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.DeteccionTemasResult>>(
                "run-plugin", It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(inv =>
            {
                object payload = inv.Arguments[1];
                _llamadas.Add(payload);
                foreach (object r in (System.Collections.IEnumerable)Propiedad(Propiedad(payload, "args"), "references"))
                    _enviadas.Add(((string)Propiedad(r, "path"), (string)Propiedad(r, "kind"), (int)Propiedad(r, "priority")));

                return Task.FromResult(new PluginDaemonResponse<SkipTimesCoordinator.DeteccionTemasResult> { Success = exito, Result = resultado });
            }));

    private static object Propiedad(object o, string nombre) => o.GetType().GetProperty(nombre)!.GetValue(o)!;

    private static TemaLocalDisponible Tema(string tipo, string slug, string? rango, string ruta) => new(tipo, slug, 1, rango, ruta);

    private static AniSkipResult Nube(string tipo, double ini, double fin) => new() { SkipType = tipo, Interval = new AniSkipInterval { StartTime = ini, EndTime = fin } };

    // === Una sola llamada con todos los temas ===

    [Fact]
    public async Task TodosLosTemasVanEnUnaSolaLlamada_ConSuTipoYPrioridadSegunElRango()
    {
        // Antes: una llamada por tema, cada una decodificando otra vez el episodio (One Piece, 29 temas: 30 s; ahora ~2 s).
        Referencias(true, Tema("OP", "OP1", "1-12", "aplica.ogg"), Tema("OP", "OP2", "13-24", "noAplica.ogg"), Tema("ED", "ED1", null, "ed1.ogg"),
            Tema("IN", "IN1", null, "insercion.ogg"));
        Deteccion();

        await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        _llamadas.Should().ContainSingle();
        _enviadas.Should().BeEquivalentTo(new[] { ("aplica.ogg", "OP", 0), ("noAplica.ogg", "OP", 1), ("ed1.ogg", "ED", 0) },
            "las canciones de inserción no son ni opening ni ending");
        object args = Propiedad(_llamadas[0], "args");
        Propiedad(_llamadas[0], "func_name").Should().Be("detect_themes");
        Propiedad(args, "episode_path").Should().Be(_episodio);
        Propiedad(args, "cache_dir").Should().Be(_carpetaHuellas);
        Propiedad(args, "min_confidence").Should().Be(SkipTimesCoordinator.ConfianzaMinima);
    }

    [Fact]
    public async Task SinTemasDeOpeningNiEnding_NoLlamaAlMotor()
    {
        Referencias(true, Tema("IN", "IN1", null, "insercion.ogg"));
        Deteccion();

        await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        _llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task ConVariosTramosDelMismoTipo_SeQuedaConElDeMayorConfianza()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"), Tema("OP", "OP2", null, "op2.ogg"));
        Deteccion(("op", 100, 190, 0.75), ("op", 10, 100, 0.95));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        var op = resultado.Single(r => r.EsIntro);
        (op.Interval.StartTime, op.Interval.EndTime, op.Confianza).Should().Be((10, 100, 0.95));
    }

    [Fact]
    public async Task UnaDeteccionConConfianzaBaja_NoSeAcepta()
    {
        // Medido con episodios reales: un tema que no corresponde (u otro corte de la misma canción) se queda en 0,3-0,6.
        Referencias(true, Tema("ED", "ED2", null, "ed2.ogg"));
        Deteccion(("ed", 1200, 1314, 0.45));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().NotContain(r => r.EsEnding && r.Origen == "audio");
    }

    [Fact]
    public async Task UnOpeningQueCierraElEpisodio_SeMarcaComoEnding()
    {
        // Episodio 1 de Kage no Jitsuryokusha y final de Kimi ga Shinu made: no hay ending propio, suena el opening al final.
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("ed", 1330, 1419, 0.87));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 1, 1420, _episodio);

        resultado.Where(r => r.Origen == "audio").Should().ContainSingle().Which.EsEnding.Should().BeTrue();
    }

    [Fact]
    public async Task SiElMotorFalla_NoHayTramosDeAudio_YSeSigueConAniSkip()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        RespuestaDelMotor(new SkipTimesCoordinator.DeteccionTemasResult { Success = false, Error = "ffmpeg no encontrado" });
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("op", 60, 150) });

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().ContainSingle().Which.Origen.Should().Be("aniskip");
    }

    // === AniSkip como respaldo ===

    [Fact]
    public async Task AniSkipCompletaSoloLoQueElAudioNoEncontro()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("op", 5, 95), Nube("ed", 1300, 1390), Nube("recap", 0, 40) });

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Where(r => r.EsIntro).Should().ContainSingle().Which.Origen.Should().Be("audio", "el opening ya lo dio el audio: el de la nube se descarta");
        resultado.Single(r => r.EsEnding).Origen.Should().Be("aniskip");
        resultado.Should().Contain(r => r.EsRecap, "el resumen previo nunca sale del audio");
    }

    [Fact]
    public async Task ConOpeningYEndingYaUbicadosPorAudio_NoSeConsultaAniSkip()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"), Tema("ED", "ED1", null, "ed1.ogg"));
        Deteccion(("op", 90, 180, 0.9), ("ed", 1300, 1390, 0.95));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().HaveCount(2).And.OnlyContain(r => r.Origen == "audio");
        _aniSkip.Verify(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "con lo esencial ya resuelto no se gasta red ni cuota de la nube (y el análisis termina antes)");
    }

    [Fact]
    public async Task SinReferenciasDeAudio_ElResultadoEsElDeAniSkip()
    {
        Referencias(false);
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("op", 60, 150), Nube("ed", 1300, 1390) });

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().OnlyContain(r => r.Origen == "aniskip");
        resultado.Should().HaveCount(2);
    }

    [Fact]
    public async Task SiAniSkipFalla_ElResultadoDelAudioSeConserva()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));
        _aniSkip.Setup(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("sin red"));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().ContainSingle().Which.Origen.Should().Be("audio");
    }

    [Fact]
    public async Task SiAniSkipNoResponde_NoSeEsperaIndefinidamente_YSeConservaLoDelAudio()
    {
        // Con AniList caído, resolver el MAL ID esperaba 30-60 s por episodio.
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));
        _aniSkip.Setup(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return 9999; });
        var sut = CrearSut(conBaseDeDatos: true);
        sut.TiempoMaximoAniSkip = TimeSpan.FromMilliseconds(100);

        var resultado = await sut.CargarSkipTimesAsync(101, 5, 1400, _episodio).WaitAsync(TimeSpan.FromSeconds(10));

        resultado.Should().ContainSingle().Which.Origen.Should().Be("audio");
        _analisisGuardado!.Completo.Should().BeFalse("AniSkip no se pudo consultar: se reintentará");
    }

    [Fact]
    public async Task SinArchivoDeVideo_SoloUsaAniSkip_YNoIntentaDetectar()
    {
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("op", 60, 150) });

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, null);

        resultado.Should().ContainSingle();
        _referencias.Verify(r => r.ObtenerAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConReferenciasQueFallan_SigueConAniSkip()
    {
        _referencias.Setup(r => r.ObtenerAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("op", 60, 150) });

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().ContainSingle().Which.Origen.Should().Be("aniskip");
    }

    [Fact]
    public async Task LaCancelacionSePropaga()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _referencias.Setup(r => r.ObtenerAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int _, int _, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ReferenciasEpisodio([], true)); });

        Func<Task> f = () => CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio, cts.Token);

        await f.Should().ThrowAsync<OperationCanceledException>();
    }

    // === Último recurso (comparar con otro episodio) ===

    private void OtroEpisodioLocal() => File.WriteAllBytes(Path.Combine(_carpeta, "Episodio 06.mkv"), new byte[1024]);

    private void ComparacionEntreEpisodios(double confianza) =>
        _python.Setup(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.AudioSkipResult>>(
                "run-plugin", It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginDaemonResponse<SkipTimesCoordinator.AudioSkipResult>
            {
                Success = true,
                Result = new SkipTimesCoordinator.AudioSkipResult { Found = true, IntroEstimatedStart = 0, IntroEstimatedEnd = 85, Confidence = confianza }
            });

    [Fact]
    public async Task ConReferenciasCompletas_UnEpisodioSinOpening_NoLanzaLaComparacionLentaEntreEpisodios()
    {
        OtroEpisodioLocal();
        ComparacionEntreEpisodios(0.95);
        Referencias(true, Tema("ED", "ED1", null, "ed1.ogg")); // AnimeThemes solo tiene ending para este episodio
        Deteccion(("ed", 1300, 1390, 0.9));

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 20, 1400, _episodio);

        resultado.Should().NotContain(r => r.EsIntro, "con el catálogo completo, no haber opening es un dato: no se inventa uno");
        _python.Verify(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.AudioSkipResult>>(
            It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SinReferencias_ElOpeningPuedeSalirDeCompararConOtroEpisodio()
    {
        OtroEpisodioLocal();
        ComparacionEntreEpisodios(0.85);
        Referencias(false);

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        var op = resultado.Single(r => r.EsIntro);
        (op.Origen, op.Interval.EndTime).Should().Be(("escenas", 85));
    }

    [Fact]
    public async Task LaComparacionEntreEpisodios_ConConfianzaBaja_SeDescarta()
    {
        // Medido con un episodio real: dejó un "opening" de 0:00 a 1:25 con confianza 0,47.
        OtroEpisodioLocal();
        ComparacionEntreEpisodios(0.47);
        Referencias(false);

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().NotContain(r => r.EsIntro);
    }

    // === Aviso de tramos parciales ===

    private sealed class Reportes : IProgress<IReadOnlyList<AniSkipResult>>
    {
        public List<IReadOnlyList<AniSkipResult>> Lista { get; } = new();
        public void Report(IReadOnlyList<AniSkipResult> value) => Lista.Add(value);
    }

    [Fact]
    public async Task AvisaDeLosTramosDelAudioAntesDeConsultarAniSkip_YLuegoConLosCompletos()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));
        int reportesAlLlegarAAniSkip = -1;
        var reportes = new Reportes();
        _aniSkip.Setup(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => { reportesAlLlegarAAniSkip = reportes.Lista.Count; return Task.FromResult<int?>(9999); });
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("ed", 1300, 1390) });

        var final = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio, reportes, CancellationToken.None);

        reportesAlLlegarAAniSkip.Should().Be(1, "lo del audio se avisa antes de esperar a la nube");
        reportes.Lista.Should().HaveCount(2);
        reportes.Lista[0].Should().ContainSingle().Which.Origen.Should().Be("audio");
        reportes.Lista[1].Should().HaveCount(2);
        final.Should().HaveCount(2);
    }

    [Fact]
    public async Task SinNingunTramo_NoAvisaDeNada()
    {
        Referencias(true);
        var reportes = new Reportes();

        await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio, reportes, CancellationToken.None);

        reportes.Lista.Should().BeEmpty();
    }

    [Fact]
    public async Task ElAvisoEsUnaCopia_NoLaListaQueSigueCambiando()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));
        _aniSkip.Setup(a => a.ObtenerSkipTimesAsync(9999, 5, It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AniSkipResult> { Nube("ed", 1300, 1390) });
        var reportes = new Reportes();

        await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio, reportes, CancellationToken.None);

        reportes.Lista[0].Should().HaveCount(1, "el primer aviso no debe cambiar cuando después se suma lo de AniSkip");
    }

    // === Análisis guardado ===

    [Fact]
    public async Task GuardaElAnalisisConSusTramosYLaFirmaDelArchivo()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"), Tema("ED", "ED1", null, "ed1.ogg"));
        Deteccion(("op", 90, 180, 0.9), ("ed", 1300, 1390, 0.95));

        await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        _analisisGuardado.Should().NotBeNull();
        (_analisisGuardado!.AnimeId, _analisisGuardado.Episodio).Should().Be((101, 5));
        _analisisGuardado.Firma.Should().Be(SkipTimesCoordinator.CalcularFirma(_episodio));
        _analisisGuardado.Completo.Should().BeTrue("tiene opening y ending y las referencias estaban completas");
        _segmentosGuardados.Should().HaveCount(2);
        _segmentosGuardados.Should().Contain(s => s.Tipo == "op" && s.Inicio == 90 && s.Origen == "audio" && s.Confianza == 0.9);
    }

    [Fact]
    public async Task ConOpeningYEndingPorAudio_ElAnalisisQuedaCompleto_AunqueAnimeThemesNoRespondiera()
    {
        // Sin red se usan los temas ya guardados; si con ellos salen los dos, no hay nada que repetir cada 12 h.
        Referencias(false, Tema("OP", "OP1", null, "op1.ogg"), Tema("ED", "ED1", null, "ed1.ogg"));
        Deteccion(("op", 90, 180, 0.9), ("ed", 1300, 1390, 0.95));

        await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        _analisisGuardado!.Completo.Should().BeTrue();
    }

    [Fact]
    public async Task ConReferenciasIncompletasOSinEnding_ElAnalisisSeGuardaComoIncompleto()
    {
        Referencias(false, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));

        await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        _analisisGuardado!.Completo.Should().BeFalse("faltan referencias y el ending: se reintentará pasadas unas horas");
    }

    [Fact]
    public async Task ConUnAnalisisVigenteGuardado_DevuelveLoGuardadoSinDetectarNiConsultarLaNube()
    {
        _db.Setup(d => d.ObtenerAnalisisSkipAsync(101, 5)).ReturnsAsync(new AnalisisSkipEpisodio
        {
            AnimeId = 101, Episodio = 5, Firma = SkipTimesCoordinator.CalcularFirma(_episodio)!, FechaUtc = DateTime.UtcNow.AddDays(-30), Completo = true
        });
        _db.Setup(d => d.ObtenerSegmentosSkipAsync(101, 5)).ReturnsAsync(
        [
            new SegmentoSkipGuardado { Tipo = "op", Inicio = 90, Fin = 180, Origen = "audio", Confianza = 0.9 },
            new SegmentoSkipGuardado { Tipo = "ed", Inicio = 1300, Fin = 1390, Origen = "aniskip" }
        ]);

        var resultado = await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        resultado.Should().HaveCount(2);
        resultado[0].Interval.StartTime.Should().Be(90);
        resultado[0].Origen.Should().Be("audio");
        _referencias.Verify(r => r.ObtenerAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _aniSkip.Verify(a => a.ObtenerMalIdDesdeAniListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _python.Verify(p => p.ExecuteCommandAsync<It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnAnalisisIncompleto_SeRepiteSiDespuesAparecioMusicaDelAnime()
    {
        // Caso real: sin conexión se guardó un análisis vacío y el usuario bajó el opening y el ending desde la Ficha poco después.
        var fecha = DateTime.UtcNow.AddHours(-2);
        _db.Setup(d => d.ObtenerAnalisisSkipAsync(101, 5)).ReturnsAsync(new AnalisisSkipEpisodio
        {
            AnimeId = 101, Episodio = 5, Firma = SkipTimesCoordinator.CalcularFirma(_episodio)!, FechaUtc = fecha, Completo = false
        });
        _referencias.Setup(r => r.ReferenciaMasNuevaUtc(101)).Returns(fecha.AddMinutes(30));
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));

        var resultado = await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        resultado.Should().Contain(r => r.EsIntro && r.Origen == "audio");
    }

    [Fact]
    public async Task UnAnalisisCompleto_NoMiraSiHayMusicaNueva()
    {
        _db.Setup(d => d.ObtenerAnalisisSkipAsync(101, 5)).ReturnsAsync(new AnalisisSkipEpisodio
        {
            AnimeId = 101, Episodio = 5, Firma = SkipTimesCoordinator.CalcularFirma(_episodio)!, FechaUtc = DateTime.UtcNow.AddDays(-3), Completo = true
        });
        _db.Setup(d => d.ObtenerSegmentosSkipAsync(101, 5)).ReturnsAsync([]);

        await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        _referencias.Verify(r => r.ReferenciaMasNuevaUtc(It.IsAny<int>()), Times.Never, "listar la música solo hace falta si el análisis quedó incompleto");
    }

    [Fact]
    public async Task SiElArchivoCambio_ElAnalisisGuardadoNoSirve_YSeVuelveADetectar()
    {
        _db.Setup(d => d.ObtenerAnalisisSkipAsync(101, 5)).ReturnsAsync(new AnalisisSkipEpisodio
        {
            AnimeId = 101, Episodio = 5, Firma = "otro-archivo", FechaUtc = DateTime.UtcNow, Completo = true
        });
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));

        var resultado = await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        resultado.Should().Contain(r => r.EsIntro);
        _referencias.Verify(r => r.ObtenerAsync(101, 5, It.IsAny<CancellationToken>()), Times.Once);
        _db.Verify(d => d.ObtenerSegmentosSkipAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SiLaBaseDeDatosFalla_ElAnalisisSigueFuncionando()
    {
        _db.Setup(d => d.ObtenerAnalisisSkipAsync(It.IsAny<int>(), It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("db"));
        _db.Setup(d => d.GuardarAnalisisSkipAsync(It.IsAny<AnalisisSkipEpisodio>(), It.IsAny<IReadOnlyList<SegmentoSkipGuardado>>())).ThrowsAsync(new InvalidOperationException("db"));
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        Deteccion(("op", 90, 180, 0.9));

        var resultado = await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, _episodio);

        resultado.Should().Contain(r => r.EsIntro);
    }

    [Fact]
    public async Task SinArchivoRealDeVideo_NoSeGuardaNada()
    {
        Referencias(true);

        await CrearSut(conBaseDeDatos: true).CargarSkipTimesAsync(101, 5, 1400, Path.Combine(_carpeta, "no-existe.mkv"));

        _db.Verify(d => d.GuardarAnalisisSkipAsync(It.IsAny<AnalisisSkipEpisodio>(), It.IsAny<IReadOnlyList<SegmentoSkipGuardado>>()), Times.Never);
    }

    // === Pre-análisis del siguiente episodio ===

    /// <summary>El motor Python queda "trabajando" hasta que se complete <paramref name="liberar"/>.</summary>
    private void DeteccionQueEspera(TaskCompletionSource liberar) =>
        _python.Setup(p => p.ExecuteCommandAsync<It.IsAnyType, PluginDaemonResponse<SkipTimesCoordinator.DeteccionTemasResult>>(
                "run-plugin", It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(inv => ResponderCuandoSeLibere(inv, liberar)));

    private async Task<PluginDaemonResponse<SkipTimesCoordinator.DeteccionTemasResult>> ResponderCuandoSeLibere(IInvocation inv, TaskCompletionSource liberar)
    {
        _llamadas.Add(inv.Arguments[1]);
        await liberar.Task.WaitAsync((CancellationToken)inv.Arguments[2]);
        return new PluginDaemonResponse<SkipTimesCoordinator.DeteccionTemasResult>
        {
            Success = true,
            Result = new SkipTimesCoordinator.DeteccionTemasResult
            {
                Success = true,
                Matches = [new SkipTimesCoordinator.TemaDetectado { Segment = "op", Start = 90, End = 180, Confidence = 0.9 }]
            }
        };
    }

    [Fact]
    public async Task AbrirElEpisodioQueSeEstaPreAnalizando_SeUneAEseAnalisis_SinRepetirlo()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        var liberar = new TaskCompletionSource();
        DeteccionQueEspera(liberar);
        var sut = CrearSut();

        var preanalisis = sut.PreanalizarAsync(1, 5, _episodio, CancellationToken.None);
        for (int i = 0; i < 100 && _llamadas.Count == 0; i++) await Task.Delay(10);
        var apertura = sut.CargarSkipTimesAsync(1, 5, 1400, _episodio);
        await Task.Delay(50);
        liberar.SetResult();

        (await apertura).Should().ContainSingle(r => r.EsIntro);
        (await preanalisis).Should().ContainSingle(r => r.EsIntro);
        _llamadas.Should().ContainSingle("el episodio se analiza una sola vez");
    }

    [Fact]
    public async Task SiElPreAnalisisSeCancela_QuienAbreElEpisodioLoAnalizaPorSuCuenta()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"));
        var liberar = new TaskCompletionSource();
        DeteccionQueEspera(liberar);
        var sut = CrearSut();
        using var cancelarPre = new CancellationTokenSource();

        var preanalisis = sut.PreanalizarAsync(1, 5, _episodio, cancelarPre.Token);
        for (int i = 0; i < 100 && _llamadas.Count == 0; i++) await Task.Delay(10);
        var apertura = sut.CargarSkipTimesAsync(1, 5, 1400, _episodio);
        await Task.Delay(50);
        cancelarPre.Cancel();
        for (int i = 0; i < 100 && _llamadas.Count < 2; i++) await Task.Delay(10);
        liberar.SetResult();

        (await apertura).Should().ContainSingle(r => r.EsIntro, "que se cancele el pre-análisis no cancela la apertura");
        await FluentActions.Awaiting(() => preanalisis).Should().ThrowAsync<OperationCanceledException>();
        _llamadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElPreAnalisisGuardaElResultado_ParaQueLaAperturaSeaInstantanea()
    {
        Referencias(true, Tema("OP", "OP1", null, "op1.ogg"), Tema("ED", "ED1", null, "ed1.ogg"));
        Deteccion(("op", 90, 180, 0.9), ("ed", 1300, 1390, 0.95));

        await CrearSut(conBaseDeDatos: true).PreanalizarAsync(101, 6, _episodio, CancellationToken.None);

        (_analisisGuardado!.AnimeId, _analisisGuardado.Episodio, _analisisGuardado.Completo).Should().Be((101, 6, true));
    }

    // === Vigencia y firma ===

    [Theory]
    [InlineData("f1", true, 400, true)]      // completo y del mismo archivo: vale siempre
    [InlineData("f2", true, 1, false)]       // otro archivo
    [InlineData("f1", false, 1, true)]       // incompleto pero reciente
    [InlineData("f1", false, 13, false)]     // incompleto y viejo: se repite
    public void EsAnalisisVigente_SegunFirmaCompletitudYEdad(string firmaGuardada, bool completo, int horas, bool esperado)
    {
        var ahora = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var analisis = new AnalisisSkipEpisodio { Firma = firmaGuardada, Completo = completo, FechaUtc = ahora.AddHours(-horas) };

        SkipTimesCoordinator.EsAnalisisVigente(analisis, "f1", ahora).Should().Be(esperado);
    }

    [Theory]
    [InlineData(false, -30, false)]  // incompleto y hay música más nueva que el análisis: se repite ya
    [InlineData(false, 30, true)]    // la música es anterior al análisis: nada nuevo
    [InlineData(true, -30, true)]    // completo: da igual
    public void EsAnalisisVigente_ConMusicaNuevaDelAnime(bool completo, int minutosMusicaAntesDelAnalisis, bool esperado)
    {
        var ahora = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var analisis = new AnalisisSkipEpisodio { Firma = "f1", Completo = completo, FechaUtc = ahora.AddHours(-1) };

        SkipTimesCoordinator.EsAnalisisVigente(analisis, "f1", ahora, analisis.FechaUtc.AddMinutes(-minutosMusicaAntesDelAnalisis)).Should().Be(esperado);
    }

    [Fact]
    public void EsAnalisisVigente_SinAnalisis_EsFalso() =>
        SkipTimesCoordinator.EsAnalisisVigente(null, "f1", DateTime.UtcNow).Should().BeFalse();

    [Fact]
    public void CalcularFirma_CambiaSiElArchivoCambia_YEsNulaSiNoExiste()
    {
        string f1 = SkipTimesCoordinator.CalcularFirma(_episodio)!;
        File.AppendAllText(_episodio, "más contenido");
        string f2 = SkipTimesCoordinator.CalcularFirma(_episodio)!;

        f2.Should().NotBe(f1);
        SkipTimesCoordinator.CalcularFirma(Path.Combine(_carpeta, "no-existe.mkv")).Should().BeNull();
        SkipTimesCoordinator.CalcularFirma(null).Should().BeNull();
        SkipTimesCoordinator.CalcularFirma("  ").Should().BeNull();
    }
}
