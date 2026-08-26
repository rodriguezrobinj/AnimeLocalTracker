using AnimeLocalTracker.Core.Services;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

/// <summary>
/// Datos crudos de una sesión de reproducción que se desean persistir.
/// </summary>
public class DatosProgresoReproduccion
{
    public int AnimeId { get; set; }
    public int NumeroEpisodio { get; set; }
    public string RutaVideo { get; set; } = string.Empty;
    public double PosicionSegundos { get; set; }
    public double DuracionSegundos { get; set; }
    public bool ForzarProgresoCero { get; set; }
    public bool FueMarcadoComoVisto { get; set; }
}

/// <summary>
/// Resultado efectivo del guardado (tras aplicar las reglas de negocio).
/// </summary>
public record ResultadoGuardadoProgreso(double ProgresoSegundos, double TotalSegundos);

/// <summary>
/// Responsable único de la persistencia del estado de reproducción:
/// reanudación de posición, guardado periódico de progreso y auto-tracking (local + AniList).
/// </summary>
public interface IPlaybackStateService
{
    /// <summary>
    /// Devuelve la posición desde la cual reanudar y la duración registrada,
    /// o null si no hay un progreso previo válido (regla &gt;5s y &lt;95% de duración).
    /// </summary>
    Task<(double Posicion, double Duracion)?> ObtenerPosicionParaReanudarAsync(int animeId, int episodio);

    /// <summary>
    /// Aplica las reglas de negocio (limpieza al 95%, mínimo de 3 segundos) y persiste el registro.
    /// Devuelve los valores efectivamente guardados para notificación a la UI.
    /// </summary>
    Task<ResultadoGuardadoProgreso> GuardarProgresoAsync(DatosProgresoReproduccion datos);

    /// <summary>
    /// Marca el episodio como visto localmente (progreso a 0) y sincroniza con AniList si hay token.
    /// Devuelve true si el flujo completo terminó sin errores.
    /// </summary>
    Task<bool> MarcarComoVistoYSincronizarAsync(int animeId, int episodio, string rutaVideo, double duracionSegundos);
}
