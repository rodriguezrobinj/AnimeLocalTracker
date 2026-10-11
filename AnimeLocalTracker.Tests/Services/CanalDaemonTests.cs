using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>El reparto de respuestas del daemon, con un daemon de mentira: aquí se decide a quién llega cada línea.</summary>
public sealed class CanalDaemonTests : IDisposable
{
    private static readonly TimeSpan Margen = TimeSpan.FromSeconds(10);

    private readonly DaemonFalso _daemon = new();
    private readonly CanalDaemon _canal;

    public CanalDaemonTests() => _canal = new CanalDaemon(_daemon.Salida, _daemon.Entrada);

    public void Dispose() => _daemon.Dispose();

    [Fact]
    public async Task DosPeticionesALaVez_CadaUnaRecibeSuRespuesta_AunqueLleguenAlReves()
    {
        var primera = _canal.PedirAsync("resolve-stream", """{"url":"https://uno"}""", CancellationToken.None);
        var segunda = _canal.PedirAsync("resolve-stream", """{"url":"https://dos"}""", CancellationToken.None);
        var enviadaPrimera = _daemon.Recibir();
        var enviadaSegunda = _daemon.Recibir();
        enviadaPrimera["command"]!.GetValue<string>().Should().Be("resolve-stream");
        enviadaPrimera["payload"]!["url"]!.GetValue<string>().Should().Be("https://uno");

        _daemon.Responder($$"""{"success":true,"direct_url":"dos","id":{{enviadaSegunda["id"]}}}""");
        _daemon.Responder($$"""{"success":true,"direct_url":"uno","id":{{enviadaPrimera["id"]}}}""");

        (await segunda.WaitAsync(Margen)).Should().Contain("\"dos\"");
        (await primera.WaitAsync(Margen)).Should().Contain("\"uno\"");
    }

    [Fact]
    public async Task PeticionCancelada_AvisaAlDaemon_YSuRespuestaTardiaNoLlegaALaSiguiente()
    {
        // Caso real con el canal de un solo carril: el opening de un episodio acabó guardado en el siguiente.
        using var cts = new CancellationTokenSource();
        var cancelada = _canal.PedirAsync("find-duplicates", "{}", cts.Token);
        long idCancelada = _daemon.Recibir()["id"]!.GetValue<long>();

        cts.Cancel();

        await FluentActions.Awaiting(() => cancelada).Should().ThrowAsync<OperationCanceledException>();
        _daemon.Recibir()["cancel"]!.GetValue<long>().Should().Be(idCancelada);

        var siguiente = _canal.PedirAsync("ping", "{}", CancellationToken.None);
        long idSiguiente = _daemon.Recibir()["id"]!.GetValue<long>();
        _daemon.Responder($$"""{"success":true,"de":"cancelada","id":{{idCancelada}}}""");
        _daemon.Responder($$"""{"success":true,"de":"siguiente","id":{{idSiguiente}}}""");

        (await siguiente.WaitAsync(Margen)).Should().Contain("\"siguiente\"");
    }

    [Fact]
    public async Task SiElDaemonSeCierra_LasPeticionesEnCursoYLasNuevasDevuelvenNull()
    {
        var enCurso = _canal.PedirAsync("ping", "{}", CancellationToken.None);
        _daemon.Recibir();

        _daemon.Cerrar();

        (await enCurso.WaitAsync(Margen)).Should().BeNull("quien llama cae entonces al proceso de un solo uso");
        (await _canal.PedirAsync("ping", "{}", CancellationToken.None).WaitAsync(Margen)).Should().BeNull();
    }

    [Theory]
    [InlineData("{\"success\": true, \"id\": 7}", 7L)]
    [InlineData("{\"success\": true}", null)]
    [InlineData("{\"id\": \"siete\"}", null)]
    [InlineData("[1, 2]", null)]
    [InlineData("no es json", null)]
    public void IdDe_LeeElIdDeLaLinea(string linea, long? esperado)
    {
        CanalDaemon.IdDe(linea).Should().Be(esperado);
    }

    /// <summary>Las dos tuberías del daemon, en memoria: lo que la app le escribe se recoge línea a línea y la prueba decide qué contesta.</summary>
    private sealed class DaemonFalso : IDisposable
    {
        private readonly BlockingCollection<string> _recibidas = new();
        private readonly BlockingCollection<string> _respuestas = new();

        public DaemonFalso()
        {
            Entrada = new Escritor(_recibidas);
            Salida = new Lector(_respuestas);
        }

        public TextWriter Entrada { get; }
        public TextReader Salida { get; }

        public JsonNode Recibir()
        {
            _recibidas.TryTake(out var linea, Margen).Should().BeTrue("la app debía haber escrito un mensaje al daemon");
            return JsonNode.Parse(linea!)!;
        }

        public void Responder(string linea) => _respuestas.Add(linea);
        public void Cerrar() => _respuestas.CompleteAdding();

        public void Dispose()
        {
            Cerrar(); // suelta el hilo lector del canal
            _recibidas.Dispose();
            _respuestas.Dispose();
        }

        private sealed class Escritor(BlockingCollection<string> lineas) : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string? value) => lineas.Add(value ?? string.Empty);
        }

        private sealed class Lector(BlockingCollection<string> lineas) : TextReader
        {
            public override string? ReadLine() => lineas.TryTake(out var linea, Timeout.Infinite) ? linea : null;
        }
    }
}
