using System;
using System.IO;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Core;

/// <summary>
/// El borrado de un episodio del disco: el video (con reintentos) y su miniatura. Lo comparten el borrado manual de la
/// ficha y "Eliminar tras ver", para que los dos hagan exactamente lo mismo.
/// </summary>
public static class BorradoDeEpisodio
{
    /// <summary>
    /// Borra el video y devuelve los bytes que liberó (0 si ya no existía). Si el video no se puede borrar tras los
    /// reintentos, lanza la excepción y NO toca la miniatura. La miniatura es opcional: la ficha puede tenerla abierta en
    /// pantalla y Windows negar el borrado; un fallo ahí se ignora (si no, un video ya borrado seguiría apareciendo en la
    /// lista). Bloquea el hilo mientras espera: llamarlo desde un hilo de trabajo (<c>Task.Run</c>), nunca desde la interfaz.
    /// </summary>
    public static long BorrarVideoYMiniatura(string rutaVideo, int intentos = BorradoDeArchivos.IntentosPorDefecto)
    {
        long bytes = 0;
        if (File.Exists(rutaVideo))
        {
            bytes = new FileInfo(rutaVideo).Length;
            BorradoDeArchivos.BorrarConReintentos(rutaVideo, intentos);
        }

        try
        {
            string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideo);
            if (File.Exists(miniatura)) File.Delete(miniatura);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("BorradoDeEpisodio", $"No se pudo borrar la miniatura de '{rutaVideo}': {ex.Message}");
        }

        return bytes;
    }
}
