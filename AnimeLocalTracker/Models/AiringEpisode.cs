using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

public class AiringEpisode : ObservableObject
{
    public int AniListId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string UrlPortada { get; set; } = string.Empty;

    private System.Windows.Media.ImageSource? _portada;
    /// <summary>
    /// La portada ya decodificada (pequeña y congelada) de la caché que comparte con la Galería. Enlazar la ruta del archivo
    /// hacía que WPF la decodificara a tamaño completo en el hilo de la interfaz al pintarla: ~0,5 s congelada la app.
    /// </summary>
    public System.Windows.Media.ImageSource? Portada
    {
        get => _portada;
        set => SetProperty(ref _portada, value);
    }
    public int NumeroEpisodio { get; set; }
    public DateTime FechaEmision { get; set; }
    
    public DayOfWeek DiaSemana => FechaEmision.ToLocalTime().DayOfWeek;
    public string HoraEmisionFormateada => FechaEmision.ToLocalTime().ToString("HH:mm");

    /// <summary>True si la hora de emisión (hora local) ya pasó: el episodio está disponible/emitido.</summary>
    public bool EstaEmitido => FechaEmision.ToLocalTime() <= DateTime.Now;

    /// <summary>
    /// Notifica a la UI el cambio de estado cuando la hora de emisión se cruza en vivo
    /// (el badge "EMITIDO/PRÓXIMO" quedaba obsoleto hasta el siguiente ACTUALIZAR).
    /// </summary>
    public void ActualizarEstadoEmitido()
    {
        OnPropertyChanged(nameof(EstaEmitido));
    }
}
