using System;
using System.IO;
using System.Threading;

namespace AnimeLocalTracker.Core;

/// <summary>Borrado de archivos que otro componente de la propia app puede tener abierto un instante.</summary>
public static class BorradoDeArchivos
{
    public const int IntentosPorDefecto = 15;
    public const int EsperaEntreIntentosMs = 200;

    /// <summary>
    /// Borra el archivo insistiendo un momento si Windows dice que está en uso. Recién abierta la ficha, la extracción de
    /// miniaturas tiene abierto cada video unos instantes: sin esperar, el borrado fallaba y el episodio se quedaba en disco.
    /// Si tras los reintentos sigue en uso, lanza la excepción. Bloquea el hilo mientras espera: llamarlo desde un hilo de
    /// trabajo (Task.Run), nunca desde la interfaz.
    /// </summary>
    public static void BorrarConReintentos(string ruta, int intentos = IntentosPorDefecto, int esperaMs = EsperaEntreIntentosMs)
    {
        for (int intento = 1; ; intento++)
        {
            try
            {
                File.Delete(ruta);
                return;
            }
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) && intento < intentos)
            {
                Thread.Sleep(esperaMs);
            }
        }
    }
}
