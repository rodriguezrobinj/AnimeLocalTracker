using System.Threading.Tasks;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Captura el fotograma actual del reproductor, lo copia al portapapeles y lo guarda como PNG.
/// </summary>
public interface IFrameCaptureService
{
    /// <summary>
    /// Devuelve la ruta del PNG guardado, o null si no se pudo capturar (Player nulo o snapshot vacío).
    /// Copia el bitmap al portapapeles antes de guardarlo. Nunca lanza: los errores se registran y
    /// se traducen a null para que el llamador decida qué mensaje mostrar.
    /// </summary>
    Task<string?> CapturarYGuardarAsync(Player? player);
}
