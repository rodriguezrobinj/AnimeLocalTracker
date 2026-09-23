using System;
using Microsoft.Win32;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Arranque automático con Windows vía el registro HKCU\...\Run (no requiere permisos de
/// administrador, a diferencia de una tarea programada). El comando incluye "--bandeja" para que
/// <c>App.OnStartup</c> arranque la ventana oculta en la bandeja del sistema en vez de mostrarla
/// (ver <see cref="ISystemTrayService.IniciarEnBandeja"/>) — sin eso, arrancar con Windows solo
/// añadiría una ventana más para cerrar/minimizar cada vez que enciendes el PC.
/// </summary>
public class StartupService : IStartupService
{
    private const string RutaClave = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string NombreValor = "AnimeLocalTracker";

    public bool EstaHabilitado()
    {
        try
        {
            using var clave = Registry.CurrentUser.OpenSubKey(RutaClave, writable: false);
            return clave?.GetValue(NombreValor) is string;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("StartupService", $"No se pudo leer el registro de inicio con Windows: {ex.Message}");
            return false;
        }
    }

    public void Sincronizar(bool habilitado)
    {
        try
        {
            using var clave = Registry.CurrentUser.OpenSubKey(RutaClave, writable: true);
            if (clave == null) return;

            if (habilitado)
            {
                string? rutaExe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(rutaExe)) return;
                clave.SetValue(NombreValor, $"\"{rutaExe}\" --bandeja");
            }
            else if (clave.GetValue(NombreValor) != null)
            {
                clave.DeleteValue(NombreValor, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StartupService", $"No se pudo actualizar el arranque con Windows: {ex.Message}");
        }
    }
}
