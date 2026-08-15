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

    // === PROPIEDADES VISUALES (NO SE GUARDAN EN SQLITE) ===
    [Ignore] 
    public string EstadoVisual => Estado == "RELEASING" ? "En Emisión" : (Estado == "FINISHED" ? "Finalizado" : "Desconocido");
    
    [Ignore] 
    public string ColorEstado => Estado == "RELEASING" ? "#4CAF50" : "#9E9E9E";
    
    [Ignore]
    public string[] GenerosLista => string.IsNullOrWhiteSpace(Generos) ? [] : Generos.Split(", ");
}