using System;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Un opening/ending en la lista de la Ficha. Estados posibles: sin nada → preparando la vista previa → vista previa lista
/// (solo escuchable, sin guardar) → descargado (guardado en la carpeta de música). Además, si es la pista cargada en el
/// reproductor, lleva su posición y duración para la barra.
/// </summary>
public partial class TemaAnimeItem : ObservableObject
{
    public required AnimeThemeInfo Info { get; init; }

    public string Slug => Info.Slug;
    public string Tipo => Info.Tipo;
    public string TituloCancion => string.IsNullOrWhiteSpace(Info.TituloCancion) ? Slug : Info.TituloCancion;
    public string Artistas => Info.Artistas;
    public string RangoTexto => string.IsNullOrWhiteSpace(Info.RangoEpisodios)
        ? LocalizationService.T("Det_MusicaTodosLosEpisodios")
        : string.Format(LocalizationService.T("Det_MusicaEpisodiosFormato"), Info.RangoEpisodios);

    // === Estado del archivo ===

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeReproducir))]
    [NotifyPropertyChangedFor(nameof(EsSoloVistaPrevia))]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    private bool _descargado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    private bool _descargando;

    /// <summary>Avance de la descarga, de 0 a 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaPorcentaje))]
    private double _progresoDescarga;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeReproducir))]
    [NotifyPropertyChangedFor(nameof(EsSoloVistaPrevia))]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    private bool _vistaPreviaLista;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    private bool _preparandoVistaPrevia;

    /// <summary>Avance de la preparación de la vista previa, de 0 a 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoPreparacionPorcentaje))]
    private double _progresoPreparacion;

    public double ProgresoDescargaPorcentaje => Math.Clamp(ProgresoDescarga, 0, 1) * 100;
    public double ProgresoPreparacionPorcentaje => Math.Clamp(ProgresoPreparacion, 0, 1) * 100;

    /// <summary>Hay un archivo que sonar: el guardado o la vista previa.</summary>
    public bool PuedeReproducir => Descargado || VistaPreviaLista;

    /// <summary>Se puede escuchar pero aún no está guardado.</summary>
    public bool EsSoloVistaPrevia => VistaPreviaLista && !Descargado;

    /// <summary>Todavía no hay nada que escuchar ni se está preparando: se ofrece "escuchar antes de descargar".</summary>
    public bool PuedePrevisualizar => !Descargado && !VistaPreviaLista && !PreparandoVistaPrevia && !Descargando;

    // === Reproducción ===

    [ObservableProperty] private bool _reproduciendo;

    /// <summary>Es la pista cargada en el reproductor (sonando o en pausa): muestra su barra de progreso.</summary>
    [ObservableProperty] private bool _esPistaActual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PosicionTexto))]
    private double _posicionSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DuracionTexto))]
    private double _duracionSegundos;

    /// <summary>
    /// Duración del archivo (guardado o vista previa) leída ANTES de reproducirlo, para verla en la fila. 0 = aún no se sabe
    /// (el tema no tiene archivo, o todavía se está leyendo).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneDuracionArchivo))]
    [NotifyPropertyChangedFor(nameof(DuracionArchivoTexto))]
    private double _duracionArchivoSegundos;

    public bool TieneDuracionArchivo => DuracionArchivoSegundos > 0;
    public string DuracionArchivoTexto => TieneDuracionArchivo ? FormatearTiempo(DuracionArchivoSegundos) : string.Empty;

    public string PosicionTexto => FormatearTiempo(PosicionSegundos);
    public string DuracionTexto => FormatearTiempo(DuracionSegundos);

    /// <summary>True mientras el controlador actualiza la posición desde el reproductor: la barra no debe tomarlo como un salto del usuario.</summary>
    internal bool Sincronizando { get; set; }

    /// <summary>True mientras el usuario arrastra la barra: el temporizador no le quita el control.</summary>
    internal bool Arrastrando { get; set; }

    internal static string FormatearTiempo(double segundos)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(segundos) ? segundos : 0));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
