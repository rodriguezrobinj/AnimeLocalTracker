using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

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

    // === Metadata técnica (ffprobe vía Python bridge) ===
    private string _resolucion = string.Empty;
    [Ignore]
    public string Resolucion
    {
        get => _resolucion;
        set => SetProperty(ref _resolucion, value);
    }

    private string _codecVideo = string.Empty;
    [Ignore]
    public string CodecVideo
    {
        get => _codecVideo;
        set => SetProperty(ref _codecVideo, value);
    }

    private string _fps = string.Empty;
    [Ignore]
    public string Fps
    {
        get => _fps;
        set => SetProperty(ref _fps, value);
    }

    private bool _es10Bit;
    [Ignore]
    public bool Es10Bit
    {
        get => _es10Bit;
        set => SetProperty(ref _es10Bit, value);
    }

    private string? _rutaMiniatura;
    [Ignore]
    public string? RutaMiniatura
    {
        get => _rutaMiniatura;
        set => SetProperty(ref _rutaMiniatura, value);
    }

    // Badge técnico para la UI: "1080p · HEVC · 10bit · 23.98fps"
    [Ignore]
    public string? BadgeTecnico
    {
        get
        {
            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(Resolucion)) partes.Add(Resolucion);
            if (!string.IsNullOrWhiteSpace(CodecVideo)) partes.Add(CodecVideo.ToUpperInvariant());
            if (Es10Bit) partes.Add("10bit");
            if (!string.IsNullOrWhiteSpace(Fps))
            {
                var fpsNum = Fps.Split('/')[0];
                if (double.TryParse(fpsNum, out var fpsVal) && fpsVal > 0)
                    partes.Add($"{fpsVal:0.##}fps");
            }
            return partes.Count > 0 ? string.Join(" · ", partes) : null;
        }
    }

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