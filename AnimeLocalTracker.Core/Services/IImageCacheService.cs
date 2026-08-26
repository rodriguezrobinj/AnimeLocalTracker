using AnimeLocalTracker.Core.Services;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

public interface IImageCacheService
{
    /// <summary>
    /// Obtiene la imagen de portada optimizada y congelada (Frozen) desde memoria o caché local en disco.
    /// </summary>
    byte[]? ObtenerPortada(int animeId, string? urlPortada, int decodeWidth = 220);

    /// <summary>
    /// Carga o descarga la portada de forma asíncrona en segundo plano y la congela para uso directo en la UI.
    /// </summary>
    Task<byte[]?> ObtenerPortadaAsync(int animeId, string? urlPortada, int decodeWidth = 220);

    /// <summary>
    /// Invalida la caché en memoria para un anime específico.
    /// </summary>
    void InvalidarCache(int animeId);
}
