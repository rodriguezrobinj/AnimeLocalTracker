using System;
using System.Runtime.InteropServices;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Envuelve SetThreadExecutionState (kernel32) para evitar que Windows apague la pantalla o active
/// el protector de pantalla durante la reproducción de un episodio largo sin que el usuario toque
/// el mouse/teclado. ES_CONTINUOUS mantiene el estado hasta la próxima llamada; sin él, Windows
/// revertiría al comportamiento normal tras un rato.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class ScreenSaverPreventionService : IScreenSaverPreventionService
{
    [Flags]
    private enum EstadoEjecucion : uint
    {
        Continuo = 0x80000000,
        SistemaRequerido = 0x00000001,
        PantallaRequerida = 0x00000002,
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint esFlags);

    public void Activar()
    {
        try
        {
            uint resultado = SetThreadExecutionState((uint)(EstadoEjecucion.Continuo | EstadoEjecucion.SistemaRequerido | EstadoEjecucion.PantallaRequerida));
            if (resultado == 0)
            {
                AppLogger.Debug("ScreenSaverPreventionService", "SetThreadExecutionState devolvió 0 (fallo) al evitar la suspensión de pantalla.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ScreenSaverPreventionService", $"No se pudo evitar la suspensión de pantalla: {ex.Message}");
        }
    }

    public void Desactivar()
    {
        try
        {
            uint resultado = SetThreadExecutionState((uint)EstadoEjecucion.Continuo);
            if (resultado == 0)
            {
                AppLogger.Debug("ScreenSaverPreventionService", "SetThreadExecutionState devolvió 0 (fallo) al restaurar el ahorro de energía.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ScreenSaverPreventionService", $"No se pudo restaurar el ahorro de energía: {ex.Message}");
        }
    }
}
