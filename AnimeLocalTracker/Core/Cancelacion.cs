using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace AnimeLocalTracker.Core;

/// <summary>
/// El gesto de "olvida la operación anterior y empieza otra": cancelar, liberar y reponer el
/// <see cref="CancellationTokenSource"/> de un campo. Antes se escribía a mano en cada sitio.
/// </summary>
public static class Cancelacion
{
    /// <summary>Cancela y libera el que hubiera y deja uno nuevo en el campo. Devuelve el nuevo.</summary>
    public static CancellationTokenSource Reemplazar([NotNull] ref CancellationTokenSource? cts)
    {
        Detener(ref cts);
        return cts = new CancellationTokenSource();
    }

    /// <summary>Cancela y libera el que hubiera y deja el campo vacío.</summary>
    public static void Detener(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }
}
