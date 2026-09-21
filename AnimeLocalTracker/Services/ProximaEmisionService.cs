using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>Próximo episodio de un anime en emisión (momento de emisión en UTC).</summary>
public sealed record ProximaEmision(int Episodio, DateTime EmisionUtc);

public interface IProximaEmisionService
{
    /// <summary>
    /// Próxima emisión de un anime, leída de la copia local. Solo consulta AniList si la copia falta o si puede haber
    /// cambiado la programación; si la consulta falla devuelve la copia que hubiera. Null = sin próximo episodio
    /// (anime que no está en emisión, sin programación o sin datos todavía).
    /// </summary>
    /// <param name="forzar">Ignora la antigüedad de la copia (botón "actualizar").</param>
    Task<ProximaEmision?> ObtenerAsync(int aniListId, string estadoAnime, bool forzar = false);
}

/// <summary>
/// Cuenta atrás del próximo episodio con caché local. La hora de emisión no cambia sola, así que el contador corre en
/// el equipo sin red; AniList solo se consulta para detectar retrasos o cambios de programación, y CON MÁS FRECUENCIA
/// cuanto más cerca está la emisión (los retrasos se anuncian a última hora):
/// más de 3 días → cada 24 h · entre 6 h y 3 días → cada 6 h · menos de 6 h → cada hora.
/// Al pasar la hora de emisión se pregunta por el episodio siguiente (con un mínimo de 15 min entre intentos, porque
/// AniList tarda un poco en actualizarlo).
/// </summary>
public sealed class ProximaEmisionService : IProximaEmisionService
{
    internal static readonly TimeSpan RevisionLejana = TimeSpan.FromHours(24);
    internal static readonly TimeSpan RevisionCercana = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RevisionInminente = TimeSpan.FromHours(1);
    internal static readonly TimeSpan RevisionTrasEmision = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RevisionSinProgramacion = TimeSpan.FromHours(24);

    private readonly IDatabaseService _database;
    private readonly IAnimeTrackingService _tracking;

    public ProximaEmisionService(IDatabaseService database, IAnimeTrackingService tracking)
    {
        _database = database;
        _tracking = tracking;
    }

    public async Task<ProximaEmision?> ObtenerAsync(int aniListId, string estadoAnime, bool forzar = false)
    {
        if (aniListId <= 0 || estadoAnime is not ("RELEASING" or "NOT_YET_RELEASED")) return null;

        ProximaEmisionLocal? local = null;
        try { local = await _database.ObtenerProximaEmisionAsync(aniListId); }
        catch (Exception ex) { AppLogger.Debug("ProximaEmisionService", $"No se pudo leer la copia local de {aniListId}: {ex.Message}"); }

        if (forzar || DebeConsultar(local, DateTime.UtcNow))
        {
            var (exito, proximo) = await _tracking.ObtenerProximaEmisionAsync(aniListId);
            if (exito)
            {
                local = new ProximaEmisionLocal
                {
                    AniListId = aniListId,
                    Episodio = proximo?.Episode ?? 0,
                    EmisionUnixUtc = proximo?.AiringAt ?? 0,
                    ConsultadoUtc = DateTime.UtcNow
                };
                try { await _database.GuardarProximaEmisionAsync(local); }
                catch (Exception ex) { AppLogger.Debug("ProximaEmisionService", $"No se pudo guardar la copia local de {aniListId}: {ex.Message}"); }
            }
            else if (local != null)
            {
                // Sin red: se conserva la copia y se anota el intento para no reintentar en cada visita.
                local.ConsultadoUtc = DateTime.UtcNow;
                try { await _database.GuardarProximaEmisionAsync(local); } catch { /* solo es una optimización */ }
            }
        }

        return Convertir(local);
    }

    /// <summary>¿Puede haber cambiado la programación desde la última consulta? (regla de revisión escalonada, ver la clase).</summary>
    internal static bool DebeConsultar(ProximaEmisionLocal? local, DateTime ahoraUtc)
    {
        if (local == null) return true;

        var consultado = AUtc(local.ConsultadoUtc);
        var desdeConsulta = ahoraUtc - consultado;

        if (local.Episodio <= 0 || local.EmisionUnixUtc <= 0)
        {
            return desdeConsulta > RevisionSinProgramacion;
        }

        var emision = DateTimeOffset.FromUnixTimeSeconds(local.EmisionUnixUtc).UtcDateTime;
        var faltan = emision - ahoraUtc;

        if (faltan <= TimeSpan.Zero)
        {
            // Ya se emitió: hay que averiguar el siguiente. Si nunca se consultó DESPUÉS de la emisión, se hace ya;
            // si AniList aún no lo había actualizado, se reintenta cada 15 min.
            return consultado < emision || desdeConsulta > RevisionTrasEmision;
        }

        var intervalo = faltan > TimeSpan.FromDays(3) ? RevisionLejana
            : faltan > TimeSpan.FromHours(6) ? RevisionCercana
            : RevisionInminente;
        return desdeConsulta > intervalo;
    }

    /// <summary>
    /// Copia local a partir de los datos de una consulta que YA se hizo (p. ej. "Actualizar biblioteca" de la galería,
    /// que trae 50 animes por petición): así se refrescan todos los contadores sin peticiones extra.
    /// Null si el dato no sirve (sin hora de emisión, p. ej. datos de una consulta antigua sin ese campo).
    /// </summary>
    public static ProximaEmisionLocal? CopiaDesdeConsulta(int aniListId, string? estadoAnime, AniListNextAiringEpisode? proximo, DateTime ahoraUtc)
    {
        if (aniListId <= 0 || estadoAnime is not ("RELEASING" or "NOT_YET_RELEASED")) return null;
        if (proximo != null && (proximo.Episode <= 0 || proximo.AiringAt <= 0)) return null;

        return new ProximaEmisionLocal
        {
            AniListId = aniListId,
            Episodio = proximo?.Episode ?? 0,
            EmisionUnixUtc = proximo?.AiringAt ?? 0,
            ConsultadoUtc = ahoraUtc
        };
    }

    private static ProximaEmision? Convertir(ProximaEmisionLocal? local)
    {
        if (local == null || local.Episodio <= 0 || local.EmisionUnixUtc <= 0) return null;
        return new ProximaEmision(local.Episodio, DateTimeOffset.FromUnixTimeSeconds(local.EmisionUnixUtc).UtcDateTime);
    }

    private static DateTime AUtc(DateTime fecha) => fecha.Kind == DateTimeKind.Local ? fecha.ToUniversalTime() : DateTime.SpecifyKind(fecha, DateTimeKind.Utc);
}
