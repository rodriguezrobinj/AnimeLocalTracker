using System.Threading.Tasks;
using System.Windows.Media;

namespace AnimeLocalTracker.Services;

public interface IImageCacheService
{
    /// <summary>
    /// Obtiene la imagen de portada optimizada y congelada (Frozen) desde memoria o caché local en disco.
    /// </summary>
    ImageSource? ObtenerPortada(int animeId, string? urlPortada, int decodeWidth = 220);

    /// <summary>
    /// Carga o descarga la portada de forma asíncrona en segundo plano y la congela para uso directo en la UI.
    /// </summary>
    Task<ImageSource?> ObtenerPortadaAsync(int animeId, string? urlPortada, int decodeWidth = 220);

    /// <summary>
    /// Invalida la caché en memoria para un anime específico.
    /// </summary>
    void InvalidarCache(int animeId);
}
