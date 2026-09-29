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
    /// <summary>Carpeta donde se guardan los mp3 de este anime (puede no existir todavía).</summary>
    string CarpetaDescargas(int aniListId);

    /// <summary>Ruta local donde quedaría (o ya está) el mp3 de este tema. No comprueba si existe.</summary>
    string ObtenerRutaLocalEsperada(int aniListId, AnimeThemeInfo tema);

    /// <summary>True si el mp3 de este tema ya está descargado localmente.</summary>
    bool EstaDescargado(int aniListId, AnimeThemeInfo tema);

    /// <summary>True si la descarga de este tema sigue en marcha (p. ej. empezó antes de salir de la ficha). Pedirla otra vez
    /// no lanza una segunda descarga: se une a la que ya está en curso.</summary>
    bool EstaDescargando(int aniListId, AnimeThemeInfo tema);

    /// <summary>
    /// Pone al día el nombre de los mp3 ya descargados cuyo tema sigue en el catálogo con otro rango de episodios (un anime en
    /// emisión pasa de "1-" a "1-12" al terminar la temporada). Sin esto el archivo quedaría huérfano y la ficha lo mostraría
    /// como no descargado. Devuelve cuántos se renombraron. Silencioso ante errores.
    /// </summary>
    int ReconciliarDescargasLocales(int aniListId, IReadOnlyList<AnimeThemeInfo> catalogo);

    /// <summary>Descarga el .ogg y lo convierte a .mp3. Devuelve la ruta local final, o null si falla
    /// (red, ffmpeg no disponible, etc.) — el .ogg temporal nunca queda en disco tras el intento.</summary>
    Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, CancellationToken ct = default);

    /// <summary>Igual que <see cref="DescargarYConvertirAsync(int, AnimeThemeInfo, CancellationToken)"/> pero informando del
    /// avance (0 a 1; la conversión final completa el 1). El informe puede llegar en cualquier hilo.</summary>
    Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, IProgress<double>? progreso, CancellationToken ct);

    /// <summary>
    /// Pone título, artista, anime (álbum) y portada a los mp3 ya descargados de este anime que aún no los tienen. Copia el
    /// audio tal cual (sin volver a descargar ni convertir). Un archivo en uso se deja como está. Devuelve cuántos etiquetó.
    /// </summary>
    Task<int> EtiquetarDescargasLocalesAsync(int aniListId, IReadOnlyList<AnimeThemeInfo> catalogo, CancellationToken ct = default);

    /// <summary>Animes con al menos un mp3 descargado (según las carpetas de la carpeta de música).</summary>
    IReadOnlyList<int> AnimesConDescargas();

    /// <summary>Cuántos mp3 de este anime tienen aún el nombre técnico antiguo o no tienen etiquetas. Sin red.</summary>
    int ContarPendientesDeOrganizar(int aniListId);

    // === PESTAÑA DESCARGAS: las descargas de música salen junto a las de episodios ===

    /// <summary>Descargas de música en marcha (no las vistas previas), listas para la pestaña Descargas.</summary>
    IReadOnlyList<DescargaItem> ObtenerDescargasMusicaActivas();

    /// <summary>Cancela la descarga en marcha de ese tema. False si no había ninguna.</summary>
    bool CancelarDescargaMusica(int aniListId, string temaClave);

    /// <summary>Cancela todas las descargas de música en marcha.</summary>
    void CancelarTodasMusica();

    /// <summary>
    /// True (una sola vez) si la última descarga de ese tema la canceló el usuario desde la pestaña Descargas: la ficha no
    /// debe avisar de un fallo que no lo es.
    /// </summary>
    bool FueCanceladaPorUsuario(int aniListId, AnimeThemeInfo tema);

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
