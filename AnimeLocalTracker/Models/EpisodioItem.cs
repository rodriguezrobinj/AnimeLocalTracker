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
    
    // NUEVO: Bandera para saber si el archivo existe en el disco duro
    [ObservableProperty]
    private bool _descargado;
}