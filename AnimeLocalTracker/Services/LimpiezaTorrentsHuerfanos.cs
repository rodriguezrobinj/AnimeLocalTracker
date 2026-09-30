using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Al abrir la app: borra las carpetas temporales de torrents (una por descarga, "AniListId_Episodio")
/// que ya no pertenecen a ninguna descarga de la cola. Quedaban cuando la app se cerraba a mitad de un
/// torrent que luego no se restauraba, cuando se cerraba sembrando (el sembrado no se reanuda) o cuando
/// un fallo impedía borrarlas; nadie las reclamaba: 5 GB en el equipo del usuario, con el disco casi
/// lleno. Las de la cola (pausadas o pendientes) se conservan: al reanudar se retoman sus piezas.
/// </summary>
public static class LimpiezaTorrentsHuerfanos
{
    /// <summary>Carpetas borradas y espacio liberado.</summary>
    public readonly record struct Resultado(int Carpetas, long Bytes);

    /// <param name="carpetaBase">Carpeta que contiene una subcarpeta por torrent.</param>
    /// <param name="clavesEnCola">Claves ("AniListId_Episodio") de las descargas que siguen en la cola.</param>
    public static Resultado Limpiar(string carpetaBase, IReadOnlySet<string> clavesEnCola)
    {
        int carpetas = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(carpetaBase)) return new Resultado(0, 0);

            foreach (var carpeta in new DirectoryInfo(carpetaBase).GetDirectories())
            {
                if (clavesEnCola.Contains(carpeta.Name)) continue;
                try
                {
                    long tamano = carpeta.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    carpeta.Delete(recursive: true);
                    carpetas++;
                    bytes += tamano;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // En uso (un torrent que sigue abierto): se intentará la próxima vez.
                    AppLogger.Debug("LimpiezaTorrentsHuerfanos", $"No se pudo borrar la carpeta temporal '{carpeta.Name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LimpiezaTorrentsHuerfanos", $"Error revisando las carpetas temporales de torrents: {ex.Message}");
        }
        return new Resultado(carpetas, bytes);
    }
}
