using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Descarga la pista de audio (.ogg) de un opening/ending de AnimeThemes.moe y la convierte a
/// .mp3 con el FFmpeg embebido de la app. Ver <see cref="AnimeThemesDownloadService"/>.
/// </summary>
public interface IAnimeThemesDownloadService
{
    /// <summary>Ruta local donde quedaría (o ya está) el mp3 de este tema. No comprueba si existe.</summary>
    string ObtenerRutaLocalEsperada(int aniListId, AnimeThemeInfo tema);

    /// <summary>True si el mp3 de este tema ya está descargado localmente.</summary>
    bool EstaDescargado(int aniListId, AnimeThemeInfo tema);

    /// <summary>Descarga el .ogg y lo convierte a .mp3. Devuelve la ruta local final, o null si falla
    /// (red, ffmpeg no disponible, etc.) — el .ogg temporal nunca queda en disco tras el intento.</summary>
    Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, CancellationToken ct = default);

    /// <summary>Borra el mp3 local de este tema, si existe. Silencioso ante errores.</summary>
    void Eliminar(int aniListId, AnimeThemeInfo tema);

    /// <summary>
    /// Temas ya descargados para este anime, reconstruidos directamente del nombre de cada archivo
    /// local (sin red ni base de datos) — es lo que usa SkipTimesCoordinator para saltar OP/ED por
    /// audio de referencia sin depender de AniSkip ni de tener un segundo episodio local.
    /// </summary>
    List<TemaLocalDisponible> ListarDescargasLocales(int aniListId);
}
