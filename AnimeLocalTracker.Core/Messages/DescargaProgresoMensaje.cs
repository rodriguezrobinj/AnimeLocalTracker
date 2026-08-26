namespace AnimeLocalTracker.Core.Messages;

public class DescargaProgresoMensaje
{
    public int AniListId { get; }
    public int NumeroEpisodio { get; }
    public double Progreso { get; }
    public bool IsDownloading { get; }
    public bool IsCompleted { get; }
    public bool IsPaused { get; }
    public string RutaArchivo { get; }
    public string? Error { get; }
    public string AnimeTitulo { get; }

    public DescargaProgresoMensaje(int aniListId, int numeroEpisodio, double progreso, bool isDownloading, bool isCompleted, bool isPaused, string rutaArchivo, string? error = null, string animeTitulo = "")
    {
        AniListId = aniListId;
        NumeroEpisodio = numeroEpisodio;
        Progreso = progreso;
        IsDownloading = isDownloading;
        IsCompleted = isCompleted;
        IsPaused = isPaused;
        RutaArchivo = rutaArchivo;
        Error = error;
        AnimeTitulo = animeTitulo;
    }
}
