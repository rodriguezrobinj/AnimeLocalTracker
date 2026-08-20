using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

public partial class DescargaItem : ObservableObject
{
    public int AniListId { get; set; }
    public string AnimeTitulo { get; set; } = string.Empty;
    public int NumeroEpisodio { get; set; }
    public string Fuente { get; set; } = "AnimeAv1";
    public string RutaArchivo { get; set; } = string.Empty;

    public string TituloEpisodio => $"Episodio {NumeroEpisodio}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoTexto))]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    private double _progreso;

    public string ProgresoTexto => Progreso > 0 ? $"{Progreso:F0}%" : "En espera...";
    public bool EstaEnEspera => Progreso <= 0.0;

    [ObservableProperty]
    private bool _isDownloading = true;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string? _error;
}
