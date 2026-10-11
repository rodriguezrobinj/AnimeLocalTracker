using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Python;

/// <summary>
/// El canal con el daemon Python: una línea JSON por mensaje en cada sentido. Puede haber varias peticiones en curso a la vez; cada
/// respuesta vuelve a la suya por el "id", así que una lenta no hace esperar a las demás y cancelar una no afecta al resto: se avisa
/// al daemon con {"cancel": id} y, si su respuesta llega de todos modos, se descarta (nunca se entrega a otra petición).
/// </summary>
internal sealed class CanalDaemon
{
    private readonly TextWriter _haciaElDaemon;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _enCurso = new();
    private long _ultimoId;
    private volatile bool _cerrado;

    public CanalDaemon(TextReader desdeElDaemon, TextWriter haciaElDaemon)
    {
        _haciaElDaemon = haciaElDaemon;
        // En un hilo propio: leer de la tubería espera sin soltar el hilo, y este lector vive tanto como el daemon.
        Task.Factory.StartNew(() => Leer(desdeElDaemon), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Envía la petición y devuelve la línea de su respuesta, o null si el daemon se cerró sin darla.</summary>
    public async Task<string?> PedirAsync(string comando, string datosJson, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _ultimoId);
        var respuesta = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _enCurso[id] = respuesta;
        try
        {
            if (_cerrado) return null;
            Escribir(new JsonObject { ["id"] = id, ["command"] = comando, ["payload"] = JsonNode.Parse(datosJson) });
            return await respuesta.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { Escribir(new JsonObject { ["cancel"] = id }); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* el daemon ya no está: no hay a quién avisar */ }
            throw;
        }
        finally
        {
            _enCurso.TryRemove(id, out _);
        }
    }

    private void Escribir(JsonObject mensaje)
    {
        lock (_haciaElDaemon)
        {
            _haciaElDaemon.WriteLine(mensaje.ToJsonString());
            _haciaElDaemon.Flush();
        }
    }

    private void Leer(TextReader desdeElDaemon)
    {
        try
        {
            while (desdeElDaemon.ReadLine() is { } linea)
            {
                // Sin petición en curso con ese id, la línea es la respuesta de una que se canceló: se descarta.
                if (IdDe(linea) is { } id && _enCurso.TryRemove(id, out var respuesta)) respuesta.TrySetResult(linea);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // tubería rota: el daemon murió
        }
        finally
        {
            _cerrado = true;
            foreach (var respuesta in _enCurso.Values) respuesta.TrySetResult(null);
        }
    }

    /// <summary>El "id" que trae una línea del daemon, o null si no es un objeto JSON con id numérico.</summary>
    internal static long? IdDe(string linea)
    {
        try
        {
            return JsonNode.Parse(linea) is JsonObject objeto && objeto.TryGetPropertyValue("id", out var valor)
                && valor is JsonValue numero && numero.TryGetValue(out long id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
