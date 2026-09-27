using System;
using System.IO;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Limpieza de archivos "Episodio *.downloading" (y su .state) que dejaron descargas abandonadas.
/// Compartida por los escáneres de carpetas: el escaneo corre al abrir la ficha, Actualizaciones,
/// Historial y en el monitor de emisión, así que NO puede borrar parciales de descargas en pausa,
/// esperando un reintento o que el usuario retomará tras reiniciar la app — su archivo está
/// cerrado en esos momentos y borrarlo hacía perder el progreso (o, con el .state huérfano,
/// terminar un video con tramos a ceros). Solo se borran los que llevan días sin tocarse.
/// </summary>
public static class LimpiezaDescargasParciales
{
    public static readonly TimeSpan AntiguedadMinima = TimeSpan.FromDays(7);

    public static void LimpiarAbandonados(DirectoryInfo carpeta, DateTime ahoraUtc)
    {
        try
        {
            foreach (var parcial in carpeta.EnumerateFiles("Episodio *.downloading", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (ahoraUtc - parcial.LastWriteTimeUtc < AntiguedadMinima) continue;

                    parcial.Delete();
                    var estado = new FileInfo(parcial.FullName + ".state");
                    if (estado.Exists) estado.Delete();
                }
                catch (IOException)
                {
                    // Archivo en uso por una descarga activa: omitir limpiamente
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("LimpiezaDescargasParciales", $"No se pudo eliminar el parcial '{parcial.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LimpiezaDescargasParciales", $"Error al limpiar parciales en {carpeta.FullName}: {ex.Message}");
        }
    }
}
