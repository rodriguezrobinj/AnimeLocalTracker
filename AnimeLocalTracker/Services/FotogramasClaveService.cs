using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>Dirección de un salto del reproductor: decide hacia qué lado puede caer el fotograma clave elegido.</summary>
public enum TipoSalto
{
    /// <summary>Clic en la barra o posición concreta: vale un fotograma clave un poco antes o después.</summary>
    Libre,
    /// <summary>Adelantar N s: no debe quedarse corto de más.</summary>
    Adelante,
    /// <summary>Retroceder N s: no debe quedarse corto de más hacia atrás.</summary>
    Atras,
    /// <summary>Saltar opening/ending/resumen: nunca caer dentro del tramo (antes del destino).</summary>
    SaltarTramo
}

public interface IFotogramasClaveService
{
    /// <summary>Instantes (s) de los fotogramas clave del video, ordenados. Null si no se pudieron leer.</summary>
    Task<IReadOnlyList<double>?> ObtenerAsync(string rutaVideo, CancellationToken ct);
}

/// <summary>
/// Lista de fotogramas clave de un episodio (ffprobe, solo lee paquetes: &lt;1 s para un episodio de 24 min). Con ella el reproductor
/// salta al instante cuando hay un fotograma clave cerca del destino y solo usa el salto preciso —que en AV1 decodifica hasta ~10 s de
/// video: 0,7-1 s de espera medidos en un i5-7300U— cuando no lo hay. Caché de los últimos episodios abiertos.
/// </summary>
public sealed class FotogramasClaveService : IFotogramasClaveService
{
    private static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(20);
    private const int MaximoEnCache = 4;

    private readonly object _candado = new();
    private readonly LinkedList<(string Clave, IReadOnlyList<double> Instantes)> _cache = new();

