using System;
using System.IO;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Rutas de datos del usuario separadas del directorio de instalación.
/// IMPORTANTE: Velopack instala en %LocalAppData%\AnimeLocalTracker y al desinstalar
/// elimina TODO ese directorio. Los datos (biblioteca, token, portadas, miniaturas,
/// logs, cachés) se guardan aquí, en %LocalAppData%\AnimeLocalTrackerData, para que
/// una desinstalación NO los borre nunca.
/// </summary>
public static class AppDataPaths
{
    /// <summary>Raíz de datos de usuario (fuera del control de Velopack).</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AnimeLocalTrackerData");

    public static string LogsDir { get; } = Path.Combine(DataRoot, "Logs");
    public static string CoversDir { get; } = Path.Combine(DataRoot, "Covers");
    public static string ThumbnailsDir { get; } = Path.Combine(DataRoot, "Thumbnails");
    public static string BibliotecaDb { get; } = Path.Combine(DataRoot, "biblioteca.db");
    public static string TokenPath { get; } = Path.Combine(DataRoot, "anilist_token.txt");
    public static string SettingsPath { get; } = Path.Combine(DataRoot, "settings.json");
    public static string ReleaseInfoPath { get; } = Path.Combine(DataRoot, "release_info.json");

    /// <summary>Ubicación heredada (pre-v5) de settings y caché de release: %AppData%\AnimeLocalTracker.</summary>
    private static string RutaRoamingAntigua() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnimeLocalTracker");

    /// <summary>
    /// Migra los datos guardados por versiones antiguas (ubicados dentro del directorio
    /// de instalación, %LocalAppData%\AnimeLocalTracker) a la nueva raíz segura.
    /// Best-effort: si falla, se conserva el original y se continúa con la raíz nueva.
    /// Se ejecuta una sola vez al arrancar, antes de inicializar la base de datos.
    /// </summary>
    public static void MigrarDesdeInstalacionAntigua()
    {
        try
        {
            var antigua = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimeLocalTracker");

            if (!Directory.Exists(antigua) || string.Equals(antigua, DataRoot, StringComparison.OrdinalIgnoreCase))
                return;

            // No migrar si el usuario ya tiene datos en la raíz nueva
            if (Directory.Exists(DataRoot) && Directory.EnumerateFileSystemEntries(DataRoot).Any())
                return;

            Directory.CreateDirectory(DataRoot);

            MoverArchivoSiExiste(Path.Combine(antigua, "biblioteca.db"), BibliotecaDb);
            MoverArchivoSiExiste(Path.Combine(antigua, "biblioteca.db-wal"), BibliotecaDb + "-wal");
            MoverArchivoSiExiste(Path.Combine(antigua, "biblioteca.db-shm"), BibliotecaDb + "-shm");
            MoverArchivoSiExiste(Path.Combine(antigua, "anilist_token.txt"), TokenPath);
            MoverCarpetaSiExiste(Path.Combine(antigua, "Covers"), CoversDir);
            MoverCarpetaSiExiste(Path.Combine(antigua, "Thumbnails"), ThumbnailsDir);
            MoverCarpetaSiExiste(Path.Combine(antigua, "Logs"), LogsDir);

            AppLogger.Info("AppDataPaths", "Datos migrados correctamente a la nueva ubicación segura.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AppDataPaths", $"Migración de datos antiguos fallida (se continuará con la raíz nueva): {ex.Message}");
        }
    }

    private static void MoverArchivoSiExiste(string origen, string destino)
    {
        try
        {
            if (File.Exists(origen) && !File.Exists(destino))
                File.Move(origen, destino);
        }
        catch { }
    }

    private static void MoverCarpetaSiExiste(string origen, string destino)
    {
        try
        {
            if (Directory.Exists(origen) && !Directory.Exists(destino))
                Directory.Move(origen, destino);
        }
        catch { }
    }

    /// <summary>
    /// ARQ-02: migra un archivo de la ubicación heredada en %AppData%\AnimeLocalTracker
    /// (Roaming) a la raíz segura de datos si el destino aún no existe. Best-effort.
    /// </summary>
    public static void MigrarArchivoDesdeRoaming(string nombreArchivo, string destino)
    {
        try
        {
            string origen = Path.Combine(RutaRoamingAntigua(), nombreArchivo);
            if (File.Exists(origen) && !File.Exists(destino))
            {
                Directory.CreateDirectory(DataRoot);
                File.Copy(origen, destino);
                AppLogger.Info("AppDataPaths", $"'{nombreArchivo}' migrado de Roaming a la carpeta de datos segura.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AppDataPaths", $"No se pudo migrar '{nombreArchivo}' desde Roaming: {ex.Message}");
        }
    }
}
