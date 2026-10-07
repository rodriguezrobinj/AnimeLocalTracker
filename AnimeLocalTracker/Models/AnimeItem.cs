using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Models;

public partial class AnimeItem : ObservableObject
{
    [PrimaryKey] 
    public int AniListId { get; set; } 
    
    [ObservableProperty]
    private int? _malId;

    [ObservableProperty]
    private string _titulo = string.Empty;

    [ObservableProperty]
    private string _nombresAlternativos = string.Empty;
    
    [ObservableProperty]
    private string _rutaCarpeta = string.Empty;
    
    [ObservableProperty]
    private string _urlPortada = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SinopsisLimpia))]
    [NotifyPropertyChangedFor(nameof(TieneSinopsisLarga))]
    private string _sinopsis = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerosLista))]
    private string _generos = string.Empty;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoPorcentaje))]
    [NotifyPropertyChangedFor(nameof(NuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(TieneNuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(ProgresoEpisodiosTexto))]
    private int _totalEpisodios;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstadoVisual))]
    [NotifyPropertyChangedFor(nameof(ColorEstado))]
    private string _estado = string.Empty;

    // Estado del usuario ("CURRENT", "COMPLETED", "PLANNING", etc)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstadoUsuarioVisual))]
    private string _estadoUsuario = string.Empty;

    // Temporada de estreno ("WINTER", "SPRING", "SUMMER", "FALL") y año, capturados de
    // AniList al añadir el anime — alimentan los filtros de temporada/año de la galería.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemporadaVisual))]
    private string _temporada = string.Empty;

    [ObservableProperty]
    private int _anioLanzamiento;

    // Favorito individual por anime (independiente del estado Viendo/Completado/Planeando).
    [ObservableProperty]
    private bool _esFavorito;

