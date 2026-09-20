namespace AnimeLocalTracker.Messages;

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
    public string VelocidadDescarga { get; }
    /// <summary>Velocidad suavizada en bytes/s (0 si no se conoce).</summary>
    public double VelocidadBps { get; }
    /// <summary>La descarga espera un slot libre (aún no ha empezado a transferir).</summary>
    public bool EnCola { get; }
    /// <summary>Reintentos automáticos por cortes de red realizados hasta ahora.</summary>
    public int Reintentos { get; }

    public DescargaProgresoMensaje(int aniListId, int numeroEpisodio, double progreso, bool isDownloading, bool isCompleted, bool isPaused, string rutaArchivo, string? error = null, string animeTitulo = "", string velocidadDescarga = "", double velocidadBps = 0, bool enCola = false, int reintentos = 0)
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
        VelocidadDescarga = velocidadDescarga;
        VelocidadBps = velocidadBps;
        EnCola = enCola;
        Reintentos = reintentos;
    }
}
