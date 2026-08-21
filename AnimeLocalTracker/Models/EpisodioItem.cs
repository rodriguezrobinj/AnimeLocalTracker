using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

// Debe ser partial y heredar de ObservableObject
public partial class EpisodioItem : ObservableObject 
{
    // Nombre que se mostrará en pantalla (Ej: "Episodio 1")
    public string TituloVisual => $"Episodio {NumeroEpisodio}"; 
    
    public string TituloArchivo { get; set; } = string.Empty;
    public string RutaCompleta { get; set; } = string.Empty;
    public int NumeroEpisodio { get; set; }

    [ObservableProperty]
    private bool _visto; 
    
    [ObservableProperty]
    private bool _favorito;
    
    [ObservableProperty]
    private bool _descargado;
    
    [ObservableProperty]
    private string _tamanoArchivoFormateado = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    private bool _isDownloading;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    private double _downloadProgress;

    public bool EstaEnEspera => IsDownloading && DownloadProgress <= 0.0;
    public bool ProgresoDescargaActivo => IsDownloading && DownloadProgress > 0.0;

    public void CalcularTamanoArchivo(long len)
    {
        if (len >= 1024L * 1024 * 1024)
            TamanoArchivoFormateado = $"{len / (1024.0 * 1024.0 * 1024.0):F1} GB";
        else if (len >= 1024L * 1024)
            TamanoArchivoFormateado = $"{len / (1024.0 * 1024.0):F0} MB";
        else if (len >= 1024L)
            TamanoArchivoFormateado = $"{len / 1024.0:F0} KB";
        else if (len >= 0)
            TamanoArchivoFormateado = $"{len} B";
        else
            TamanoArchivoFormateado = string.Empty;
    }

    public void CalcularTamanoArchivo()
    {
        if (!string.IsNullOrEmpty(RutaCompleta) && System.IO.File.Exists(RutaCompleta))
        {
            try
            {
                var len = new System.IO.FileInfo(RutaCompleta).Length;
                CalcularTamanoArchivo(len);
            }
            catch
            {
                TamanoArchivoFormateado = string.Empty;
            }
        }
        else
        {
            TamanoArchivoFormateado = string.Empty;
        }
    }
}