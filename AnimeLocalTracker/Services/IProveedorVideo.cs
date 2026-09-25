using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Fuente de video intercambiable para resolver la URL de un episodio (Fase A
/// multi-fuente). Cada proveedor es independiente (sitio o agregador); el
/// orquestador los prueba por prioridad y salud, con degradación por fallos.
/// </summary>
public interface IProveedorVideo
{
    /// <summary>Nombre para logs y diagnóstico.</summary>
    string Nombre { get; }

    /// <summary>
    /// Resuelve la URL directa del episodio (archivo) o un manifiesto HLS/DASH
    /// (que el descargador procesa con el daemon). Null si el proveedor no tiene
    /// el episodio o falló. aniListId permite verificar la identidad del anime
    /// (MAL ID) y evitar confusiones entre títulos parecidos. audioPreferido es
    /// una PREFERENCIA con fallback (AppSettings.PreferenciaAudioAnimeAv1, "SUB"/"DUB"):
    /// si la pista pedida no está disponible para el episodio, se resuelve igual en la otra.
    /// servidorPreferido también es preferencia con fallback (AppSettings.ServidorPreferidoAnimeAv1,
    /// ej. "MP4Upload"): se prueba primero y, si falla, se sigue con el resto en el orden de siempre.
    /// </summary>
    Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken ct = default);

    /// <summary>
    /// Extrae la URL directa desde una página concreta del proveedor (solo si la
    /// página pertenece a sus dominios). Null en caso contrario.
    /// </summary>
    Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default);
}
