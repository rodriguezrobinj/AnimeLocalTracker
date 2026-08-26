using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Core.Models;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    private double _progresoSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    private double _totalSegundos;

    public bool EstaEnEspera => IsDownloading && DownloadProgress <= 0.0;
    public bool ProgresoDescargaActivo => IsDownloading && DownloadProgress > 0.0;

    public double PorcentajeProgreso => TotalSegundos > 0 ? Math.Clamp(ProgresoSegundos / TotalSegundos, 0.0, 1.0) : 0.0;
    
    public bool TieneProgresoGuardado => ProgresoSegundos > 5 && !Visto && (TotalSegundos <= 0 || ProgresoSegundos < TotalSegundos * 0.95);

    public string ProgresoFormateado
    {
        get
        {
            if (ProgresoSegundos <= 0) return string.Empty;
            var tCur = System.TimeSpan.FromSeconds(ProgresoSegundos);
            string curStr = tCur.ToString(tCur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
            if (TotalSegundos > 0)
            {
                var tTot = System.TimeSpan.FromSeconds(TotalSegundos);
                string totStr = tTot.ToString(tTot.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                return $"{curStr} / {totStr}";
            }
            return curStr;
        }
    }

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
