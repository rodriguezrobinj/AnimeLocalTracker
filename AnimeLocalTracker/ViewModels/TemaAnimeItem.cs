using System;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Un opening/ending en la lista de la Ficha. Estados posibles: sin nada → preparando la vista previa → vista previa lista
/// (solo escuchable, sin guardar) → descargado (guardado en la carpeta de música). Además, si es la pista cargada en el
/// reproductor, lleva su posición y duración para la barra.
/// </summary>
public partial class TemaAnimeItem : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    public required AnimeThemeInfo Info { get; init; }

    public TemaAnimeItem() => WeakReferenceMessenger.Default.Register(this);

    public string Slug => Info.Slug;
    public string Tipo => Info.Tipo;
    public string TituloCancion => string.IsNullOrWhiteSpace(Info.TituloCancion) ? Slug : Info.TituloCancion;
    public string Artistas => Info.Artistas;
    public string RangoTexto => string.IsNullOrWhiteSpace(Info.RangoEpisodios)
        ? LocalizationService.T("Det_MusicaTodosLosEpisodios")
        : string.Format(LocalizationService.T("Det_MusicaEpisodiosFormato"), Info.RangoEpisodios);

    /// <summary>Aclaración de AnimeThemes sobre esta versión (p. ej. "OP as ED"), tal cual la da la API.</summary>
    public string? Notas => Info.Notas;
    public bool TieneNotas => !string.IsNullOrWhiteSpace(Info.Notas);

    // === Spoilers ===

    /// <summary>
    /// AnimeThemes lo marca como spoiler y corresponde a una parte que el usuario aún no ha visto: se tapan el título y el
    /// artista hasta que lo pida (el nombre de un ending del final puede destripar la historia).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TituloVisible))]
    [NotifyPropertyChangedFor(nameof(ArtistasVisible))]
    private bool _ocultoPorSpoiler;

    public string TituloVisible => OcultoPorSpoiler ? LocalizationService.T("Det_MusicaSpoilerTitulo") : TituloCancion;
    public string ArtistasVisible => OcultoPorSpoiler ? LocalizationService.T("Det_MusicaSpoilerSub") : Artistas;

    // === Tamaño (antes de descargar) ===

    /// <summary>Peso aproximado de la descarga ("2,5 MB"). Solo mientras no está guardado y si AnimeThemes lo dice.</summary>
    public bool MostrarTamano => !Descargado && Info.TamanoBytes is > 0;

    public string TamanoTexto => FormatearTamano(Info.TamanoBytes ?? 0);

    internal static string FormatearTamano(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("0.0", LocalizationService.Cultura) + " MB";

    /// <summary>En la cola de "Descargar todos", esperando su turno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsperandoEnCola))]
    [NotifyPropertyChangedFor(nameof(MostrarIconoDescarga))]
    private bool _enCola;

    /// <summary>En cola y todavía sin empezar: el botón muestra un giro de espera en vez del icono.</summary>
    public bool EsperandoEnCola => EnCola && !Descargando;

    public bool MostrarIconoDescarga => !Descargando && !EnCola;

    public void Receive(IdiomaCambiadoMensaje message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefrescarTextos);
            return;
        }
        RefrescarTextos();
    }

    private void RefrescarTextos()
    {
        OnPropertyChanged(nameof(RangoTexto));
        OnPropertyChanged(nameof(TituloVisible));
        OnPropertyChanged(nameof(ArtistasVisible));
        OnPropertyChanged(nameof(TamanoTexto));
    }

    // === Estado del archivo ===

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeReproducir))]
    [NotifyPropertyChangedFor(nameof(EsSoloVistaPrevia))]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    [NotifyPropertyChangedFor(nameof(PuedeDescargar))]
    [NotifyPropertyChangedFor(nameof(MostrarTamano))]
    private bool _descargado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedePrevisualizar))]
    [NotifyPropertyChangedFor(nameof(EsperandoEnCola))]
    [NotifyPropertyChangedFor(nameof(MostrarIconoDescarga))]
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

    /// <summary>
    /// Hay enlace para bajarlo de AnimeThemes. Sin conexión, la ficha muestra los mp3 guardados reconstruidos desde su nombre
    /// de archivo, sin enlace: se pueden escuchar y borrar, pero no volver a bajar.
    /// </summary>
    public bool TieneAudioEnLinea => !string.IsNullOrWhiteSpace(Info.AudioUrlOgg);

    /// <summary>Todavía no hay nada que escuchar ni se está preparando: se ofrece "escuchar antes de descargar".</summary>
    public bool PuedePrevisualizar => TieneAudioEnLinea && !Descargado && !VistaPreviaLista && !PreparandoVistaPrevia && !Descargando;

    /// <summary>Muestra el botón de descargar (o su progreso mientras baja).</summary>
    public bool PuedeDescargar => TieneAudioEnLinea && !Descargado;

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
