using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace AnimeLocalTracker.Models;

public partial class AnimeItem : ObservableObject
{
    [PrimaryKey] 
    public int AniListId { get; set; } 
    
    [ObservableProperty]
    private string _titulo = string.Empty;

    [ObservableProperty]
    private string _nombresAlternativos = string.Empty;
    
    [ObservableProperty]
    private string _rutaCarpeta = string.Empty;
    
    [ObservableProperty]
    private string _urlPortada = string.Empty;
    
    [ObservableProperty]
    private string _sinopsis = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerosLista))]
    private string _generos = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoPorcentaje))]
    [NotifyPropertyChangedFor(nameof(NuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(TieneNuevosEpisodios))]
    private int _totalEpisodios;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstadoVisual))]
    [NotifyPropertyChangedFor(nameof(ColorEstado))]
    private string _estado = string.Empty;

    // Estado del usuario ("CURRENT", "COMPLETED", "PLANNING", etc)
    [ObservableProperty]
    private string _estadoUsuario = string.Empty;

    // Estado transitorio para la UI de Selección Múltiple
    [property: Ignore]
    [ObservableProperty]
    private bool _estaSeleccionado;

    // === PROPIEDADES DE PROGRESO LOCAL ===
    [property: Ignore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoPorcentaje))]
    [NotifyPropertyChangedFor(nameof(NuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(TieneNuevosEpisodios))]
    private int _episodiosVistos;

    [Ignore]
    public int NuevosEpisodios => TotalEpisodios > EpisodiosVistos ? (TotalEpisodios - EpisodiosVistos) : 0;

    [Ignore]
    public double ProgresoPorcentaje => TotalEpisodios > 0 ? (EpisodiosVistos / (double)TotalEpisodios) * 100 : 0;

    [Ignore]
    public bool TieneNuevosEpisodios => NuevosEpisodios > 0;


    // === PROPIEDADES VISUALES (NO SE GUARDAN EN SQLITE) ===
    [Ignore] 
    public string EstadoVisual => Estado == "RELEASING" ? "En Emisión" : (Estado == "FINISHED" ? "Finalizado" : "Desconocido");
    
    [Ignore] 
    public string ColorEstado => Estado == "RELEASING" ? "#4CAF50" : "#9E9E9E";
    
    [Ignore]
    public string[] GenerosLista => string.IsNullOrWhiteSpace(Generos) ? [] : Generos.Split(", ");
    
    // === LÓGICA DE CACHÉ DE PORTADAS OFFLINE ===
    private string? _portadaCacheada;

    [Ignore]
    public string PortadaVisible
    {
        get
        {
            if (_portadaCacheada != null) return _portadaCacheada;
            if (string.IsNullOrWhiteSpace(UrlPortada)) return string.Empty;
            
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string directory = System.IO.Path.Combine(appData, "AnimeLocalTracker", "Covers");
            string localPath = System.IO.Path.Combine(directory, $"{AniListId}.jpg");
            
            _portadaCacheada = System.IO.File.Exists(localPath) ? localPath : UrlPortada;
            return _portadaCacheada;
        }
    }

    public void NotificarPortadaActualizada()
    {
        _portadaCacheada = null;
        OnPropertyChanged(nameof(PortadaVisible));
    }
}