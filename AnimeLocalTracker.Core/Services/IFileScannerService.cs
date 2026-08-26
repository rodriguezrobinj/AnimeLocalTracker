using AnimeLocalTracker.Core.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;

namespace AnimeLocalTracker.Core.Services;

public interface IFileScannerService
{
    Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta);
}
