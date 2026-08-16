using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public interface IFileScannerService
{
    Task<List<EpisodioItem>> EscanearEpisodiosAsync(string carpeta);
}
