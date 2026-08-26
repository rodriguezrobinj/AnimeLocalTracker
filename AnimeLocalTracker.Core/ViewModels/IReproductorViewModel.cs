using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using System.Collections.Generic;

namespace AnimeLocalTracker.Core.ViewModels;

public interface IReproductorViewModel : IDisposable
{
    Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, IEnumerable<EpisodioItem>? episodiosDisponibles);
}
