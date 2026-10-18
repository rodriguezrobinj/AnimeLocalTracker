using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Clips;

/// <summary>Crea clips MP4/GIF de un tramo de un episodio con el ffmpeg embebido.</summary>
public interface IClipService
{
    /// <summary>Dónde se guardan los clips (Vídeos\AnimeLocalTracker).</summary>
    string Carpeta { get; }

    /// <summary>
    /// Ruta del clip creado, o null si no se pudo (el motivo va al registro). Si <see cref="SolicitudClip.RutaSalida"/> ya existe
    /// se guarda como "nombre (2)". Nunca lanza.
    /// </summary>
    Task<string?> CrearAsync(SolicitudClip solicitud, CancellationToken ct);
}
