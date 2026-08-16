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
    private string _rutaCarpeta = string.Empty;
    
    [ObservableProperty]
    private string _urlPortada = string.Empty;
    
    [ObservableProperty]
    private string _sinopsis = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerosLista))]
    private string _generos = string.Empty;
    
    [ObservableProperty]
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

    // === PROPIEDADES VISUALES (NO SE GUARDAN EN SQLITE) ===
    [Ignore] 
    public string EstadoVisual => Estado == "RELEASING" ? "En Emisión" : (Estado == "FINISHED" ? "Finalizado" : "Desconocido");
    
    [Ignore] 
    public string ColorEstado => Estado == "RELEASING" ? "#4CAF50" : "#9E9E9E";
    
    [Ignore]
    public string[] GenerosLista => string.IsNullOrWhiteSpace(Generos) ? [] : Generos.Split(", ");
    
    // === LÓGICA DE CACHÉ DE PORTADAS OFFLINE ===
    [Ignore]
    public string PortadaVisible
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UrlPortada)) return string.Empty;
            
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string directory = System.IO.Path.Combine(appData, "AnimeLocalTracker", "Covers");
            string localPath = System.IO.Path.Combine(directory, $"{AniListId}.jpg");
            
            // Si el archivo ya se descargó, usamos la ruta local
            if (System.IO.File.Exists(localPath))
            {
                return localPath;
            }
            
            // Fallback: usar la URL web
            return UrlPortada;
        }
    }

    public void NotificarPortadaActualizada()
    {
        OnPropertyChanged(nameof(PortadaVisible));
    }
}