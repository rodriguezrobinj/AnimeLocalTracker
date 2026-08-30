using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace AnimeLocalTracker.Services;

public interface IHoverThumbnailService
{
    /// <summary>
    /// Precarga o genera en segundo plano la tira de fotogramas (Sprite Sheet) para el video actual.
    /// </summary>
    void PrepararSpritesheet(string rutaVideo, double duracionTotalSegundos);

    /// <summary>
    /// Retorna el fotograma instantáneo (0 ms) cortado en RAM del Sprite Sheet pre-cargado.
    /// </summary>
    ImageSource? ObtenerFrameInstantaneo(double segundos);

    /// <summary>
    /// Obtiene o genera la miniatura correspondiente al segundo solicitado del video.
    /// </summary>
    Task<ImageSource?> ObtenerMiniaturaHoverAsync(string rutaVideo, double segundos, CancellationToken ct = default);

    /// <summary>
    /// Limpia la caché en memoria de miniaturas y libera el Sprite Sheet activo.
    /// </summary>
    void LimpiarCacheMemoria();

    /// <summary>
    /// Intervalo de agrupación en segundos (comprimido para el conteo de extracciones ffmpeg).
    /// </summary>
    int BucketIntervaloSegundos { get; }
}
