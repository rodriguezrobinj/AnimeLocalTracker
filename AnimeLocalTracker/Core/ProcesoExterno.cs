using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// Null si el proceso no llegó a arrancar. Si se cancela o vence <paramref name="limite"/>, mata el proceso (y sus hijos)
    /// y lanza <see cref="OperationCanceledException"/>. Los argumentos van uno a uno: no hace falta entrecomillar rutas.
    /// </summary>
    public static async Task<Resultado?> EjecutarAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct)
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

        using var proceso = Process.Start(psi);
        if (proceso == null) return null;

        using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limite is { } maximo) corte.CancelAfter(maximo);

        try
        {
            var salida = proceso.StandardOutput.ReadToEndAsync(corte.Token);
            var error = proceso.StandardError.ReadToEndAsync(corte.Token);
            await proceso.WaitForExitAsync(corte.Token);
            return new Resultado(proceso.ExitCode, await salida, await error);
        }
        catch (OperationCanceledException)
        {
            try { if (!proceso.HasExited) proceso.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
    }
}
