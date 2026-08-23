using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IAniSkipService
{
    Task<List<AniSkipResult>> ObtenerSkipTimesAsync(int malId, int episodio, double duracionSegundos = 0, CancellationToken ct = default);
    Task<int?> ObtenerMalIdDesdeAniListAsync(int aniListId, CancellationToken ct = default);
}
