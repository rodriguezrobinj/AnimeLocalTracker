using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Flujo de detección de temas sobre huellas sintéticas (sin ffmpeg): el tema se "incrusta" en la huella del episodio y se
/// comprueba dónde lo encuentra, qué grupo de temas se llega a decodificar y cómo se guarda la huella. La comparación la hace
/// el núcleo Rust de verdad. Port de las pruebas de tools/python/tests/test_audio_skip_plugin.py.
/// </summary>
public sealed class DetectorTemasAudioTests : IDisposable
{
    private const int Fps = AudioSintetico.Fps;
    private const int C = AudioSintetico.Columnas;
    private const double Confianza = 0.7, Inicio = 480, Final = 360;

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());
    private readonly string _huellas;
    private readonly string _episodio;
    private readonly Dictionary<string, float[]> _temas = new();
    private readonly List<string> _decodificados = new();
    private float[] _sonido = [];
    private double _duracion = 1400;

    public DetectorTemasAudioTests()
    {
        Directory.CreateDirectory(_carpeta);
        _huellas = Path.Combine(_carpeta, "huellas");
        _episodio = Path.Combine(_carpeta, "Episodio 01.mkv");
        File.WriteAllBytes(_episodio, [1]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private string Tema(string nombre, float[] huella)
    {
        string ruta = Path.Combine(_carpeta, nombre);
        // Contenido distinto por tema: la caché de huellas se indexa por contenido y dos temas iguales compartirían huella.
        File.WriteAllBytes(ruta, System.Text.Encoding.UTF8.GetBytes(nombre));
        _temas[ruta] = huella;
        return ruta;
    }

    /// <summary>El episodio "suena" como <see cref="_sonido"/>: cada petición devuelve el trozo de huella que le toca.</summary>
    private float[] Tramo(double desde, double? duracion)
    {
        int total = _sonido.Length / C;
        int a = Math.Min(total, (int)(desde * Fps));
        int b = duracion is { } d ? Math.Min(total, (int)((desde + d) * Fps)) : total;
        return _sonido.AsSpan(a * C, (b - a) * C).ToArray();
    }

    private DetectorTemasAudio CrearSut() => new(_huellas,
        (_, _) => Task.FromResult(_duracion),
        (_, desde, duracion, _) => Task.FromResult<float[]?>(Tramo(desde, duracion)),
        (ruta, _) =>
        {
            lock (_decodificados) _decodificados.Add(Path.GetFileName(ruta));
            return Task.FromResult<float[]?>(_temas[ruta]);
        });

    private Task<SkipTimesCoordinator.DeteccionTemasResult?> Detectar(params ReferenciaAudio[] referencias) =>
        CrearSut().DetectarAsync(_episodio, referencias, Confianza, Inicio, Final, CancellationToken.None);

    private static float[] R(double segundos, uint semilla) => AudioSintetico.RuidoSegundos(segundos, semilla);

    [Fact]
    public async Task UbicaOpeningYEndingConSusTemas()
    {
        float[] op = R(90, 1), ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120), ed, 1300);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0));

        r!.Success.Should().BeTrue();
        var opening = r.Matches.Single(m => m.Segment == "op");
        var ending = r.Matches.Single(m => m.Segment == "ed");
        opening.Start.Should().BeApproximately(120, 0.2);
        opening.End.Should().BeApproximately(210, 0.2);
        opening.Confidence.Should().BeGreaterThan(0.9);
        opening.Mode.Should().Be("full");
        ending.Start.Should().BeApproximately(1300, 0.2);
        Path.GetFileName(ending.ReferencePath).Should().Be("ED1.mp3");
        r.Evaluated.Should().Be(2);
    }

    [Fact]
    public async Task UnOpeningTrasUnaAperturaLarga_SeBuscaHastaLaMitadDelEpisodio()
    {
        // Sasaki to Pii-chan 2, episodio 1 (47 min): el opening suena a los 8:21, fuera de los primeros 480 s.
        _duracion = 2840;
        float[] op = R(88.6, 1), ed = R(88.5, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(2840, 3), op, 501.5), ed, 2750.5);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0));

        var opening = r!.Matches.Single(m => m.Segment == "op");
        opening.Start.Should().BeApproximately(501.5, 0.2);
        opening.End.Should().BeApproximately(590.1, 0.2);
        r.Matches.Single(m => m.Segment == "ed").Start.Should().BeApproximately(2750.5, 0.2);
    }

    [Fact]
    public async Task UnOpeningACaballoDelLimiteDeLosPrimerosMinutos_SeEncuentra()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 440); // 440-530 s: no cabe entero en los primeros 480

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        var m = r!.Matches.Should().ContainSingle().Subject;
        (m.Segment, m.Mode).Should().Be(("op", "full"));
        m.Start.Should().BeApproximately(440, 0.2);
    }

    [Fact]
    public async Task ElOpeningSonandoEnLaSegundaMitad_NoSeMarcaComoOpening()
    {
        // Canción de fondo del clímax: marcarla haría que "saltar opening" se llevara la escena.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 800);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task UnTemaQueNoSuenaEnElEpisodio_NoSeAcepta()
    {
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Tema("OP9.mp3", R(90, 7)), "OP", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task LosTemasQueNoAplican_NiSeDecodificanSiAciertaUnoQueAplica()
    {
        float[] op = R(90, 1), ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 60), ed, 1300);

        await Detectar(new(Tema("OP2.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0),
            new(Tema("OP1.mp3", R(90, 5)), "OP", 1), new(Tema("OP3.mp3", R(90, 6)), "OP", 1));

        _decodificados.Should().BeEquivalentTo("OP2.mp3", "ED1.mp3");
    }

    [Fact]
    public async Task SiNingunoQueAplicaAcierta_PruebaLosDemas()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 60);

        var r = await Detectar(new(Tema("OP2.mp3", R(90, 5)), "OP", 0), new(Tema("OP1.mp3", op), "OP", 1));

        Path.GetFileName(r!.Matches[0].ReferencePath).Should().Be("OP1.mp3");
    }

    [Fact]
    public async Task UnOpeningQueSuenaAlFinal_EsElEndingDelEpisodio()
    {
        // Episodio 1 / final de temporada: no hay ending propio y el opening cierra el episodio.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 1310);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", R(90, 2)), "ED", 0));

        r!.Matches.Select(m => m.Segment).Should().Equal("ed");
        r.Matches[0].Start.Should().BeApproximately(1310, 0.2);
    }

    [Fact]
    public async Task EnUnEpisodioCorto_ElEndingNoPuedeSerElMismoOpening()
    {
        // Episodio de 4 minutos: los tramos de inicio y final cubren el episodio entero.
        _duracion = 256;
        float[] op = R(60, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(256, 3), op, 24);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        r!.Matches.Select(m => m.Segment).Should().Equal("op");
    }

    [Fact]
    public async Task UnEndingRecortado_SeUbicaPorTrozos()
    {
        // El episodio solo usa los últimos 60 s del tema (los primeros 30 s suenan sobre otra escena distinta).
        float[] ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), ed, 1330, desde: 30 * Fps);

        var r = await Detectar(new ReferenciaAudio(Tema("ED1.mp3", ed), "ED", 0));

        var m = r!.Matches.Should().ContainSingle().Subject;
        (m.Segment, m.Mode).Should().Be(("ed", "partial"));
        m.Start.Should().BeApproximately(1330, 5);
        m.End.Should().BeApproximately(1390, 5);
        m.Confidence.Should().BeGreaterThanOrEqualTo(0.7);
    }

    [Fact]
    public async Task SinReferenciasQueExistan_RespondeVacioSinMirarElEpisodio()
    {
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Path.Combine(_carpeta, "no_existe.ogg"), "OP", 0),
            new ReferenciaAudio("https://animethemes.moe/a.ogg", "ED", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
        _decodificados.Should().BeEmpty();
    }

    [Fact]
    public async Task SiElEpisodioNoExiste_RespondeConElMotivo()
    {
        var r = await CrearSut().DetectarAsync(Path.Combine(_carpeta, "no_existe.mkv"), [new(Tema("OP1.mp3", R(90, 1)), "OP", 0)],
            Confianza, Inicio, Final, CancellationToken.None);

        r!.Success.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SiNoSePuedeExtraerElAudio_RespondeConElMotivo()
    {
        // Foco de revisión 2: episodio sin pista de audio o que ffmpeg no lee → huella vacía.
        _sonido = [];

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", R(90, 1)), "OP", 0));

        r!.Success.Should().BeFalse();
        r.Error.Should().Contain("audio");
    }

    [Fact]
    public async Task SiNoSeConoceLaDuracion_RespondeConElMotivo()
    {
        _duracion = 0;
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", R(90, 1)), "OP", 0));

        r!.Success.Should().BeFalse();
        r.Error.Should().Contain("duración");
    }

    [Fact]
    public async Task UnTemaDeSilencio_SeIgnoraYSiguenLosDemas()
    {
        // Foco de revisión 4: un tema que es todo silencio (o dura menos de 5 s) no sirve, pero no estropea el resto.
        // Aquí se usa la lectura real de temas (con recorte de silencio y caché): el "audio" de cada tema sale de _temas.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120);
        string silencio = Tema("OP_silencio.mp3", new float[90 * Fps * C]);
        string corto = Tema("OP_corto.mp3", R(3, 8));
        string bueno = Tema("OP1.mp3", op);
        var sut = new DetectorTemasAudio(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, _) => Task.FromResult<float[]?>(_temas.TryGetValue(ruta, out var tema) ? tema : Tramo(desde, duracion)),
            huellaDeTema: null);

        var r = await sut.DetectarAsync(_episodio, [new(silencio, "OP", 0), new(corto, "OP", 0), new(bueno, "OP", 0)],
            Confianza, Inicio, Final, CancellationToken.None);

        r!.Success.Should().BeTrue();
        Path.GetFileName(r.Matches.Single().ReferencePath).Should().Be("OP1.mp3");
        r.Evaluated.Should().Be(1, "los otros dos no llegan a ser candidatos");
    }

    [Fact]
    public async Task LaHuellaDelTema_SeGuardaYNoSeVuelveADecodificar()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120);
        string tema = Path.Combine(_carpeta, "OP1.mp3");
        File.WriteAllBytes(tema, new byte[4000]);
        int decodificaciones = 0;
        DetectorTemasAudio Sut() => new(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, _) =>
            {
                if (ruta != tema) return Task.FromResult<float[]?>(Tramo(desde, duracion));
                Interlocked.Increment(ref decodificaciones);
                return Task.FromResult<float[]?>(op);
            },
            huellaDeTema: null);

        var primera = await Sut().DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);
        var segunda = await Sut().DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);

        decodificaciones.Should().Be(1, "la segunda vez la huella sale del disco");
        segunda!.Matches.Single().Start.Should().Be(primera!.Matches.Single().Start);
        Directory.GetFiles(_huellas, "*.huella").Should().ContainSingle();
    }

    [Fact]
    public async Task LaCancelacionSePropaga_YNoDejaArchivosTemporales()
    {
        // Foco de revisión 5: cambiar de episodio a mitad del análisis.
        _sonido = R(1400, 3);
        using var cts = new CancellationTokenSource();
        string tema = Path.Combine(_carpeta, "OP1.mp3");
        File.WriteAllBytes(tema, new byte[4000]);
        var sut = new DetectorTemasAudio(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, ct) =>
            {
                if (ruta != tema) return Task.FromResult<float[]?>(Tramo(desde, duracion));
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<float[]?>(null);
            },
            huellaDeTema: null);

        Func<Task> accion = () => sut.DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, cts.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        if (Directory.Exists(_huellas)) Directory.GetFiles(_huellas).Should().BeEmpty();
    }

    [Fact]
    public async Task ElCalculoNoVuelveAlHiloDeQuienLlama()
    {
        // En la app quien llama es el hilo de la interfaz (el reproductor): si el detector siguiera ahí tras cada espera, calcular
        // las huellas (decenas de ms) daría un tirón al video al abrir el episodio. Con el plugin Python todo iba en otro proceso.
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), R(90, 1), 120);
        string tema = Tema("OP1.mp3", R(90, 1));
        var contextos = new System.Collections.Concurrent.ConcurrentBag<SynchronizationContext?>();
        var sut = new DetectorTemasAudio(_huellas,
            (_, _) => { contextos.Add(SynchronizationContext.Current); return Task.FromResult(_duracion); },
            (_, desde, duracion, _) => { contextos.Add(SynchronizationContext.Current); return Task.FromResult<float[]?>(Tramo(desde, duracion)); },
            (ruta, _) => { contextos.Add(SynchronizationContext.Current); return Task.FromResult<float[]?>(_temas[ruta]); });
        var anterior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            await sut.DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(anterior);
        }

        contextos.Should().NotBeEmpty();
        contextos.Should().OnlyContain(c => c == null, "todo el trabajo va al grupo de hilos, sin contexto de interfaz");
    }

    [Fact]
    public void RecortarSilencio_QuitaLosExtremosSinSonido()
    {
        var huella = new float[100 * C];
        for (int f = 20; f < 70; f++) huella[f * C] = 0.5f;

        DetectorTemasAudio.RecortarSilencio(huella).Length.Should().Be(50 * C);
        DetectorTemasAudio.RecortarSilencio(new float[30 * C]).Should().BeEmpty();
    }

    [Fact]
    public async Task ConFfmpegDeVerdad_UbicaUnTemaDentroDeUnEpisodio()
    {
        // Integración: 60 s de "episodio" (ruido rosa) con 20 s de "tema" (otro ruido, otra semilla) pegados en el segundo 15.
        string tema = Path.Combine(_carpeta, "tema.wav");
        string episodio = Path.Combine(_carpeta, "episodio.wav");
        await Ffmpeg("-f", "lavfi", "-i", "anoisesrc=d=20:c=brown:r=8000:s=7", "-ac", "1", tema);
        await Ffmpeg("-f", "lavfi", "-i", "anoisesrc=d=15:c=pink:r=8000:s=1", "-i", tema, "-f", "lavfi", "-i", "anoisesrc=d=25:c=pink:r=8000:s=2",
            "-filter_complex", "[0:a][1:a][2:a]concat=n=3:v=0:a=1", "-ac", "1", episodio);

        var r = await new DetectorTemasAudio(_huellas).DetectarAsync(episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);

        r.Should().NotBeNull();
        r!.Success.Should().BeTrue(r.Error);
        var m = r.Matches.First(x => x.Segment == "op");
        m.Start.Should().BeApproximately(15, 0.3);
        m.End.Should().BeApproximately(35, 0.3);
        m.Confidence.Should().BeGreaterThan(0.9);
    }

    private static async Task Ffmpeg(params string[] argumentos)
    {
        string[] todos = ["-y", "-hide_banner", "-loglevel", "error", .. argumentos];
        var resultado = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, todos, TimeSpan.FromSeconds(60), CancellationToken.None);
        resultado!.Codigo.Should().Be(0, resultado.Error);
    }
}
