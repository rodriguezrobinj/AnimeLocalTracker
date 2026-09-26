using System;
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

    /// <summary>Igual que <see cref="DescargarYConvertirAsync(int, AnimeThemeInfo, CancellationToken)"/> pero informando del
    /// avance (0 a 1; la conversión final completa el 1). El informe puede llegar en cualquier hilo.</summary>
    Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, IProgress<double>? progreso, CancellationToken ct);

    /// <summary>Borra el mp3 local de este tema, si existe. Silencioso ante errores.</summary>
    void Eliminar(int aniListId, AnimeThemeInfo tema);

    /// <summary>
    /// Temas ya descargados para este anime, reconstruidos directamente del nombre de cada archivo
    /// local (sin red ni base de datos) — es lo que usa SkipTimesCoordinator para saltar OP/ED por
    /// audio de referencia sin depender de AniSkip ni de tener un segundo episodio local.
    /// </summary>
    List<TemaLocalDisponible> ListarDescargasLocales(int aniListId);

    // === VISTA PREVIA: la canción completa en una caché temporal, para escucharla antes de guardarla ===

    /// <summary>Ruta de la vista previa ya preparada de este tema, o null si no hay ninguna.</summary>
    string? ObtenerRutaVistaPrevia(int aniListId, AnimeThemeInfo tema);

    /// <summary>
    /// Baja y convierte el tema a la caché temporal (no toca la carpeta de música) y devuelve su ruta; si ya estaba
    /// preparado, la devuelve sin volver a bajar nada. Null si falla.
    /// </summary>
    Task<string?> PrepararVistaPreviaAsync(int aniListId, AnimeThemeInfo tema, IProgress<double>? progreso, CancellationToken ct);

    /// <summary>Convierte la vista previa en descarga guardada moviendo el archivo (instantáneo, sin bajar nada de nuevo).
    /// False si no había vista previa que guardar o no se pudo mover.</summary>
    bool GuardarVistaPrevia(int aniListId, AnimeThemeInfo tema);

    /// <summary>Vacía la caché de vistas previas. Silencioso ante errores.</summary>
    void LimpiarVistasPrevias();
}