#pragma warning disable CS0657 // 'property' target is forwarded to the generated property by CommunityToolkit.Mvvm
    // Estado transitorio para la UI de Selección Múltiple
    [property: Ignore]
    [property: JsonIgnore]
    [ObservableProperty]
    private bool _estaSeleccionado;



    // === PROPIEDADES DE PROGRESO LOCAL ===
    [property: Ignore]
    [property: JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoPorcentaje))]
    [NotifyPropertyChangedFor(nameof(NuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(TieneNuevosEpisodios))]
    [NotifyPropertyChangedFor(nameof(ProgresoEpisodiosTexto))]
    private int _episodiosVistos;
#pragma warning restore CS0657

    [Ignore]
    [JsonIgnore]
    public int NuevosEpisodios => TotalEpisodios > EpisodiosVistos ? (TotalEpisodios - EpisodiosVistos) : 0;

    [Ignore]
    [JsonIgnore]
    public double ProgresoPorcentaje => TotalEpisodios > 0 ? (EpisodiosVistos / (double)TotalEpisodios) * 100 : 0;

    [Ignore]
    [JsonIgnore]
    public bool TieneNuevosEpisodios => NuevosEpisodios > 0;

    [Ignore]
    [JsonIgnore]
    public string ProgresoEpisodiosTexto => string.Format(AnimeLocalTracker.Services.LocalizationService.T("Epi_ProgresoEpisodiosTexto"), EpisodiosVistos, TotalEpisodios);


    // === PROPIEDADES VISUALES (NO SE GUARDAN EN SQLITE) ===
    [Ignore] 
    [JsonIgnore]
    public string EstadoVisual => Estado == "RELEASING"
        ? AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoEnEmision")
        : (Estado == "FINISHED"
            ? AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoFinalizado")
            : AnimeLocalTracker.Services.LocalizationService.T("Media_EstadoDesconocido"));

    [Ignore]
    [JsonIgnore]
    public string ColorEstado => Estado == "RELEASING" ? "#4CAF50" : "#9E9E9E";

    /// <summary>
    /// Cuándo se añadió a la biblioteca (UTC), para el orden "Añadidos recientemente". Null en los animes que ya estaban
    /// antes de guardarse este dato (migración v19): quedan al final de ese orden.
    /// </summary>
    public System.DateTime? FechaAgregadoUtc { get; set; }

    /// <summary>
    /// "Conservar los videos": con esto activo, ningún modo de "Eliminar el video tras verlo" borra nada de este anime
    /// (ni pregunta). Solo protege del borrado automático: borrar a mano un episodio o "Liberar espacio" siguen funcionando.
    /// Migración v21; los animes que ya estaban quedan sin proteger.
    /// </summary>
    public bool ConservarVideos { get; set; }

    /// <summary>Tu estado con el anime, como lo enseña la tarjeta de la Galería (antes mostraba si seguía en emisión).</summary>
    [Ignore]
    [JsonIgnore]
    public string EstadoUsuarioVisual => AnimeLocalTracker.Services.LocalizationService.T(EstadoUsuario switch
    {
        "CURRENT" => "Estado_Viendo",
        "REPEATING" => "Tarjeta_ViendoDeNuevo",
        "COMPLETED" => "Tarjeta_Completado",
        "PAUSED" => "Estado_EnPausa",
        "DROPPED" => "Estado_Abandonado",
        _ => "Estado_Planeando"
    });

    /// <summary>Año y temporada de estreno en un solo número, para ordenar por "Estreno más reciente" (0 = sin datos).</summary>
    [Ignore]
    [JsonIgnore]
    public int OrdenEstreno => AnioLanzamiento <= 0 ? 0 : AnioLanzamiento * 10 + Temporada switch
    {
        "WINTER" => 1,
        "SPRING" => 2,
        "SUMMER" => 3,
        "FALL" => 4,
        _ => 0
    };

    [Ignore]
    [JsonIgnore]
    public string TemporadaVisual => Temporada switch
    {
        "WINTER" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Invierno"),
        "SPRING" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Primavera"),
        "SUMMER" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Verano"),
        "FALL" => AnimeLocalTracker.Services.LocalizationService.T("Temporada_Otonio"),
        _ => string.Empty
    };
    
    [Ignore]
    [JsonIgnore]
    public string SinopsisLimpia
    {
        get
        {
            // Cacheada: los bindings la leen en cada render de la galería/detalle y
            // el Regex sobre sinopsis largas es caro de repetir
            if (_sinopsisLimpiaCache != null) return _sinopsisLimpiaCache;
            if (string.IsNullOrWhiteSpace(Sinopsis)) return string.Empty;

            string clean = Sinopsis.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
            _sinopsisLimpiaCache = System.Text.RegularExpressions.Regex.Replace(clean, "<.*?>", string.Empty).Trim();
            return _sinopsisLimpiaCache;
        }
    }

    private string? _sinopsisLimpiaCache;

    partial void OnSinopsisChanged(string value) => _sinopsisLimpiaCache = null;

    [Ignore]
    [JsonIgnore]
    public bool TieneSinopsisLarga => !string.IsNullOrWhiteSpace(SinopsisLimpia) && (SinopsisLimpia.Length > 150 || SinopsisLimpia.Contains('\n'));

    [Ignore]
    [JsonIgnore]
    public string[] GenerosLista => string.IsNullOrWhiteSpace(Generos) 
        ? [] 
        : Generos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    
    // === LÓGICA DE CACHÉ DE PORTADAS OFFLINE ===
    private string? _portadaCacheada;

    // IMP-05 / PERF-01: Evitar File.Exists en el getter para no bloquear la UI.
    [Ignore]
    [JsonIgnore]
    public string PortadaVisible
    {
        get
        {
            if (_portadaCacheada != null) return _portadaCacheada;
            if (string.IsNullOrWhiteSpace(UrlPortada)) return string.Empty;
            
            return UrlPortada;
        }
    }

    public void ResolverPortadaLocal()
    {
        if (string.IsNullOrWhiteSpace(UrlPortada)) return;
        string localPath = System.IO.Path.Combine(Services.AppDataPaths.CoversDir, $"{AniListId}.jpg");
        if (System.IO.File.Exists(localPath))
        {
            _portadaCacheada = localPath;
        }
    }

    /// <summary>
    /// La portada ya está en disco (acaba de cargarse o descargarse): la Ficha la toma del archivo. Antes esto borraba la ruta
    /// guardada y <see cref="PortadaVisible"/> volvía a la URL: al abrir la app la Galería lo llamaba para todas las portadas, así
    /// que la Ficha las pedía a internet (sin conexión se quedaban cargando para siempre). Llamar a <see cref="ResolverPortadaLocal"/>
    /// antes, fuera del hilo de la interfaz.
    /// </summary>
    public void NotificarPortadaActualizada()
    {
        OnPropertyChanged(nameof(PortadaVisible));
    }
}