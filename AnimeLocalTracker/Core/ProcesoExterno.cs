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
            var salida = proceso.StandardOutput.ReadToEndAsync(corte);
            var error = proceso.StandardError.ReadToEndAsync(corte);
            await proceso.WaitForExitAsync(corte);
            return new Resultado(proceso.ExitCode, await salida, await error);
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
            using var memoria = new MemoryStream();
            var salida = proceso.StandardOutput.BaseStream.CopyToAsync(memoria, corte);
            var error = proceso.StandardError.ReadToEndAsync(corte);
            await proceso.WaitForExitAsync(corte);
            await salida;
            return new ResultadoBinario(proceso.ExitCode, memoria.ToArray(), await error);
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
