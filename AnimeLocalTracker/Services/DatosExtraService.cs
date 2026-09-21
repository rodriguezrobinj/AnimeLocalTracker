using System;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IDatosExtraService
{
    /// <summary>
    /// Datos de AniList para las etiquetas de la ficha, desde la copia local. Solo consulta a AniList si no hay copia o
    /// tiene más de una semana (nota, estudio, formato… casi no cambian); sin red devuelve la copia que haya.
    /// </summary>
    Task<DatosExtraAnime?> ObtenerAsync(int aniListId);
}

public sealed class DatosExtraService : IDatosExtraService
{
    internal static readonly TimeSpan Vigencia = TimeSpan.FromDays(7);
    /// <summary>Tras un intento fallido con copia vieja, no se insiste en cada visita.</summary>
    internal static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromHours(6);

    private readonly IDatabaseService _database;
    private readonly IAnimeTrackingService _tracking;

    public DatosExtraService(IDatabaseService database, IAnimeTrackingService tracking)
    {
        _database = database;
        _tracking = tracking;
    }

    public async Task<DatosExtraAnime?> ObtenerAsync(int aniListId)
    {
        if (aniListId <= 0) return null;

        DatosExtraAnime? local = null;
        try { local = await _database.ObtenerDatosExtraAsync(aniListId); }
        catch (Exception ex) { AppLogger.Debug("DatosExtraService", $"No se pudo leer la copia local de {aniListId}: {ex.Message}"); }

        if (!DebeConsultar(local, DateTime.UtcNow)) return local;

        var (exito, media) = await _tracking.ObtenerDatosExtraAsync(aniListId);
        if (exito && media != null)
        {
            local = Convertir(aniListId, media, DateTime.UtcNow);
        }
        else if (local != null)
        {
            local.ConsultadoUtc = DateTime.UtcNow - Vigencia + EsperaTrasFallo; // volverá a intentarlo dentro de EsperaTrasFallo
        }
        else
        {
            return null;
        }

        try { await _database.GuardarDatosExtraAsync(local); }
        catch (Exception ex) { AppLogger.Debug("DatosExtraService", $"No se pudo guardar la copia local de {aniListId}: {ex.Message}"); }
        return local;
    }

    internal static bool DebeConsultar(DatosExtraAnime? local, DateTime ahoraUtc)
    {
        if (local == null) return true;
        var consultado = local.ConsultadoUtc.Kind == DateTimeKind.Local ? local.ConsultadoUtc.ToUniversalTime() : DateTime.SpecifyKind(local.ConsultadoUtc, DateTimeKind.Utc);
        return ahoraUtc - consultado > Vigencia;
    }

    internal static DatosExtraAnime Convertir(int aniListId, AniListMedia media, DateTime ahoraUtc) => new()
    {
        AniListId = aniListId,
        Formato = media.Format ?? string.Empty,
        DuracionMin = media.Duration ?? 0,
        NotaMedia = media.AverageScore ?? 0,
        Estudio = media.Studios?.Nodes?.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
        Fuente = media.Source ?? string.Empty,
        TrailerId = media.Trailer?.Id ?? string.Empty,
        TrailerSitio = media.Trailer?.Site ?? string.Empty,
        ConsultadoUtc = ahoraUtc
    };

    /// <summary>URL https del tráiler (solo YouTube y Dailymotion, los sitios que da AniList); null si no hay o no es seguro.</summary>
    public static string? UrlTrailer(DatosExtraAnime? datos)
    {
        if (datos == null || string.IsNullOrWhiteSpace(datos.TrailerId)) return null;
        string id = Uri.EscapeDataString(datos.TrailerId.Trim());
        return datos.TrailerSitio?.ToLowerInvariant() switch
        {
            "youtube" => $"https://www.youtube.com/watch?v={id}",
            "dailymotion" => $"https://www.dailymotion.com/video/{id}",
            _ => null
        };
    }
}
