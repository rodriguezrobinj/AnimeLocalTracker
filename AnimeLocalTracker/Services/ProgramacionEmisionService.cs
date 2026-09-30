using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>Episodios de la ventana pedida; <see cref="DesdeCopiaLocal"/> = AniList no respondió y salen de lo guardado.</summary>
public sealed record ProgramacionEmision(List<AiringEpisode> Episodios, bool DesdeCopiaLocal);

/// <summary>
/// Programación de emisión (Calendario y Actualizaciones) con copia local: con conexión se pide a AniList y se guarda; sin
/// conexión se muestra la última guardada, completada con el próximo episodio que la Ficha guarda para su cuenta atrás.
/// Antes, abrir la app sin internet dejaba las dos pestañas vacías.
/// </summary>
public interface IProgramacionEmisionService
{
    Task<ProgramacionEmision> ObtenerAsync(IReadOnlyCollection<int> animeIds, long inicioUnix, long finUnix);
}

public sealed class ProgramacionEmisionService : IProgramacionEmisionService
{
    private readonly IAnimeTrackingService _tracking;
    private readonly IDatabaseService _database;

    public ProgramacionEmisionService(IAnimeTrackingService tracking, IDatabaseService database)
    {
        _tracking = tracking;
        _database = database;
    }

    public async Task<ProgramacionEmision> ObtenerAsync(IReadOnlyCollection<int> animeIds, long inicioUnix, long finUnix)
    {
        var ids = animeIds.Distinct().ToList();
        var (exito, episodios) = await _tracking.ObtenerCalendarioEmisionAsync(ids, inicioUnix, finUnix);
        if (exito)
        {
            try
            {
                await _database.GuardarEmisionesAsync(ids, inicioUnix, finUnix, episodios.Select(AGuardada).ToList());
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProgramacionEmisionService", $"No se pudo guardar la programación: {ex.Message}");
            }
            return new ProgramacionEmision(episodios, DesdeCopiaLocal: false);
        }

        return new ProgramacionEmision(await LeerCopiaAsync(ids, inicioUnix, finUnix), DesdeCopiaLocal: true);
    }

    private async Task<List<AiringEpisode>> LeerCopiaAsync(List<int> ids, long inicioUnix, long finUnix)
    {
        var pedidos = ids.ToHashSet();
        var lista = new List<AiringEpisode>();
        var incluidos = new HashSet<(int, int)>();
        try
        {
            foreach (var e in await _database.ObtenerEmisionesAsync(inicioUnix, finUnix) ?? new List<EmisionGuardada>())
            {
                if (!pedidos.Contains(e.AniListId) || !incluidos.Add((e.AniListId, e.Episodio))) continue;
                lista.Add(new AiringEpisode
                {
                    AniListId = e.AniListId, NumeroEpisodio = e.Episodio, Titulo = e.Titulo, UrlPortada = e.UrlPortada,
                    FechaEmision = DesdeUnix(e.EmisionUnixUtc)
                });
            }

            // Lo que la Ficha guarda para su cuenta atrás cubre animes que nunca se vieron en el Calendario con conexión.
            foreach (var p in await _database.ObtenerProximasEmisionesAsync() ?? new List<ProximaEmisionLocal>())
            {
                if (p.Episodio <= 0 || p.EmisionUnixUtc < inicioUnix || p.EmisionUnixUtc > finUnix) continue;
                if (!pedidos.Contains(p.AniListId) || !incluidos.Add((p.AniListId, p.Episodio))) continue;
                lista.Add(new AiringEpisode { AniListId = p.AniListId, NumeroEpisodio = p.Episodio, FechaEmision = DesdeUnix(p.EmisionUnixUtc) });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ProgramacionEmisionService", $"No se pudo leer la programación guardada: {ex.Message}");
        }
        return lista;
    }

    /// <summary>Igual que AniListTrackingService: valor UTC con Kind Unspecified (las vistas ya lo tratan así; no reconvertir).</summary>
    private static DateTime DesdeUnix(long segundos) => DateTimeOffset.FromUnixTimeSeconds(segundos).DateTime;

    internal static EmisionGuardada AGuardada(AiringEpisode e) => new()
    {
        AniListId = e.AniListId,
        Episodio = e.NumeroEpisodio,
        EmisionUnixUtc = new DateTimeOffset(DateTime.SpecifyKind(e.FechaEmision, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        Titulo = e.Titulo ?? string.Empty,
        UrlPortada = e.UrlPortada ?? string.Empty
    };
}
