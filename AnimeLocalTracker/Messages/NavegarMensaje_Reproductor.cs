using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Messages;

public class NavegarMensaje_Reproductor
{
    public string RutaVideo { get; }
    public int AnimeId { get; }
    public string TituloAnime { get; }
    public int Episodio { get; }

    public NavegarMensaje_Reproductor(string rutaVideo, int animeId, string tituloAnime, int episodio)
    {
        RutaVideo = rutaVideo;
        AnimeId = animeId;
        TituloAnime = tituloAnime;
        Episodio = episodio;
    }
}