    public async Task<IReadOnlyList<double>?> ObtenerAsync(string rutaVideo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rutaVideo) || !File.Exists(rutaVideo)) return null;

        string clave;
        try
        {
            var info = new FileInfo(rutaVideo);
            clave = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception) { return null; }

        lock (_candado)
        {
            for (var nodo = _cache.First; nodo != null; nodo = nodo.Next)
            {
                if (nodo.Value.Clave != clave) continue;
                _cache.Remove(nodo);
                _cache.AddFirst(nodo);
                return nodo.Value.Instantes;
            }
        }

        var instantes = await LeerAsync(rutaVideo, ct);
        if (instantes == null) return null;

        lock (_candado)
        {
            _cache.AddFirst((clave, instantes));
            while (_cache.Count > MaximoEnCache) _cache.RemoveLast();
        }
        return instantes;
    }

    private static async Task<IReadOnlyList<double>?> LeerAsync(string rutaVideo, CancellationToken ct)
    {
        string[] argumentos = ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", rutaVideo];
        try
        {
            var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe, argumentos, TiempoMaximo, ct);
            if (resultado is not { Codigo: 0 }) return null;

            var instantes = Parsear(resultado.Salida);
            return instantes.Count > 0 ? instantes : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FotogramasClaveService", $"No se pudieron leer los fotogramas clave de '{rutaVideo}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Líneas "pts_time,flags" de ffprobe → instantes de los paquetes con la marca K (fotograma clave), ordenados.</summary>
    internal static List<double> Parsear(string salida)
    {
        var resultado = new List<double>();
        foreach (var linea in salida.Split('\n'))
        {
            var partes = linea.Trim().Split(',');
            if (partes.Length < 2 || !partes[1].Contains('K')) continue;
            if (double.TryParse(partes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t >= 0) resultado.Add(t);
        }
        resultado.Sort();
        return resultado;
    }

    /// <summary>
    /// Destino real del salto: el fotograma clave más cercano al segundo pedido dentro de la ventana que admite el tipo de salto (salto
    /// rápido, instantáneo) o, si no hay ninguno, el segundo pedido con salto preciso. Ventanas elegidas con los fotogramas clave reales
    /// de la biblioteca (AV1 de AnimeAV1: 3-5 s de media entre ellos, hasta 10,4 s): adelantar/retroceder salen instantáneos el 88-95 %
    /// de las veces con ~1,5 s de error medio, el clic en la barra el 75-87 %.
    /// - Adelante/Atrás: hasta 4 s de más o de menos, pero siempre al menos la mitad del paso en la dirección pedida (antes, con el
    ///   salto rápido a secas, "adelantar 10 s" podía quedarse en el mismo sitio).
    /// - Libre (clic): ±3 s (en la barra, un píxel ya son ~1 s de episodio).
    /// - Saltar tramo: como mucho 0,5 s antes del final del opening/ending y hasta 3 s después.
    /// Pedir el fotograma clave + 0,05 s asegura que el salto rápido (que cae en el fotograma clave anterior al pedido) aterrice en él.
    /// </summary>
    public static (double Destino, bool Preciso) ElegirDestino(IReadOnlyList<double>? claves, double objetivo, TipoSalto tipo, double desde = double.NaN)
    {
        if (claves == null || claves.Count == 0) return (objetivo, true);

        double paso = double.IsNaN(desde) ? 0 : Math.Abs(objetivo - desde);
        (double min, double max) = tipo switch
        {
            TipoSalto.Adelante => (double.IsNaN(desde) ? objetivo - 4 : Math.Max(objetivo - 4, desde + paso / 2), objetivo + 4),
            TipoSalto.Atras => (objetivo - 4, double.IsNaN(desde) ? objetivo + 4 : Math.Min(objetivo + 4, desde - paso / 2)),
            TipoSalto.SaltarTramo => (objetivo - 0.5, objetivo + 3),
            _ => (objetivo - 3, objetivo + 3)
        };
        if (max < min) return (objetivo, true);

        double? mejor = null;
        for (int i = BuscarPrimeroMayorOIgual(claves, min); i < claves.Count && claves[i] <= max; i++)
        {
            if (mejor == null || Math.Abs(claves[i] - objetivo) < Math.Abs(mejor.Value - objetivo)) mejor = claves[i];
        }

        return mejor is double k ? (k + 0.05, false) : (objetivo, true);
    }

    /// <summary>
    /// Modo "Rápidos": siempre a un fotograma clave (instantáneo), el más cercano al segundo pedido que respete la dirección (adelantar
    /// avanza al menos la mitad del paso; retroceder, igual hacia atrás; saltar un tramo nunca cae más de 0,5 s dentro). Sin lista de
    /// fotogramas clave, salto rápido directo al segundo pedido.
    /// </summary>
    public static (double Destino, bool Preciso) ElegirDestinoRapido(IReadOnlyList<double>? claves, double objetivo, TipoSalto tipo, double desde = double.NaN)
    {
        if (claves == null || claves.Count == 0) return (objetivo, false);

        double paso = double.IsNaN(desde) ? 0 : Math.Abs(objetivo - desde);
        double? mejor = null;
        foreach (double k in claves)
        {
            bool valido = tipo switch
            {
                TipoSalto.Adelante => double.IsNaN(desde) || k >= desde + paso / 2,
                TipoSalto.Atras => double.IsNaN(desde) || k <= desde - paso / 2,
                TipoSalto.SaltarTramo => k >= objetivo - 0.5,
                _ => true
            };
            if (valido && (mejor == null || Math.Abs(k - objetivo) < Math.Abs(mejor.Value - objetivo))) mejor = k;
        }
        return mejor is double m ? (m + 0.05, false) : (objetivo, true);
    }

    private static int BuscarPrimeroMayorOIgual(IReadOnlyList<double> claves, double valor)
    {
        int bajo = 0, alto = claves.Count;
        while (bajo < alto)
        {
            int medio = (bajo + alto) / 2;
            if (claves[medio] < valor) bajo = medio + 1;
            else alto = medio;
        }
        return bajo;
    }
}
