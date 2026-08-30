using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace AnimeLocalTracker.Services;

public interface IHoverThumbnailService
{
    /// <summary>
    /// Obtiene o genera la miniatura correspondiente al segundo solicitado del video.
    /// </summary>
    Task<ImageSource?> ObtenerMiniaturaHoverAsync(string rutaVideo, double segundos, CancellationToken ct = default);

    /// <summary>
    /// Limpia la caché en memoria de miniaturas.
    /// </summary>
    void LimpiarCacheMemoria();

    /// <summary>
    /// Intervalo de agrupación en segundos (por defecto 4s para máxima velocidad y aciertos de caché).
    /// </summary>
    int BucketIntervaloSegundos { get; }
}
