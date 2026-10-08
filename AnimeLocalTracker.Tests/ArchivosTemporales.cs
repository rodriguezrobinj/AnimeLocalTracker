using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AnimeLocalTracker.Tests;

/// <summary>
/// Limpieza de las carpetas temporales de las pruebas. Windows no deja borrar un archivo que alguien aún tiene abierto (una imagen que
/// WPF no ha soltado, un archivo que un servicio acaba de escribir) y el recolector de basura lo libera un instante después: un
/// <c>try { Directory.Delete(…); } catch { }</c> directo se rinde a la primera y deja el resto en %TEMP% para siempre. Además, una
/// tarea en segundo plano puede volver a crear la carpeta justo después de borrarla, así que las que se pasan por aquí se vuelven a
/// barrer al terminar el proceso de pruebas, cuando ya no queda ninguna tarea.
/// </summary>
internal static class ArchivosTemporales
{
    private static readonly ConcurrentBag<string> Registradas = [];

    static ArchivosTemporales() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (string ruta in Registradas) IntentarBorrar(ruta);
        };

    /// <summary>Borra la carpeta con sus archivos reintentando unos instantes; si aun así no puede, no hace fallar la prueba.</summary>
    public static void BorrarCarpeta(string ruta)
    {
        Registradas.Add(ruta);
        for (int intento = 1; intento <= 8; intento++)
        {
            if (IntentarBorrar(ruta)) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(40 * intento);
        }
    }

    private static bool IntentarBorrar(string ruta)
    {
        try
        {
            if (Directory.Exists(ruta)) Directory.Delete(ruta, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
