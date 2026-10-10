using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Ejecuta un programa auxiliar (ffmpeg, ffprobe…) sin ventana y recoge lo que escribe. Reúne lo que antes repetía cada
/// servicio: leer las dos salidas a la vez (un pipe redirigido sin lector puede colgar el proceso, visto con ffmpeg), un
/// tiempo límite y matar el proceso si se cancela, porque no se detiene solo al soltar el <see cref="Process"/>.
/// </summary>
public static class ProcesoExterno
{
    public sealed record Resultado(int Codigo, string Salida, string Error);

    /// <summary>Como <see cref="Resultado"/>, con la salida en bytes (audio o imagen que el programa escribe por su salida).</summary>
    public sealed record ResultadoBinario(int Codigo, byte[] Salida, string Error);

    /// <summary>
    /// Null si el proceso no llegó a arrancar. Si se cancela o vence <paramref name="limite"/>, mata el proceso (y sus hijos)
    /// y lanza <see cref="OperationCanceledException"/>. Los argumentos van uno a uno: no hace falta entrecomillar rutas.
    /// <paramref name="prioridad"/>: para trabajo de fondo (miniaturas) que no debe quitarle procesador al reproductor.
    /// </summary>
    public static async Task<Resultado?> EjecutarAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct,
        ProcessPriorityClass? prioridad = null)
    {
        using var proceso = Iniciar(ejecutable, argumentos, prioridad);
        if (proceso == null) return null;

        return await HastaQueTermineAsync(proceso, limite, async corte =>
        {
            var salida = EnHiloPropio(proceso.StandardOutput.ReadToEnd);
            var error = EnHiloPropio(proceso.StandardError.ReadToEnd);
            await proceso.WaitForExitAsync(corte);
            return new Resultado(proceso.ExitCode, await salida.WaitAsync(corte), await error.WaitAsync(corte));
        }, ct);
    }

    /// <summary>
    /// Igual que <see cref="EjecutarAsync"/>, pero devuelve la salida tal cual, en bytes: para el audio decodificado que
    /// ffmpeg escribe por su salida (leído como texto se estropea).
    /// </summary>
    public static async Task<ResultadoBinario?> EjecutarBinarioAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct)
    {
        using var proceso = Iniciar(ejecutable, argumentos, null);
        if (proceso == null) return null;

        return await HastaQueTermineAsync(proceso, limite, async corte =>
        {
            var salida = EnHiloPropio(() =>
            {
                using var memoria = new MemoryStream();
                proceso.StandardOutput.BaseStream.CopyTo(memoria);
                return memoria.ToArray();
            });
            var error = EnHiloPropio(proceso.StandardError.ReadToEnd);
            await proceso.WaitForExitAsync(corte);
            return new ResultadoBinario(proceso.ExitCode, await salida.WaitAsync(corte), await error.WaitAsync(corte));
        }, ct);
    }

    private static Process? Iniciar(string ejecutable, IEnumerable<string> argumentos, ProcessPriorityClass? prioridad)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ejecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argumento in argumentos) psi.ArgumentList.Add(argumento);

        var proceso = Process.Start(psi);
        if (proceso != null && prioridad is { } clase)
        {
            try { proceso.PriorityClass = clase; } catch { /* ya terminó: nada que bajar */ }
        }
        return proceso;
    }

    /// <summary>
    /// Lee una salida del proceso en un hilo aparte. Windows no deja leer estos pipes sin esperar, así que ReadToEndAsync dejaba
    /// ocupado un hilo del grupo por cada salida mientras el proceso viviera: con varios ffmpeg lentos a la vez (miniaturas en
    /// prioridad baja con el procesador ocupado), el resto del trabajo en segundo plano de la app esperaba segundos.
    /// </summary>
    private static Task<T> EnHiloPropio<T>(Func<T> leer)
    {
        // El hilo se arranca desde el grupo y no desde quien llama (puede ser el hilo de la interfaz): arrancarlo espera a que
        // eche a andar, y con el procesador ocupado eso son decenas de milisegundos.
        var lectura = Task.Run(() => Task.Factory.StartNew(leer, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        // Si el proceso se corta nadie espera ya esta lectura: que su fallo (pipe cerrado) no llegue al registro como error sin atender.
        _ = lectura.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return lectura;
    }

    private static async Task<T> HastaQueTermineAsync<T>(Process proceso, TimeSpan? limite, Func<CancellationToken, Task<T>> leer, CancellationToken ct)
    {
        using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limite is { } maximo) corte.CancelAfter(maximo);

        try
        {
            return await leer(corte.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proceso.HasExited) proceso.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
    }
}
