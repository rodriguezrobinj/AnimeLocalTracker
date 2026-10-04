using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;

namespace AnimeLocalTracker.ViewModels;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public partial class ReproductorViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService? _settingsService;
    private readonly IPlaybackStateService _playbackState;
    private readonly ISkipTimesCoordinator _skipCoordinator;
    private readonly IEpisodeNavigator _episodeNavigator;
    private readonly IFrameCaptureService _frameCaptureService;
    private readonly IPlaybackWindowModeCoordinator _windowModeCoordinator;
    private readonly ISubtitleCoordinator _subtitleCoordinator;
    private readonly ISubtitleCuesExtractorService _subtitleCuesExtractor;
    private readonly ISubtitleAssRenderer _assRenderer;
    private readonly IPlaybackVolumeCoordinator _volumeCoordinator;
    private readonly IPlaybackSeekCoordinator _seekCoordinator;
    private readonly IFotogramasClaveService _fotogramasClaveService;

    /// <summary>Fotogramas clave del episodio abierto (null hasta leerlos, ~1 s tras abrir): ver <see cref="Saltar"/>.</summary>
    private IReadOnlyList<double>? _fotogramasClave;
    private readonly ISystemMediaControlsService? _smtc;
    private readonly IScreenSaverPreventionService? _screenSaverPrevention;
    private CancellationTokenSource? _skipCts;

    /// <summary>
    /// Pre-análisis de marcas del siguiente episodio. Vida propia: NO se cancela al cambiar de episodio (si se abre justo ese, su carga
    /// se une al análisis en curso); se cancela al programar otro o al cerrar el reproductor.
    /// </summary>
    private CancellationTokenSource? _preanalisisCts;

    /// <summary>Espera tras terminar el análisis del episodio actual: no competir con el arranque de la reproducción (lectura de disco).</summary>
    internal TimeSpan EsperaPreanalisis { get; set; } = TimeSpan.FromSeconds(15);

    // FUN-011: serializa los guardados periódicos de progreso (un guardado a la vez).
    private readonly SemaphoreSlim _guardadoLock = new(1, 1);

    [ObservableProperty]
    private Player _player = null!;

    [ObservableProperty]
    private bool _esModoMini;

    /// <summary>Estilo de los subtítulos (Configuración → Reproducción). Se relee al abrir cada episodio,
    /// así los cambios guardados se ven en el siguiente capítulo sin reiniciar la app.</summary>
    [ObservableProperty]
    private EstiloSubtitulos _estiloSubtitulos = new();

    [ObservableProperty]
    private string _tituloAnime = string.Empty;

    [ObservableProperty]
    private string _tituloEpisodio = string.Empty;

    // Skip Intro / Outro (AniSkip & Fallback)
    [ObservableProperty]
    private bool _mostrarSkipIntro;

    [ObservableProperty]
    private bool _mostrarSkipButton;

    [ObservableProperty]
    private string _skipButtonTexto = "Saltar intro (S)";

    [ObservableProperty]
    private string _skipButtonIcon = "FastForward";

    [ObservableProperty]
    private bool _autoSkipIntroOutro = false;

    [ObservableProperty]
    private string _accionFinEpisodio = AccionFinEpisodioValores.AutoPlayCuentaAtras;

    // === Pasos de salto configurables (Configuración → Reproducción) ===
    [ObservableProperty]
    private int _pasosSaltoSegundos = 10;

    public string RetrocederTooltip => string.Format(LocalizationService.T("Player_RetrocederTooltipFormato"), PasosSaltoSegundos);
    public string AdelantarTooltip => string.Format(LocalizationService.T("Player_AdelantarTooltipFormato"), PasosSaltoSegundos);

    partial void OnPasosSaltoSegundosChanged(int value)
    {
        OnPropertyChanged(nameof(RetrocederTooltip));
        OnPropertyChanged(nameof(AdelantarTooltip));
    }

    // === Cuenta atrás de auto-play al siguiente episodio (AccionFinEpisodioValores.AutoPlayCuentaAtras) ===
    [ObservableProperty] private bool _mostrarCuentaAtrasSiguiente;

    /// <summary>"Te saltas el episodio 4 (no está descargado)" si el siguiente/anterior con archivo no es el consecutivo; vacío si lo es.</summary>
    [ObservableProperty] private string _avisoSaltoSiguiente = string.Empty;
    [ObservableProperty] private string _avisoSaltoAnterior = string.Empty;

    private EpisodioItem? _itemReproduciendose;

    /// <summary>Solo para la marca de favorito del episodio (el progreso y el visto van por PlaybackStateService).</summary>
    private readonly IDatabaseService _databaseService;

    /// <summary>El episodio que se está viendo está marcado como favorito (botón de arriba; el mismo marcador que en la ficha).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipFavoritoEpisodio))]
    private bool _episodioEsFavorito;

    public string TooltipFavoritoEpisodio => LocalizationService.T(EpisodioEsFavorito ? "Player_QuitarFavorito" : "Player_MarcarFavorito");
    [ObservableProperty] private int _segundosCuentaAtrasSiguiente;
    [ObservableProperty] private string _tituloSiguienteEnCuentaAtras = string.Empty;

    [RelayCommand]
    private void CancelarAutoPlay() => MostrarCuentaAtrasSiguiente = false;

    // Control de volumen y mute
    private int _volumen = 100;
    public int Volumen
    {
        get => _volumen;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _volumen, clamped))
            {
                OnVolumenChanged(clamped);
            }
        }
    }

    [ObservableProperty]
    private bool _isMuted = false;

    [ObservableProperty]
    private string _volumenIcon = "VolumeHigh";

    private int _volumenPrevioMute = 100;
    private bool _autoPlayEjecutado = false;
    private double _posicionInicioSegundos = 0;
    private volatile bool _haCompletadoOpen = false;

    /// <summary>
    /// El fin del episodio (guardar, marcar visto, acción configurada) se procesa UNA vez por llegada al final. Antes, con el
    /// video parado al final (sin siguiente episodio, "permanecer pausado" o cuenta atrás cancelada), el bucle lo repetía cada
    /// segundo: guardaba progreso 0 en la base de datos y avisaba a la Ficha/Galería sin parar.
    /// </summary>
    private bool _finDeEpisodioProcesado;

    private readonly Stopwatch _relojArranque = new();
    private bool _arranqueRegistrado;

    /// <summary>Cuenta cada apertura: una consulta de reanudación que termina tarde no debe pisar los datos de un episodio más nuevo
    /// (pasa al pulsar "siguiente" varias veces seguidas).</summary>
    private int _versionCarga;

    /// <summary>Consulta del punto guardado del episodio que se está abriendo (va en paralelo con la apertura del archivo).</summary>
    private Task _tareaReanudacion = Task.CompletedTask;

    /// <summary>
    /// Hay (o aún puede haber) un salto al punto guardado sin aplicar. Mientras tanto la barra y el reloj NO se actualizan con la
    /// posición real (que es 0:00): antes se veía un instante el segundo 0 antes de saltar a donde se había dejado.
    /// </summary>
    private volatile bool _reanudacionPendiente;

    private readonly Stopwatch _relojCambio = new();

    /// <summary>Reintentos de apertura del episodio actual (uno como mucho: un archivo de verdad dañado no se reintenta sin fin).</summary>
    private int _reintentosApertura;

    /// <summary>Tiempo máximo esperando a que Flyleaf termine de abrir antes de reintentar (antes se quedaba cargando sin fin y había que
    /// salir y volver a entrar varias veces).</summary>
    internal TimeSpan TiempoMaximoApertura { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Se está cambiando de pista de audio: el OpenCompleted que llegue no es el de un episodio nuevo.</summary>
    private bool _cambiandoPistaAudio;

    /// <summary>Ya se vio el primer fotograma (o se dejó de esperar): desde ahí se puede saltar al punto guardado sin romper el arranque del
    /// decodificador. Con el sondeo rápido del arranque, el bucle llegaba a saltar a los ~100 ms, antes de que hubiera imagen.</summary>
    private volatile bool _imagenLista;

    /// <summary>La pista de subtítulos por defecto solo se elige sola una vez por episodio (después manda lo que elija el usuario).</summary>
    private bool _pistaSubtitulosElegida;

    private List<AniSkipResult> _skipTimes = new();
    public List<AniSkipResult> SkipTimes => _skipTimes;

    /// <summary>Opening/ending/resumen del episodio para dibujarlos en la barra de progreso (vacío hasta que llegan los tramos).</summary>
    [ObservableProperty]
    private IReadOnlyList<SegmentoLineaTiempo> _segmentosLineaTiempo = Array.Empty<SegmentoLineaTiempo>();

    private AniSkipResult? _currentActiveSkip;
    public AniSkipResult? CurrentActiveSkip => _currentActiveSkip;

    private readonly HashSet<string> _skipAutoEjecutados = new();

    // Propiedades para controles de medios
    [ObservableProperty] private double _currentSeconds;
    [ObservableProperty] private double _totalSeconds;
    [ObservableProperty] private string _tiempoActualTexto = "00:00";
    [ObservableProperty] private string _tiempoTotalTexto = "00:00";
    [ObservableProperty] private string _tiempoCombinadoTexto = "00:00 / 00:00";
    [ObservableProperty] private string _playPauseIcon = "Pause";
    [ObservableProperty] private bool _isDraggingSlider = false;

    // Oculta el frame de video (queda solo la carátula/fondo) mientras se reanuda un episodio:
    // el seek de reanudación se aplica diferido (ver IPlaybackSeekCoordinator) y sin esto se ve
    // el video arrancar en el segundo 0 antes de saltar visiblemente al punto guardado.
    [ObservableProperty] private bool _ocultarVideoInicio;
    private DateTime _ocultarVideoDesdeUtc = DateTime.MinValue;
    private static readonly TimeSpan MaxOcultarVideoInicio = TimeSpan.FromSeconds(5);
    
    // Navegación entre episodios
    private bool _tieneEpisodioAnterior;
    public bool TieneEpisodioAnterior
    {
        get => _tieneEpisodioAnterior;
        set => SetProperty(ref _tieneEpisodioAnterior, value);
    }

    private bool _tieneEpisodioSiguiente;
    public bool TieneEpisodioSiguiente
    {
        get => _tieneEpisodioSiguiente;
        set => SetProperty(ref _tieneEpisodioSiguiente, value);
    }

    private string _episodioAnteriorTooltip = "Episodio anterior (B)";
    public string EpisodioAnteriorTooltip
    {
        get => _episodioAnteriorTooltip;
        set => SetProperty(ref _episodioAnteriorTooltip, value);
    }

    private string _episodioSiguienteTooltip = "Episodio siguiente (N)";
    public string EpisodioSiguienteTooltip
    {
        get => _episodioSiguienteTooltip;
        set => SetProperty(ref _episodioSiguienteTooltip, value);
    }

    // === CAJÓN LATERAL DE EPISODIOS (tecla L) ===
    // Reutiliza la lista del navigator (ya filtrada a episodios con archivo local, ver
    // CargarVideoAsync) — sin consulta nueva a la BD/disco, el dato ya estaba cargado para
    // Siguiente/Anterior episodio.
    public IReadOnlyList<EpisodioItem> EpisodiosDelCajon => _episodeNavigator.EpisodiosDisponibles;

    [ObservableProperty] private bool _cajonEpisodiosAbierto;

    [RelayCommand]
    private void ToggleCajonEpisodios() => CajonEpisodiosAbierto = !CajonEpisodiosAbierto;

    [RelayCommand]
    private void SaltarAEpisodio(EpisodioItem? episodio)
    {
        if (episodio == null || string.IsNullOrWhiteSpace(episodio.RutaCompleta)) return;
        CajonEpisodiosAbierto = false;
        CargarVideo(episodio.RutaCompleta, _animeId, TituloAnime, episodio.NumeroEpisodio, null, _rutaPortada); // misma lista: no se rehace el cajón
    }

    private string _fullscreenIcon = "Fullscreen";
    public string FullscreenIcon
    {
        get => _fullscreenIcon;
        set => SetProperty(ref _fullscreenIcon, value);
    }
    
    private string _subtitulosIcon = "SubtitlesOutline";
    public string SubtitulosIcon
    {
        get => _subtitulosIcon;
        set => SetProperty(ref _subtitulosIcon, value);
    }
    
    private bool _subtitulosHabilitados = false;
    public bool SubtitulosHabilitados
    {
        get => _subtitulosHabilitados;
        set => SetProperty(ref _subtitulosHabilitados, value);
    }

    // ── Subtítulos que se solapan: hasta 2 líneas a la vez (arriba/abajo), ver SubtitulosSolapadosResolver ──

    /// <summary>
    /// True cuando ya se extrajo la pista completa y se puede resolver el solape por nuestra cuenta; el code-behind
    /// usa esto para decidir si confía en <see cref="SubtituloLineaAbajo"/>/<see cref="SubtituloLineaArriba"/> o si
    /// sigue mostrando el texto único de Flyleaf (<c>Player.Subtitles.SubsText</c>) como respaldo — por ejemplo
    /// mientras la extracción todavía está en curso, o si falló (formato no soportado, archivo dañado, etc.).
    /// </summary>
    [ObservableProperty] private bool _subtitulosDobleLineaActivo;

    [ObservableProperty] private string _subtituloLineaAbajo = string.Empty;
    [ObservableProperty] private string _subtituloLineaArriba = string.Empty;

    private IReadOnlyList<Models.SubtitleCue> _subtitleCues = Array.Empty<Models.SubtitleCue>();
    private CancellationTokenSource? _subtitleCuesCts;

    /// <summary>Olvida la pista extraída (episodio nuevo o cambio de pista) y cancela la extracción que estuviera en curso.</summary>
    private void ReiniciarCuesSubtitulos()
    {
        Core.Cancelacion.Detener(ref _subtitleCuesCts);

        SubtitulosDobleLineaActivo = false;
        _subtitleCues = Array.Empty<Models.SubtitleCue>();
        SubtituloLineaAbajo = string.Empty;
        SubtituloLineaArriba = string.Empty;

        _pistaTextoActual = null;
        PistaActualEsAss = false;
        CerrarDibujoAss();
    }

    /// <summary>
    /// Muestra la pista elegida (sola al abrir el episodio, o por el usuario en el menú). Las pistas de texto incrustadas las lee
    /// la app por su cuenta y Flyleaf ni las abre: su lector de subtítulos ASS cierra la aplicación entera con ciertas etiquetas
    /// de color (caso real: el cartel del título de Re:Zero 4th Season, <c>{\c&amp;H..&amp;\fad(..)..\3a&amp;H37&amp;..}</c>), en un
    /// hilo suyo que no se puede proteger. Solo las pistas de imagen (PGS/VobSub), que la app no sabe dibujar, van por Flyleaf.
    /// </summary>
    private void AbrirPistaSubtitulos(object pista)
    {
        ReiniciarCuesSubtitulos();

        if (pista is FlyleafLib.MediaFramework.MediaStream.SubtitlesStream { IsBitmap: false, ExternalStream: null } texto)
        {
            _subtitleCoordinator.Deshabilitar(Player); // por si Flyleaf tenía abierta otra pista (de imagen)
            IniciarCargaCuesSubtitulos(_rutaVideo, texto.StreamIndex, null, texto);

            // Si además es ASS/SSA se prepara el dibujo con su estilo original; mientras tanto (y si falla) se ve el texto plano.
            _pistaTextoActual = texto;
            PistaActualEsAss = texto.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
            IniciarDibujoAssSiCorresponde();
            return;
        }

        _subtitleCoordinator.SeleccionarPista(Player, pista);
    }

    /// <summary>
    /// Se llama cuando Flyleaf termina de abrir una pista de subtítulos (de imagen, externa o una de texto que la app no pudo
    /// leer). Se intenta extraer igualmente para resolver los solapes; hasta que termine (o si falla) se muestra el texto único
    /// de Flyleaf.
    /// </summary>
    private void CargarCuesDePistaDeFlyleaf()
    {
        ReiniciarCuesSubtitulos();

        var subtitulos = Player?.Subtitles;
        if (subtitulos == null || subtitulos.StreamIndex < 0) return;
        var stream = subtitulos.Streams?.FirstOrDefault(s => s.StreamIndex == subtitulos.StreamIndex);
        if (stream == null) return;

        // Una pista externa (.srt/.ass suelto junto al video) trae su ruta real en ExternalStream.Url; una pista
        // incrustada en el propio contenedor no tiene ExternalStream y se identifica por su índice de flujo.
        string? rutaExterna = stream.ExternalStream?.Url;
        int? streamIndexEmbebido = rutaExterna == null ? stream.StreamIndex : null;

        IniciarCargaCuesSubtitulos(_rutaVideo, streamIndexEmbebido, rutaExterna, null);
    }

    private void IniciarCargaCuesSubtitulos(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, object? pistaSinFlyleaf)
    {
        var cts = new CancellationTokenSource();
        _subtitleCuesCts = cts;

        _ = CargarCuesSubtitulosAsync(rutaVideo, streamIndexEmbebido, rutaExterna, pistaSinFlyleaf, cts);
    }

    /// <param name="pistaSinFlyleaf">Pista de texto que Flyleaf no tiene abierta: si la app no consigue leerla, se le entrega a
    /// Flyleaf como último recurso para no dejar el episodio sin subtítulos. Null si Flyleaf ya la está mostrando.</param>
    private async Task CargarCuesSubtitulosAsync(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, object? pistaSinFlyleaf, CancellationTokenSource cts)
    {
        try
        {
            var cues = await _subtitleCuesExtractor.ExtraerAsync(rutaVideo, streamIndexEmbebido, rutaExterna, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_subtitleCuesCts, cts)) return; // se abrió otra pista/video mientras tanto

            _subtitleCues = cues;
            SubtitulosDobleLineaActivo = cues.Count > 0;
            if (cues.Count > 0)
            {
                AppLogger.Debug("ReproductorViewModel", $"Pista de subtítulos {streamIndexEmbebido?.ToString() ?? "externa"} extraída: {cues.Count} líneas.");
            }
            else if (pistaSinFlyleaf != null && SubtitulosHabilitados)
            {
                AppLogger.Warn("ReproductorViewModel", $"No se pudo leer la pista de subtítulos {streamIndexEmbebido}: se deja en manos de Flyleaf.");
                _subtitleCoordinator.SeleccionarPista(Player, pistaSinFlyleaf);
            }
            else
            {
                AppLogger.Debug("ReproductorViewModel", "Extracción de subtítulos sin resultado: se sigue mostrando lo que dé Flyleaf.");
            }
        }
        catch (OperationCanceledException)
        {
            // Se canceló porque se abrió otra pista/video: nada que hacer, el nuevo pedido ya está en curso.
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo extraer la pista de subtítulos: {ex.Message}");
        }
    }

    /// <summary>Se llama en el mismo sondeo de progreso (cada 250 ms mientras reproduce): resuelve qué línea va
    /// arriba y cuál abajo para el instante actual. No hace nada si no hay pista extraída (ver <see cref="SubtitulosDobleLineaActivo"/>).</summary>
    private void ActualizarLineasSubtitulosSolapados(double curSeconds)
    {
        if (!SubtitulosHabilitados || !SubtitulosDobleLineaActivo)
        {
            return;
        }

        var (abajo, arriba) = Core.SubtitulosSolapadosResolver.Resolver(_subtitleCues, TimeSpan.FromSeconds(curSeconds));
        SubtituloLineaAbajo = abajo ?? string.Empty;
        SubtituloLineaArriba = arriba ?? string.Empty;
    }

    // ── Subtítulos ASS/SSA con su estilo original (ver docs/investigacion-subtitulos-ass.md) ──

    /// <summary>La vista le pide los fotogramas; el ViewModel decide cuándo se abre y se cierra.</summary>
    public ISubtitleAssRenderer DibujanteAss => _assRenderer;

    /// <summary>El dibujante está listo y manda sobre el texto plano: la vista muestra la capa de imagen solo con esto en true.</summary>
    [ObservableProperty] private bool _subtitulosAssActivo;

    /// <summary>La pista elegida es ASS/SSA: el menú de subtítulos ofrece "Usar mi estilo".</summary>
    [ObservableProperty] private bool _pistaActualEsAss;

    private CancellationTokenSource? _assCts;
    private bool _assListo;
    private FlyleafLib.MediaFramework.MediaStream.SubtitlesStream? _pistaTextoActual;

    private bool _usarMiEstiloEnAss;
    /// <summary>
    /// Con una pista ASS, ver el texto plano con el estilo de Configuración en vez del original del subtítulo. Se guarda como
    /// preferencia. El dibujante no se cierra al encenderlo: así volver al estilo original es inmediato.
    /// </summary>
    public bool UsarMiEstiloEnAss
    {
        get => _usarMiEstiloEnAss;
        set
        {
            if (!SetProperty(ref _usarMiEstiloEnAss, value)) return;

            GuardarUsarMiEstiloEnAss(value);
            if (value) SubtitulosAssActivo = false;
            else if (_assListo) SubtitulosAssActivo = true;
            else IniciarDibujoAssSiCorresponde();
        }
    }

    [RelayCommand]
    private void ToggleUsarMiEstiloEnAss() => UsarMiEstiloEnAss = !UsarMiEstiloEnAss;

    private void GuardarUsarMiEstiloEnAss(bool valor)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.UsarMiEstiloEnAss == valor) return;
        config.UsarMiEstiloEnAss = valor;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    /// <summary>Abre el dibujante para la pista de texto actual si es ASS/SSA incrustada y el usuario no pidió su estilo.</summary>
    private void IniciarDibujoAssSiCorresponde()
    {
        var pista = _pistaTextoActual;
        var player = Player;
        if (pista == null || player == null) return;

        bool esAss = pista.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
        if (!Core.SubtitulosAss.DebeDibujarse(esAss, pista.IsBitmap, pista.ExternalStream != null, UsarMiEstiloEnAss)) return;

        // ponytail: pantalla principal en unidades de WPF. Con la escala de Windows por encima del 100 % se dibuja algo por
        // debajo de los píxeles reales; si se nota borroso, usar los píxeles del monitor donde está la ventana.
        var (anchoVideo, altoVideo) = TamanoDelVideo(player);
        var (ancho, alto) = Core.SubtitulosAss.TamanoDibujo(
            anchoVideo, altoVideo,
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        int indice = Core.SubtitulosAss.IndiceEntreSubtitulos(
            player.Subtitles.Streams.Where(s => s.ExternalStream == null).Select(s => s.StreamIndex), pista.StreamIndex);
        if (ancho == 0 || indice < 0)
        {
            AppLogger.Debug("ReproductorViewModel", $"Dibujo ASS no disponible (tamaño {ancho}x{alto}, pista {indice}): se queda el texto plano.");
            return;
        }

        IniciarDibujoAss(_rutaVideo, indice, ancho, alto);
    }

    /// <summary>Tamaño del video abierto. Justo al abrir (cuando se elige la pista de subtítulos) Player.Video aún vale 0x0, porque
    /// se rellena con el primer fotograma: entonces se toma de la pista de video del contenedor, que ya está leída.</summary>
    private static (int Ancho, int Alto) TamanoDelVideo(Player player)
    {
        int ancho = player.Video?.Width ?? 0, alto = player.Video?.Height ?? 0;
        if (ancho > 0 && alto > 0) return (ancho, alto);

        var pista = player.VideoDemuxer?.VideoStream ?? player.VideoDemuxer?.VideoStreams?.FirstOrDefault();
        return pista == null ? (0, 0) : ((int)pista.Width, (int)pista.Height);
    }

    internal void IniciarDibujoAss(string ruta, int indice, int ancho, int alto)
    {
        CerrarDibujoAss();

        var cts = new CancellationTokenSource();
        _assCts = cts;
        _ = AbrirDibujoAssAsync(ruta, indice, ancho, alto, cts);
    }

    private async Task AbrirDibujoAssAsync(string ruta, int indice, int ancho, int alto, CancellationTokenSource cts)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            bool listo = await _assRenderer.AbrirAsync(ruta, indice, ancho, alto, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_assCts, cts)) return; // se abrió otra pista/video mientras tanto

            AppLogger.Debug("ReproductorViewModel", $"[Perf] Dibujo ASS {(listo ? "listo" : "no disponible")} en {reloj.ElapsedMilliseconds} ms ({ancho}x{alto}, pista {indice}).");
            _assListo = listo;
            SubtitulosAssActivo = listo && !UsarMiEstiloEnAss;
        }
        catch (OperationCanceledException)
        {
            // Se abrió otra pista/video: el nuevo pedido ya está en curso.
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ReproductorViewModel", $"No se pudo preparar el dibujo ASS: {ex.Message}");
        }
    }

    internal void CerrarDibujoAss()
    {
        Core.Cancelacion.Detener(ref _assCts);

        _assListo = false;
        SubtitulosAssActivo = false;
        _assRenderer.Cerrar();
    }

    /// <summary>La vista avisa de que un fotograma falló: ese episodio sigue en texto plano, sin reintentos.</summary>
    public void NotificarFalloDibujoAss()
    {
        AppLogger.Warn("ReproductorViewModel", "El dibujo ASS falló a mitad de episodio: se vuelve al texto plano.");
        CerrarDibujoAss();
    }

    private bool _modoNocheActivo = false;
    /// <summary>
    /// "Modo noche": compresor de rango dinámico (FFmpeg acompressor) sobre el audio para que
    /// el diálogo se escuche parejo sin que las escenas/openings fuertes suenen a todo volumen.
    /// Se persiste como preferencia por defecto (AppSettings.ModoNocheActivo).
    /// </summary>
    public bool ModoNocheActivo
    {
        get => _modoNocheActivo;
        set
        {
            if (SetProperty(ref _modoNocheActivo, value))
            {
                AplicarModoNocheAlPlayer(value);
                GuardarModoNochePreferencia(value);
            }
        }
    }

    private const string ArgumentosModoNoche = "threshold=0.089:ratio=9:attack=200:release=1000:makeup=2";

    private void AplicarModoNocheAlPlayer(bool activo) => AplicarFiltrosAudio();

    /// <summary>Cadena de filtros de audio actual (ecualizador y Modo Noche) en el formato de Flyleaf.</summary>
    private List<Filter> ConstruirFiltrosAudio() =>
        Core.EcualizadorAudio.ConstruirFiltros(EcualizadorActivo, BandasEcualizador.Select(b => b.Ganancia).ToList(), ModoNocheActivo, ArgumentosModoNoche)
            .Select(f => new Filter { Id = f.Id, Name = f.Nombre, Args = f.Argumentos })
            .ToList();

    /// <summary>Reconstruye la cadena de filtros (al encender/apagar el ecualizador o el Modo Noche; un corte de milisegundos).</summary>
    private void AplicarFiltrosAudio()
    {
        if (Player?.Config?.Audio == null) return;
        try
        {
            Player.Config.Audio.Filters = ConstruirFiltrosAudio();
            int resultado = Player.Config.Audio.ReloadFilters();
            if (resultado < 0) AppLogger.Warn("ReproductorViewModel", $"Flyleaf no pudo montar los filtros de audio ({resultado}).");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error aplicando los filtros de audio: {ex.Message}");
        }
    }

    // ── Ecualizador ──

    public sealed partial class BandaEcualizador : ObservableObject
    {
        public BandaEcualizador(int indice, int frecuencia, double ganancia)
        {
            Indice = indice;
            Etiqueta = Core.EcualizadorAudio.EtiquetaFrecuencia(frecuencia);
            _ganancia = ganancia;
        }

        public int Indice { get; }
        public string Etiqueta { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TextoGanancia))]
        private double _ganancia;

        public string TextoGanancia => Ganancia > 0 ? $"+{Ganancia:0}" : $"{Ganancia:0}";
    }

    /// <summary>Un ajuste predefinido tal como se ve en el menú (EsActual resalta el que está puesto).</summary>
    public sealed partial class OpcionPresetEcualizador : ObservableObject
    {
        public OpcionPresetEcualizador(string clave, string nombre)
        {
            Clave = clave;
            Nombre = nombre;
        }

        public string Clave { get; }
        public string Nombre { get; }

        [ObservableProperty] private bool _esActual;
    }

    public System.Collections.ObjectModel.ObservableCollection<BandaEcualizador> BandasEcualizador { get; } = new();

    public IReadOnlyList<OpcionPresetEcualizador> PresetsEcualizador { get; private set; } = Array.Empty<OpcionPresetEcualizador>();

    [ObservableProperty] private bool _ecualizadorActivo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NombrePresetEcualizador))]
    private string _presetEcualizador = Core.EcualizadorAudio.PresetPlano;

    /// <summary>"Voces claras", "Personalizado"…</summary>
    public string NombrePresetEcualizador => LocalizationService.T("Eq_Preset_" + PresetEcualizador);

    partial void OnPresetEcualizadorChanged(string value)
    {
        foreach (var opcion in PresetsEcualizador) opcion.EsActual = opcion.Clave == value;
    }

    private bool _aplicandoPreset;
    private CancellationTokenSource? _guardadoEcualizadorCts;

    private void CargarEcualizador(AppSettings? config)
    {
        var ganancias = Core.EcualizadorAudio.Normalizar(config?.EcualizadorGanancias);
        foreach (var banda in BandasEcualizador) banda.PropertyChanged -= Banda_PropertyChanged;
        BandasEcualizador.Clear();
        for (int i = 0; i < ganancias.Length; i++)
        {
            var banda = new BandaEcualizador(i, Core.EcualizadorAudio.Frecuencias[i], ganancias[i]);
            banda.PropertyChanged += Banda_PropertyChanged;
            BandasEcualizador.Add(banda);
        }
        string preset = Core.EcualizadorAudio.PresetDe(ganancias);
        PresetsEcualizador = Core.EcualizadorAudio.Presets
            .Select(p => new OpcionPresetEcualizador(p.Clave, LocalizationService.T("Eq_Preset_" + p.Clave)) { EsActual = p.Clave == preset })
            .ToList();

        _cargandoEcualizador = true; // lo que se lee de los ajustes no se vuelve a guardar
        try
        {
            EcualizadorActivo = config?.EcualizadorActivo ?? false;
            PresetEcualizador = preset;
        }
        finally
        {
            _cargandoEcualizador = false;
        }
    }

    private bool _cargandoEcualizador;

    partial void OnEcualizadorActivoChanged(bool value)
    {
        if (_cargandoEcualizador) return; // aún no hay Player: CreateOptimizedPlayer ya monta los filtros guardados
        AplicarFiltrosAudio();
        GuardarEcualizadorEnDiferido();
    }

    /// <summary>Mover una banda: solo se le manda la ganancia nueva a su filtro (sin reconstruir la cadena, sin cortes).</summary>
    private void Banda_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BandaEcualizador.Ganancia) || sender is not BandaEcualizador banda) return;

        if (!_aplicandoPreset)
        {
            PresetEcualizador = Core.EcualizadorAudio.PresetDe(BandasEcualizador.Select(b => b.Ganancia).ToList());
            if (!EcualizadorActivo) EcualizadorActivo = true; // mover una banda con el ecualizador apagado lo enciende (ya monta la cadena y guarda)
            else
            {
                ActualizarFiltroBanda(banda);
                GuardarEcualizadorEnDiferido();
            }
        }
    }

    private void ActualizarFiltroBanda(BandaEcualizador banda)
    {
        if (!EcualizadorActivo || Player?.Config?.Audio == null) return;
        try
        {
            var audio = Player.Config.Audio;
            int r1 = audio.UpdateFilter(Core.EcualizadorAudio.IdBanda(banda.Indice), "g", Core.EcualizadorAudio.TextoGanancia(banda.Ganancia));
            double pre = Core.EcualizadorAudio.Preamplificacion(BandasEcualizador.Select(b => b.Ganancia).ToList());
            int r2 = audio.UpdateFilter(Core.EcualizadorAudio.IdPreamplificador, "volume", Core.EcualizadorAudio.TextoVolumen(pre));
            if (r1 < 0 || r2 < 0) AplicarFiltrosAudio(); // si el filtro no admite el cambio en vivo, se monta la cadena de nuevo
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error cambiando una banda del ecualizador: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AplicarPresetEcualizador(string? clave)
    {
        var preset = Core.EcualizadorAudio.Presets.FirstOrDefault(p => p.Clave == clave);
        if (preset == null) return;

        _aplicandoPreset = true;
        try
        {
            for (int i = 0; i < BandasEcualizador.Count; i++) BandasEcualizador[i].Ganancia = preset.Ganancias[i];
        }
        finally
        {
            _aplicandoPreset = false;
        }
        PresetEcualizador = preset.Clave;
        if (!EcualizadorActivo) EcualizadorActivo = true; // elegir un ajuste enciende el ecualizador (ya reconstruye la cadena)
        else AplicarFiltrosAudio();
        GuardarEcualizadorEnDiferido();
    }

    [RelayCommand]
    private void RestablecerEcualizador() => AplicarPresetEcualizador(Core.EcualizadorAudio.PresetPlano);

    /// <summary>Se guarda en los ajustes medio segundo después del último cambio (arrastrar un deslizador genera decenas).</summary>
    private void GuardarEcualizadorEnDiferido()
    {
        if (_settingsService == null) return;
        _guardadoEcualizadorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _guardadoEcualizadorCts = cts;
        var ganancias = BandasEcualizador.Select(b => b.Ganancia).ToList();
        bool activo = EcualizadorActivo;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                var config = _settingsService.ObtenerConfiguracion();
                if (config == null) return;
                config.EcualizadorActivo = activo;
                config.EcualizadorGanancias = ganancias;
                await _settingsService.GuardarConfiguracionAsync(config);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo guardar el ecualizador: {ex.Message}"); }
        });
    }

    private void GuardarModoNochePreferencia(bool activo)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.ModoNocheActivo == activo) return;
        config.ModoNocheActivo = activo;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    [RelayCommand]
    private void ToggleModoNoche() => ModoNocheActivo = !ModoNocheActivo;

    private int _animeId;
    public int AnimeId => _animeId;

    private int _episodio;
    public int Episodio => _episodio;

    private string _rutaVideo = string.Empty;
    public string RutaVideo => _rutaVideo;

    private string? _rutaPortada;

    private bool _fueMarcadoComoVisto = false;
    
    // Cache para evitar recalcular duración en cada tick
    private double _lastNotifiedSeconds = -1;
    private double _lastSavedSeconds = -1;
    private double _resumingPositionSeconds = 0;
    public double ResumingPositionSeconds => _resumingPositionSeconds;
    private bool _durationCached = false;

    public ReproductorViewModel(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IAuthService authService,
        IAniSkipService? aniSkipService = null,
        ISettingsService? settingsService = null,
        IPlaybackStateService? playbackStateService = null,
        ISkipTimesCoordinator? skipTimesCoordinator = null,
        IVentanaPrincipal? ventanaPrincipal = null,
        ISystemMediaControlsService? systemMediaControlsService = null,
        ILogrosService? logrosService = null,
        IDialogService? dialogService = null,
        IScreenSaverPreventionService? screenSaverPreventionService = null,
        IEpisodeNavigator? episodeNavigator = null,
        IFrameCaptureService? frameCaptureService = null,
        IPlaybackWindowModeCoordinator? windowModeCoordinator = null,
        ISubtitleCoordinator? subtitleCoordinator = null,
        ISubtitleCuesExtractorService? subtitleCuesExtractorService = null,
        IPlaybackVolumeCoordinator? volumeCoordinator = null,
        IPlaybackSeekCoordinator? seekCoordinator = null,
        IFotogramasClaveService? fotogramasClaveService = null,
        ISubtitleAssRenderer? subtitleAssRenderer = null)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
        _logrosService = logrosService;
        DialogService = dialogService;
        _screenSaverPrevention = screenSaverPreventionService;

        _playbackState = playbackStateService ?? new PlaybackStateService(databaseService, animeTrackingService, authService);

        _skipCoordinator = skipTimesCoordinator ?? new SkipTimesCoordinator(aniSkipService);
        _episodeNavigator = episodeNavigator ?? new EpisodeNavigator();
        _frameCaptureService = frameCaptureService ?? new FrameCaptureService();
        _windowModeCoordinator = windowModeCoordinator ?? new PlaybackWindowModeCoordinator(ventanaPrincipal);
        _subtitleCoordinator = subtitleCoordinator ?? new SubtitleCoordinator();
        _subtitleCuesExtractor = subtitleCuesExtractorService ?? new SubtitleCuesExtractorService();
        _assRenderer = subtitleAssRenderer ?? new SubtitleAssRenderer();
        _volumeCoordinator = volumeCoordinator ?? new PlaybackVolumeCoordinator();
        _seekCoordinator = seekCoordinator ?? new PlaybackSeekCoordinator();
        _fotogramasClaveService = fotogramasClaveService ?? new FotogramasClaveService();

        // SMT-01: SMTC es un singleton (un único HWND); esta instancia se suscribe a sus
        // botones mientras controla la reproducción y se desuscribe en Dispose(). Al ser
        // ReproductorViewModel transient (una instancia nueva por navegación al reproductor),
        // nunca hay dos VMs escuchando el mismo evento a la vez (NavigationService dispone el
        // anterior antes de crear uno nuevo).
        _smtc = systemMediaControlsService;
        if (_smtc != null)
        {
            _smtc.PlayRequested += OnSmtcPlayRequested;
            _smtc.PauseRequested += OnSmtcPauseRequested;
            _smtc.NextRequested += OnSmtcNextRequested;
            _smtc.PreviousRequested += OnSmtcPreviousRequested;
        }

        if (_settingsService != null)
        {
            var config = _settingsService.ObtenerConfiguracion();
            if (config != null)
            {
                _autoSkipIntroOutro = config.AutoSkipIntroOutro;
                _accionFinEpisodio = string.IsNullOrWhiteSpace(config.AccionFinEpisodio) ? AccionFinEpisodioValores.AutoPlayCuentaAtras : config.AccionFinEpisodio;
                _pasosSaltoSegundos = config.PasosSaltoSegundos is 5 or 10 or 30 or 60 ? config.PasosSaltoSegundos : 10;
                _subtitulosHabilitados = config.SubtitulosPorDefecto;
                _subtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
                _modoNocheActivo = config.ModoNocheActivo;
                CargarEcualizador(config);
                _estiloSubtitulos = (config.EstiloSubtitulos ?? new EstiloSubtitulos()).Normalizar();
                _usarMiEstiloEnAss = config.UsarMiEstiloEnAss;
            }
        }

        if (BandasEcualizador.Count == 0) CargarEcualizador(null); // sin ajustes: ecualizador plano y apagado
    }

    /// <summary>
    /// FUN-003: umbral configurable de "marcado como visto" (AppSettings.UmbralMarcadoVisto,
    /// 1-100 → 0-1). Antes el auto-marcado estaba fijo en 90% y el ajuste de la UI era decorativo.
    /// </summary>
    private double UmbralMarcadoVistoActual
    {
        get
        {
            int porcentaje = _settingsService?.ObtenerConfiguracion()?.UmbralMarcadoVisto ?? 90;
            return Math.Clamp(porcentaje, 1, 100) / 100.0;
        }
    }

    private void OnVolumenChanged(int value)
    {
        if (_volumeCoordinator.AplicarVolumen(Player, value, IsMuted))
        {
            IsMuted = false;
        }

        ActualizarVolumenIcon();
    }

    [RelayCommand]
    public void ToggleMute()
    {
        if (IsMuted || Volumen == 0)
        {
            IsMuted = false;
            Volumen = _volumenPrevioMute > 0 ? _volumenPrevioMute : 100;
            _volumeCoordinator.Desmutear(Player, Volumen);
        }
        else
        {
            _volumenPrevioMute = Volumen;
            IsMuted = true;
            // Bug: la barra de volumen seguía al máximo al silenciar porque Volumen
            // no cambiaba — ahora baja a 0 (y se restaura el previo al desmutear)
            Volumen = 0;
            _volumeCoordinator.Mutear(Player);
        }
        ActualizarVolumenIcon();
    }

    private void ActualizarVolumenIcon()
    {
        VolumenIcon = _volumeCoordinator.CalcularIcono(Volumen, IsMuted);
    }


    // En entornos de pruebas (headless) Flyleaf puede dejar su hilo maestro bloqueado y
    // cualquier construcción posterior de Config() se cuelga en Dispatcher.Invoke síncrono.
    internal static bool EsEntornoPruebas()
    {
        try
        {
            return EsEntornoPruebas(
                Process.GetCurrentProcess().ProcessName,
                Environment.CommandLine,
                AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Criterio deliberadamente ESTRECHO: solo los marcadores del ejecutor de pruebas (testhost /
    /// vstest / xunit). Un simple "contiene 'test'" es peligroso — un plugin cargado en la app
    /// (p. ej. un ensamblado "plugin_test") o una ruta de instalación como "C:\Users\test\..."
    /// hacían creer a la app que corría bajo pruebas y dejaban los videos sin reproductor.
    /// </summary>
    internal static bool EsEntornoPruebas(string nombreProceso, string lineaComandos, IEnumerable<string?> nombresEnsamblados)
    {
        if (nombreProceso.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
            nombreProceso.Contains("vstest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (lineaComandos.Contains("vstest", StringComparison.OrdinalIgnoreCase) ||
            lineaComandos.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return nombresEnsamblados.Any(nombre =>
            nombre != null && (
                nombre.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                nombre.Equals("testhost", StringComparison.OrdinalIgnoreCase) ||
                nombre.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)));
    }

    public void AsegurarPlayerInicializado()
    {
        if (Player == null || Player.IsDisposed)
        {
            Player = CreateOptimizedPlayer();
        }
    }

    public virtual Player CreateOptimizedPlayer()
    {
        if (EsEntornoPruebas())
        {
            return null!;
        }

        try
        {
            MotorVideo.AsegurarIniciado();

            var config = new Config();
            
            // 1. Seeking rápido instantáneo por Keyframe (no frame-accurate) -> 0 ms seek latency
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
                config.Player.AutoPlay = true;

                // El hilo de reproducción es el que alimenta el audio: con la ventana sin foco y
                // otras apps compitiendo por CPU, en prioridad Normal el sonido se entrecorta.
                config.Player.ThreadPriority = ThreadPriority.Highest;
                // Contador de fotogramas mostrados: con él se sabe cuándo aparece la imagen de verdad (ver VigilarArranqueAsync).
                config.Player.Stats = true;
            }

            // 2. Decoder multi-hilos para decodificación suave de AV1 y HEVC 10-bit
            if (config.Decoder != null)
            {
                // Todos los hilos: el AV1 va por software en gráficas sin soporte (Intel HD 620) y un salto preciso decodifica hasta
                // ~10 s de video; con 4 hilos en vez de 2 se decodifica un ~15 % más rápido (medido con ffmpeg: 231 → 267 fps).
                config.Decoder.VideoThreads = Math.Max(2, Environment.ProcessorCount);
            }

            // 2b. Decodificación por GPU (Direct3D) explícita: es el valor por defecto de FlyleafLib,
            // pero se deja explícito para que no dependa de un default que la librería podría cambiar
            // en una actualización futura. El soporte real de AV1/HEVC 10-bit por hardware varía mucho
            // entre GPUs, así que si falla, FlyleafLib cae solo a decodificación por software (más lenta
            // pero funcional) — se registra cuál de las dos se usó en OpenCompleted, más abajo.
            if (config.Video != null)
            {
                var ajustes = _settingsService?.ObtenerConfiguracion();
                config.Video.VideoAcceleration = ajustes?.AceleracionHardwareVideo ?? true;

                // Tarjeta gráfica elegida en Configuración (portátiles con integrada + dedicada: Windows suele dar la integrada).
                string? patronTarjeta = MotorVideo.PatronTarjeta(ajustes?.TarjetaGraficaVideo);
                if (patronTarjeta != null) config.Video.GPUAdapter = patronTarjeta;
            }

            // 3. Buffer de Demuxer en RAM (30 segundos precargados en memoria para reproducción sin tirones)
            if (config.Demuxer != null)
            {
                // BufferDuration en ticks (1 tick = 100ns -> 30 segundos = 300,000,000 ticks)
                config.Demuxer.BufferDuration = 300_000_000L;
            }

            // 4. Subtítulos: Flyleaf arranca siempre con los suyos apagados para que no abra (ni lea) ninguna pista por su cuenta;
            // la app elige la pista y la lee ella misma (ver AbrirPistaSubtitulos).
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = false;
            }

            // 5. Filtros de audio guardados: ecualizador y Modo Noche (compresor de rango dinámico)
            if (config.Audio != null && (ModoNocheActivo || EcualizadorActivo))
            {
                config.Audio.Filters = ConstruirFiltrosAudio();
            }

            // Red de seguridad para cuando Flyleaf sí decodifica una pista de texto: su lector de estilos ASS cerraba la app.
            ParcheSubtitulosFlyleaf.Aplicar();

            var player = new Player(config);

            // Prioridad del proceso solo mientras hay un video abierto (se restaura en Dispose).
            if (!_prioridadElevada)
            {
                PrioridadReproduccion.Elevar();
                _prioridadElevada = true;
            }

            player.OpenCompleted += (s, e) =>
            {
                // Que Flyleaf abra una pista de subtítulos también dispara OpenCompleted: solo hay que extraer la pista nueva. Antes se
                // reevaluaba "subtítulos por defecto" y, con esa opción apagada, se apagaba en el acto la pista que el usuario
                // acababa de elegir en el menú.
                if (e.IsSubtitles)
                {
                    if (!e.Success) AppLogger.Warn("ReproductorViewModel", $"No se pudo abrir la pista de subtítulos: {e.Error}");
                    CargarCuesDePistaDeFlyleaf();
                    return;
                }

                if (_cambiandoPistaAudio)
                {
                    // Cambio de pista de audio desde el menú: el video sigue donde estaba; solo se restaura volumen/silencio.
                    _cambiandoPistaAudio = false;
                    if (!e.Success) AppLogger.Warn("ReproductorViewModel", $"No se pudo abrir la pista de audio: {e.Error}");
                    try { if (player.Audio != null) { player.Audio.Volume = Volumen; player.Audio.Mute = IsMuted; } } catch { }
                    return;
                }

                if (!e.Success)
                {
                    if (ReintentarAperturaSiCorresponde(e.Error)) return;
                    NotificarFalloAlAbrir(e.Error);
                    return;
                }

                _haCompletadoOpen = true;
                AppLogger.Debug("ReproductorViewModel", $"[Arranque] OpenCompleted a los {_relojArranque.ElapsedMilliseconds} ms (estado {player.Status}).");
                AppLogger.Debug("ReproductorViewModel", $"Pistas de subtítulos disponibles: {player.Subtitles?.Streams?.Count ?? -1}.");
                EvaluarSubtitulosPorDefecto();

                try
                {
                    if (player.Status != Status.Playing)
                    {
                        player.Play();
                    }
                    PlayPauseIcon = "Pause";
                    _smtc?.ActualizarEstadoReproduccion(true);
                    IniciarProteccionPantallaSiCorresponde();
                }
                catch { }

                if (player.Audio != null)
                {
                    try
                    {
                        // Sigue en silencio hasta que se vea la imagen (y, al reanudar, hasta que el salto al punto guardado se
                        // asiente): lo quita VigilarArranqueAsync.
                        player.Audio.Volume = Volumen;
                        player.Audio.Mute = true;
                    }
                    catch { }
                }

                ActualizarPistasAudio(aplicarPreferencia: true);
                _ = VigilarArranqueAsync(player, _trackingCts?.Token ?? CancellationToken.None);

                // IMPORTANTE: NO seekear aquí (ni el diferido-al-abrir del coordinador ni la
                // reanudación). En este punto el decoder de video aún está creando su contexto de
                // renderizado y un Player.CurTime inmediato interrumpe ese proceso
                // en algunos archivos (HEVC/VFR) dejando la pantalla en negro con
                // audio avanzando. El bucle de tracking aplica el seek vía
                // IPlaybackSeekCoordinator cuando el video ya está reproduciendo de verdad.
            };
            return player;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Player nativo no disponible en este entorno: {ex.Message}");
            return null!;
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (Player == null) return;

        if (Player.Status == Status.Playing)
        {
            Pausar();
        }
        else
        {
            Reproducir();
        }
    }

    private void Pausar()
    {
        if (Player == null || Player.Status != Status.Playing) return;

        Player.Pause();
        PlayPauseIcon = "Play";
        _smtc?.ActualizarEstadoReproduccion(false);
        DetenerProteccionPantalla();
        _ = GuardarProgresoActualAsync();
    }

    private void Reproducir()
    {
        if (Player == null || Player.Status == Status.Playing) return;

        // Si el episodio terminó, reproducir de nuevo desde el inicio
        if (Player.Status == Status.Ended)
        {
            try { Player.CurTime = 0; } catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo reiniciar posición: {ex.Message}"); }
            _fueMarcadoComoVisto = false;
        }
        Player.Play();
        PlayPauseIcon = "Pause";
        _smtc?.ActualizarEstadoReproduccion(true);
        IniciarProteccionPantallaSiCorresponde();
    }

    /// <summary>Activa SetThreadExecutionState (evitar suspensión) solo si el usuario lo pidió en
    /// Configuración; se llama en cada Play (manual, SMTC o autoplay al abrir un episodio).</summary>
    private void IniciarProteccionPantallaSiCorresponde()
    {
        bool evitarSuspension = _settingsService?.ObtenerConfiguracion()?.EvitarSuspensionPantalla ?? true;
        if (evitarSuspension)
        {
            _screenSaverPrevention?.Activar();
        }
    }

    private void DetenerProteccionPantalla() => _screenSaverPrevention?.Desactivar();

    /// <summary>
    /// SMT-01: el evento ButtonPressed de SMTC llega en un hilo COM/MTA ajeno al Dispatcher
    /// de WPF — nunca tocar Player ni propiedades observables sin saltar antes al hilo de UI.
    /// </summary>
    private void OnSmtcPlayRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(Reproducir);

    private void OnSmtcPauseRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(Pausar);

    private void OnSmtcNextRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(SiguienteEpisodio);

    private void OnSmtcPreviousRequested(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(AnteriorEpisodio);
    
    [RelayCommand]
    public void Rewind10()
    {
        double newSeconds = Math.Max(0, CurrentSeconds - PasosSaltoSegundos);
        Saltar(newSeconds, TipoSalto.Atras);
    }

    [RelayCommand]
    public void Forward10()
    {
        double max = TotalSeconds > 0 ? TotalSeconds : double.MaxValue;
        double newSeconds = Math.Min(max, CurrentSeconds + PasosSaltoSegundos);
        Saltar(newSeconds, TipoSalto.Adelante);
    }

    // === Scrubbing de la línea de tiempo ===
    private double _posicionAntesArrastre;

    private void ActualizarTextosTiempo(double posicionSegundos)
    {
        TiempoActualTexto = FormatearTiempo(posicionSegundos, TotalSeconds);
        TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";
    }

    /// <summary>Fija la duración del episodio (barra y texto) y rehace los marcadores con ella.</summary>
    private void EstablecerDuracion(double duracionSegundos)
    {
        TotalSeconds = duracionSegundos;
        TiempoTotalTexto = duracionSegundos > 0 ? FormatearTiempo(duracionSegundos, duracionSegundos) : "00:00";
        TiempoActualTexto = FormatearTiempo(CurrentSeconds, duracionSegundos);
        TiempoCombinadoTexto = $"{TiempoActualTexto} / {TiempoTotalTexto}";

        // Los tramos pudieron llegar antes que la duración (el análisis guardado responde al instante): se recortan con la real.
        if (_skipTimes.Count > 0) SegmentosLineaTiempo = SegmentoLineaTiempo.Crear(_skipTimes, duracionSegundos);
    }

    /// <summary>
    /// "mm:ss", o "h:mm:ss" si el episodio dura una hora o más — el mismo formato para la posición y la duración (antes la
    /// posición pasaba a "hh:mm:ss" solo al cruzar la hora y quedaba "59:59 / 01:20:00"). Los segundos se truncan, como en
    /// cualquier reproductor: el final se ajusta a la duración exacta al terminar (ver <see cref="AjustarPosicionAlFinal"/>).
    /// </summary>
    internal static string FormatearTiempo(double segundos, double duracionReferencia)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, segundos));
        bool conHoras = duracionReferencia >= 3600 || t.TotalHours >= 1;
        return conHoras
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    /// <summary>
    /// Al terminar el episodio la barra queda llena y el tiempo igual a la duración. Antes se quedaba en la última lectura del
    /// sondeo (hasta medio segundo antes del final): "23:59 / 24:00" y la bolita sin llegar al borde.
    /// </summary>
    internal void AjustarPosicionAlFinal()
    {
        if (TotalSeconds <= 0 || IsDraggingSlider) return;
        CurrentSeconds = TotalSeconds;
        _lastNotifiedSeconds = TotalSeconds;
        ActualizarTextosTiempo(TotalSeconds);
    }

    private static double AcotarPosicion(double segundos)
    {
        if (segundos < 0) return 0;
        return segundos;
    }

    /// <summary>
    /// Comando para cuando el usuario suelta el slider (Thumb.DragCompleted), hace clic en la pista
    /// o usa los atajos de teclado. Feedback de UI inmediato + seek nativo coalescido (último-gana).
    /// </summary>
    [RelayCommand]
    public void Seek(double seconds) => Saltar(seconds, TipoSalto.Libre);

    /// <summary>
    /// Salto puntual (flechas, ±N s, saltar opening, clic en la barra). Si hay un fotograma clave cerca del destino se salta a él con el
    /// salto rápido (instantáneo); si no, salto preciso. En AV1 el preciso decodifica hasta ~10 s de video (0,7-1 s de espera en un
    /// i5-7300U) y el rápido a secas caía hasta 10 s antes de lo pedido (o no avanzaba); con esto ~9 de cada 10 saltos con las flechas
    /// son instantáneos, con ~1,5 s de diferencia media. Mientras la lista de fotogramas clave no está (~1 s tras abrir), son precisos.
    /// </summary>
    internal void Saltar(double seconds, TipoSalto tipo)
    {
        seconds = AcotarPosicion(seconds);
        var (destino, preciso) = _estrategiaSaltos switch
        {
            EstrategiaSaltos.Exactos => (seconds, true),
            EstrategiaSaltos.Rapidos => FotogramasClaveService.ElegirDestinoRapido(_fotogramasClave, seconds, tipo, CurrentSeconds),
            _ => FotogramasClaveService.ElegirDestino(_fotogramasClave, seconds, tipo, CurrentSeconds)
        };
        if (TotalSeconds > 0 && destino > TotalSeconds) (destino, preciso) = (seconds, true);

        // Actualizar UI inmediatamente para feedback instantáneo
        _seekCoordinator.IniciarVentanaDeSettle();
        _lastNotifiedSeconds = destino;
        CurrentSeconds = destino;
        ActualizarTextosTiempo(destino);

        _seekCoordinator.SolicitarSeek(Player, destino, () => _haCompletadoOpen, preciso);
    }

    /// <summary>
    /// Inicio de arrastre del pulgar: congela el repintado del bucle y recuerda la posición real del reproductor.
    /// </summary>
    public void IniciarArrastre()
    {
        IsDraggingSlider = true;

        // La posición autoritativa es la del reproductor, no la del slider (el binding pudo ya mover el thumb)
        double posicionReal = CurrentSeconds;
        if (Player != null && !Player.IsDisposed)
        {
            try { posicionReal = TimeSpan.FromTicks(Player.CurTime).TotalSeconds; } catch { }
        }
        _posicionAntesArrastre = posicionReal;
    }

    /// <summary>
    /// Durante el arrastre: feedback visual instantáneo (thumb + tiempo) y scrub en vivo
    /// a través del mismo coalescing de seeks (último-gana, máx ~4 seeks/seg).
    /// </summary>
    public void VistaPreviaArrastre(double segundos)
    {
        if (!IsDraggingSlider) return;

        segundos = AcotarPosicion(segundos);
        if (TotalSeconds > 0 && segundos > TotalSeconds) segundos = TotalSeconds;

        CurrentSeconds = segundos;
        ActualizarTextosTiempo(segundos);

        _seekCoordinator.SolicitarSeek(Player, segundos, () => _haCompletadoOpen);
    }

    /// <summary>
    /// Fin de arrastre: aplica el seek final garantizado, actualiza UI y descongela el bucle.
    /// </summary>
    public void FinalizarArrastre(double segundos)
    {
        segundos = AcotarPosicion(segundos);
        if (TotalSeconds > 0 && segundos > TotalSeconds) segundos = TotalSeconds;

        IsDraggingSlider = false;

        // Si la posición apenas cambió respecto a antes de arrastrar, restauramos sin seek extra
        if (Math.Abs(segundos - _posicionAntesArrastre) < 0.25)
        {
            CurrentSeconds = _posicionAntesArrastre;
            ActualizarTextosTiempo(_posicionAntesArrastre);
            return;
        }

        Seek(segundos);
    }

    [RelayCommand]
    public void ToggleFullscreen()
    {
        string? icono = _windowModeCoordinator.AlternarPantallaCompleta();
        if (icono != null) FullscreenIcon = icono;
    }

    [RelayCommand]
    public void AlternarModoMini()
    {
        if (EsModoMini)
        {
            RestaurarFormatoHabitual();
        }
        else
        {
            MinimizarAMini();
        }
    }

    /// <summary>
    /// Mini reproductor: el episodio pasa a una ventana flotante propia (siempre encima, visible aunque la app esté minimizada)
    /// y la ventana principal queda libre para navegar. EsModoMini hace que la vista muestre sus controles compactos.
    /// </summary>
    [RelayCommand]
    public void MinimizarAMini()
    {
        EsModoMini = true;
        _windowModeCoordinator.EntrarModoMini();
    }

    [RelayCommand]
    public void RestaurarFormatoHabitual()
    {
        EsModoMini = false; // la ventana principal vuelve a dibujar el reproductor…
        _windowModeCoordinator.SalirModoMini(); // …y se cierra la flotante
        _windowModeCoordinator.MostrarVentanaPrincipal();
    }


    [RelayCommand]
    public void SelectSubtitleStream(object stream)
    {
        if (Player == null || stream == null) return;

        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
        AbrirPistaSubtitulos(stream);
    }

    // ── Pistas de audio (doblaje + original, comentarios…). El botón está siempre; con una sola pista el menú lo dice. ──

    public System.Collections.ObjectModel.ObservableCollection<OpcionPistaAudio> PistasAudio { get; } = new();

    [ObservableProperty] private bool _hayVariasPistasAudio;
    [ObservableProperty] private bool _hayUnaSolaPistaAudio;
    /// <summary>El archivo abierto no trae audio. Falso antes de abrir (mientras carga no se afirma nada).</summary>
    [ObservableProperty] private bool _sinPistasAudio;

    [RelayCommand]
    private void SeleccionarPistaAudio(OpcionPistaAudio? opcion)
    {
        if (opcion == null || opcion.EsActual) return;
        CambiarPistaAudio(opcion, recordar: true);
    }

    /// <summary>Rehace el menú de audio con las pistas del archivo; con <paramref name="aplicarPreferencia"/> pone sola la del idioma
    /// que el usuario eligió la última vez.</summary>
    private void ActualizarPistasAudio(bool aplicarPreferencia)
    {
        var audio = Player?.Audio;
        var streams = audio?.Streams?.ToList() ?? new List<FlyleafLib.MediaFramework.MediaStream.AudioStream>();
        int actual = audio?.StreamIndex ?? -1;
        var opciones = streams.Select((st, i) =>
        {
            string? idioma = null;
            try { idioma = st.Language?.TopCulture?.TwoLetterISOLanguageName; } catch { }
            if (idioma is "iv") idioma = null; // cultura invariante = sin idioma
            return new OpcionPistaAudio(st, OpcionPistaAudio.ConstruirNombre(idioma, st.Title, i + 1), idioma, st.StreamIndex == actual);
        }).ToList();

        EnHiloDeInterfaz(() =>
        {
            PistasAudio.Clear();
            foreach (var o in opciones) PistasAudio.Add(o);
            HayVariasPistasAudio = opciones.Count > 1;
            HayUnaSolaPistaAudio = opciones.Count == 1;
            SinPistasAudio = opciones.Count == 0;
        });

        if (!aplicarPreferencia) return;
        var preferida = OpcionPistaAudio.ElegirPreferida(opciones, _settingsService?.ObtenerConfiguracion()?.IdiomaAudioPreferido);
        if (preferida != null && !preferida.EsActual)
        {
            AppLogger.Debug("ReproductorViewModel", $"Pista de audio preferida ({preferida.Idioma}) elegida automáticamente.");
            CambiarPistaAudio(preferida, recordar: false);
        }
    }

    private void CambiarPistaAudio(OpcionPistaAudio opcion, bool recordar)
    {
        if (Player == null || opcion.Pista is not FlyleafLib.MediaFramework.MediaStream.AudioStream pista) return;

        EnHiloDeInterfaz(() => { foreach (var o in PistasAudio) o.EsActual = ReferenceEquals(o, opcion) || o.Pista == opcion.Pista; });

        if (recordar && !string.IsNullOrWhiteSpace(opcion.Idioma) && _settingsService?.ObtenerConfiguracion() is { } config
            && !string.Equals(config.IdiomaAudioPreferido, opcion.Idioma, StringComparison.OrdinalIgnoreCase))
        {
            config.IdiomaAudioPreferido = opcion.Idioma;
            _ = _settingsService.GuardarConfiguracionAsync(config);
        }

        try
        {
            _cambiandoPistaAudio = true;
            Player.OpenAsync(pista);
        }
        catch (Exception ex)
        {
            _cambiandoPistaAudio = false;
            AppLogger.Warn("ReproductorViewModel", $"No se pudo cambiar la pista de audio: {ex.Message}");
        }
    }

    private static void EnHiloDeInterfaz(Action accion)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) accion();
        else dispatcher.InvokeAsync(accion);
    }

    [RelayCommand]
    public void TurnOffSubtitles()
    {
        DeshabilitarSubtitulos();
    }

    public void EvaluarSubtitulosPorDefecto()
    {
        try
        {
            bool permitirSubtitulos = _settingsService?.ObtenerConfiguracion()?.SubtitulosPorDefecto ?? true;

            if (_subtitleCoordinator.DebenHabilitarsePorDefecto(Player, permitirSubtitulos))
            {
                HabilitarSubtitulos();

                // Flyleaf tiene sus subtítulos apagados y no elige pista: la elige la app (idioma de la app, luego inglés) y la
                // abre como si el usuario la hubiera escogido en el menú.
                if (!_pistaSubtitulosElegida)
                {
                    _pistaSubtitulosElegida = true;
                    var pista = _subtitleCoordinator.PistaPorDefecto(Player, LocalizationService.Cultura.TwoLetterISOLanguageName);
                    if (pista != null) AbrirPistaSubtitulos(pista);
                }
            }
            else
            {
                DeshabilitarSubtitulos();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al evaluar subtítulos por defecto: {ex.Message}");
        }
    }

    public void DeshabilitarSubtitulos()
    {
        SubtitulosHabilitados = false;
        SubtitulosIcon = "SubtitlesOutline";
        _subtitleCoordinator.Deshabilitar(Player);

        // No hace falta volver a extraer al reactivarlos: la pista no cambió. Solo se limpian las líneas para que
        // no quede una imagen fantasma mientras están apagados.
        SubtituloLineaAbajo = string.Empty;
        SubtituloLineaArriba = string.Empty;
    }

    /// <summary>Solo marca el estado: a Flyleaf no se le encienden los subtítulos aquí (abriría y leería una pista por su cuenta);
    /// qué se muestra lo decide <see cref="AbrirPistaSubtitulos"/>.</summary>
    public void HabilitarSubtitulos()
    {
        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
    }

    public EpisodioItem? ObtenerSiguienteEpisodio() => _episodeNavigator.ObtenerSiguiente(_episodio);

    public EpisodioItem? ObtenerAnteriorEpisodio() => _episodeNavigator.ObtenerAnterior(_episodio);

    public void ActualizarEstadosNavegacionEpisodios()
    {
        var siguiente = ObtenerSiguienteEpisodio();
        _siguienteEpisodioCache = siguiente;
        TieneEpisodioSiguiente = siguiente != null && !string.IsNullOrWhiteSpace(siguiente.RutaCompleta);
        AvisoSaltoSiguiente = TieneEpisodioSiguiente ? TextoEpisodiosSaltados(_episodio, siguiente!.NumeroEpisodio) : string.Empty;
        EpisodioSiguienteTooltip = TieneEpisodioSiguiente
            ? ConAviso(string.Format(LocalizationService.T("Player_SiguienteTooltipFormato"), siguiente!.NumeroEpisodio, NombreTecla("SiguienteEpisodio")), AvisoSaltoSiguiente)
            : LocalizationService.T("Player_SinSiguienteEpisodio");

        var anterior = ObtenerAnteriorEpisodio();
        TieneEpisodioAnterior = anterior != null && !string.IsNullOrWhiteSpace(anterior.RutaCompleta);
        AvisoSaltoAnterior = TieneEpisodioAnterior ? TextoEpisodiosSaltados(_episodio, anterior!.NumeroEpisodio) : string.Empty;
        EpisodioAnteriorTooltip = TieneEpisodioAnterior
            ? ConAviso(string.Format(LocalizationService.T("Player_AnteriorTooltipFormato"), anterior!.NumeroEpisodio, NombreTecla("AnteriorEpisodio")), AvisoSaltoAnterior)
            : LocalizationService.T("Player_SinEpisodioAnterior");

        MarcarEpisodioReproduciendose();

        _smtc?.ActualizarNavegacionDisponible(TieneEpisodioSiguiente, TieneEpisodioAnterior);
    }

    [RelayCommand]
    public void SiguienteEpisodio() => IrAEpisodio(ObtenerSiguienteEpisodio(), avisarSalto: true);

    [RelayCommand]
    public void AnteriorEpisodio() => IrAEpisodio(ObtenerAnteriorEpisodio(), avisarSalto: true);

    /// <summary>
    /// Abre otro episodio de la misma lista. Si entre medias faltan episodios sin descargar (del 3 al 6), se avisa: antes se
    /// saltaba en silencio y parecía que el 6 era el que seguía. La cuenta atrás ya lo avisa en pantalla, así que ahí no se repite.
    /// </summary>
    private void IrAEpisodio(EpisodioItem? destino, bool avisarSalto)
    {
        if (destino == null || string.IsNullOrWhiteSpace(destino.RutaCompleta)) return;

        string aviso = TextoEpisodiosSaltados(_episodio, destino.NumeroEpisodio);
        if (avisarSalto && aviso.Length > 0)
        {
            AvisarEnReproductor(
                string.Format(LocalizationService.T("Player_SaltoTitulo"), destino.NumeroEpisodio), aviso, "AlertOutline", "#F59E0B");
        }

        // Misma lista (null): antes se pasaba una copia y el cajón rehacía todas sus filas (1180 en One Piece) en cada cambio.
        CargarVideo(destino.RutaCompleta, _animeId, TituloAnime, destino.NumeroEpisodio, null, _rutaPortada);
    }

    /// <summary>"Te saltas el episodio 4 (no está descargado)" / "Te saltas los episodios 4–6 (3 sin descargar)"; vacío si son consecutivos.</summary>
    internal static string TextoEpisodiosSaltados(int actual, int destino)
    {
        var faltan = EpisodeNavigator.EpisodiosIntermedios(actual, destino);
        return faltan.Count switch
        {
            0 => string.Empty,
            1 => string.Format(LocalizationService.T("Player_SaltoUnEpisodio"), faltan[0]),
            _ => string.Format(LocalizationService.T("Player_SaltoVariosEpisodios"), faltan[0], faltan[^1], faltan.Count),
        };
    }

    private static string ConAviso(string texto, string aviso) => aviso.Length == 0 ? texto : $"{texto}\n⚠ {aviso}";

    /// <summary>Marca en el cajón de episodios la fila del episodio que está sonando (y desmarca la anterior).</summary>
    private void MarcarEpisodioReproduciendose()
    {
        var actual = _episodeNavigator.EpisodiosDisponibles.FirstOrDefault(e => e.NumeroEpisodio == _episodio);
        if (ReferenceEquals(actual, _itemReproduciendose)) return;
        if (_itemReproduciendose != null) _itemReproduciendose.EsReproduciendose = false;
        _itemReproduciendose = actual;
        if (actual != null) actual.EsReproduciendose = true;
    }

    public EpisodioItem? EpisodioReproduciendose => _itemReproduciendose;

    /// <summary>
    /// Con la lista de la ficha, el favorito sale de su fila; si se abrió sin lista (desde Historial o Actualizaciones) se lee de la
    /// base de datos. Si mientras tanto se cambió de episodio, la respuesta vieja se descarta.
    /// </summary>
    private async Task CargarFavoritoAsync(int version)
    {
        if (_itemReproduciendose != null)
        {
            EpisodioEsFavorito = _itemReproduciendose.Favorito;
            return;
        }

        EpisodioEsFavorito = false;
        try
        {
            var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(_animeId);
            if (version != _versionCarga) return;
            EpisodioEsFavorito = registros?.FirstOrDefault(r => r.NumeroEpisodio == _episodio)?.FavoritoLocal == true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo leer el favorito del episodio: {ex.Message}");
        }
    }

    /// <summary>
    /// Marca o desmarca como favorito el episodio que se está viendo. Solo cambia esa marca (no el progreso), y la fila de la ficha
    /// y del cajón se actualizan a la vez porque son el mismo EpisodioItem.
    /// </summary>
    [RelayCommand]
    private async Task AlternarFavoritoEpisodioAsync()
    {
        if (_animeId <= 0) return;

        bool favorito = !EpisodioEsFavorito;
        EpisodioEsFavorito = favorito;
        if (_itemReproduciendose != null) _itemReproduciendose.Favorito = favorito;

        try
        {
            await _databaseService.GuardarFavoritoEpisodioAsync(_animeId, _episodio, favorito, _rutaVideo);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ReproductorViewModel", $"No se pudo guardar el favorito del episodio {_episodio}: {ex.Message}");
            EpisodioEsFavorito = !favorito;
            if (_itemReproduciendose != null) _itemReproduciendose.Favorito = !favorito;
        }
    }

    /// <summary>ARQ-01: decisión pura de pre-carga — movida a <see cref="EpisodeNavigator"/>; se
    /// mantiene este reenvío para no tocar los tests existentes que la llaman por este nombre.</summary>
    internal static bool DebePrecargarSiguienteEpisodio(double porcentaje, string? rutaSiguiente, string? rutaYaPrecargada) =>
        EpisodeNavigator.DebePrecargarSiguienteEpisodio(porcentaje, rutaSiguiente, rutaYaPrecargada);

    private CancellationTokenSource? _trackingCts;

    /// <summary>
    /// PERF: <see cref="ObtenerSiguienteEpisodio"/> recorre y ordena _episodiosDisponibles (puede tener
    /// cientos/miles de elementos en animes largos, p. ej. One Piece). La lista no cambia durante la
    /// reproducción del episodio actual, así que el resultado se calcula una vez en
    /// ActualizarEstadosNavegacionEpisodios() y el loop de progreso (250ms) lee este caché en vez de
    /// recalcularlo en cada tick.
    /// </summary>
    private EpisodioItem? _siguienteEpisodioCache;

    public Task VerificarProgresoPrevioAsync(int animeId, int episodio) => VerificarProgresoPrevioAsync(animeId, episodio, _versionCarga);

    private async Task VerificarProgresoPrevioAsync(int animeId, int episodio, int version)
    {
        try
        {
            var previo = await _playbackState.ObtenerPosicionParaReanudarAsync(animeId, episodio, _rutaVideo);
            if (version != _versionCarga) return; // ya se abrió otro episodio: estos datos no son suyos

            if (previo.HasValue && previo.Value.Posicion > 5)
            {
                var (posicion, duracion) = previo.Value;
                _resumingPositionSeconds = posicion;
                _posicionInicioSegundos = posicion;
                if (duracion > 0) EstablecerDuracion(duracion);
                CurrentSeconds = posicion;
                _lastNotifiedSeconds = posicion;
                ActualizarTextosTiempo(posicion);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error comprobando progreso previo: {ex.Message}");
        }

        if (version == _versionCarga) _reanudacionPendiente = false; // sin punto guardado: la barra ya puede seguir al video
    }

    public async Task CargarSkipTimesAsync(int animeId, int episodio, CancellationToken ct = default)
    {
        try
        {
            // AniSkip API como fuente primaria; si no hay datos, detección local por escenas (Python/ffmpeg)
            // usando la ruta del video local actual (requiere un archivo en disco).
            // Los tramos parciales (solo el audio, luego con AniSkip) se aplican según llegan: la barra no espera al final del análisis.
            // Progress se crea aquí, en el hilo de la UI, así que sus avisos también se ejecutan en él.
            var progreso = new Progress<IReadOnlyList<AniSkipResult>>(parcial =>
            {
                if (!ct.IsCancellationRequested) AplicarSkipTimes(parcial);
            });

            var results = await _skipCoordinator.CargarSkipTimesAsync(animeId, episodio, TotalSeconds, RutaVideo, progreso, ct);
            if (!ct.IsCancellationRequested && results != null && results.Count > 0)
            {
                AplicarSkipTimes(results);
            }

            if (!ct.IsCancellationRequested) ProgramarPreanalisisDelSiguiente(animeId, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error cargando skip times de AniSkip: {ex.Message}");
        }
    }

    /// <summary>
    /// Con el episodio actual ya analizado, deja listo el siguiente (si tiene archivo local): al pasar de capítulo, las marcas de opening y
    /// ending salen al instante en vez de tardar unos segundos. Espera un poco antes para no competir con el arranque del video, y si en
    /// esa espera se cambia de episodio no se hace (ese cambio ya lanza su propio análisis).
    /// </summary>
    private void ProgramarPreanalisisDelSiguiente(int animeId, CancellationToken ctEpisodio)
    {
        var siguiente = _siguienteEpisodioCache ?? ObtenerSiguienteEpisodio();
        if (animeId <= 0 || siguiente == null || siguiente.NumeroEpisodio <= 0 || string.IsNullOrWhiteSpace(siguiente.RutaCompleta)) return;

        Core.Cancelacion.Reemplazar(ref _preanalisisCts);
        _ = PreanalizarSiguienteAsync(animeId, siguiente.NumeroEpisodio, siguiente.RutaCompleta, ctEpisodio, _preanalisisCts.Token);
    }

    private async Task PreanalizarSiguienteAsync(int animeId, int episodio, string ruta, CancellationToken ctEpisodio, CancellationToken ct)
    {
        try
        {
            await Task.Delay(EsperaPreanalisis, ctEpisodio);
            if (!System.IO.File.Exists(ruta)) return;
            await _skipCoordinator.PreanalizarAsync(animeId, episodio, ruta, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error pre-analizando el episodio {episodio}: {ex.Message}");
        }
    }

    /// <summary>Deja los tramos como los vigentes del episodio (saltos y marcadores de la barra).</summary>
    private void AplicarSkipTimes(IReadOnlyList<AniSkipResult> tramos)
    {
        Interlocked.Exchange(ref _skipTimes, new List<AniSkipResult>(tramos));
        SegmentosLineaTiempo = SegmentoLineaTiempo.Crear(tramos, TotalSeconds);
    }

    public void CargarVideo(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null, string? rutaPortada = null)
    {
        _ = CargarVideoAsync(rutaVideo, animeId, tituloAnime, episodio, listaEpisodios, rutaPortada);
    }

    public async Task CargarVideoAsync(string rutaVideo, int animeId, string tituloAnime, int episodio, List<EpisodioItem>? listaEpisodios = null, string? rutaPortada = null)
    {
        int version = ++_versionCarga;
        _relojCambio.Restart();
        _ = GuardarProgresoActualAsync();

        CancellationToken skipCtToken = CancelarTrabajoPendienteDelEpisodioAnterior();
        RecargarConfiguracionDeSesion();
        AsignarMetadatosDeEpisodio(rutaVideo, animeId, episodio, tituloAnime, rutaPortada);
        EstablecerListaDeEpisodiosSiCorresponde(listaEpisodios);
        ActualizarEstadosNavegacionEpisodios();
        _ = CargarFavoritoAsync(version);

        // Cancelar rastreo previo
        Core.Cancelacion.Reemplazar(ref _trackingCts);

        // 1. Asegurar que Player existe antes de configurar el nuevo archivo. No se detiene antes el video anterior: la apertura de
        //    Flyleaf ya lo reinicia en su propio hilo, y el Stop() previo repetía ese trabajo en el de la interfaz (~200 ms bloqueada
        //    en cada cambio de episodio). El audio viejo se silencia justo antes de abrir y la tapa cubre la última imagen.
        AsegurarPlayerInicializado();

        // 2. El video queda tapado desde ya: se destapa con el primer fotograma o, si hay que reanudar, cuando el salto al punto
        //    guardado se asienta (VigilarArranqueAsync). Antes se destapaba en el primer instante de reproducción y se veía el 0:00.
        OcultarVideoInicio = true;
        _ocultarVideoDesdeUtc = DateTime.UtcNow;
        _reanudacionPendiente = true;

        // 3. El punto guardado se busca EN PARALELO con la apertura (antes la apertura esperaba a la base de datos: en animes largos
        //    se leían todos sus episodios antes de empezar a abrir el archivo). El salto se aplica con la imagen ya lista.
        _tareaReanudacion = VerificarProgresoPrevioAsync(animeId, episodio, version);

        // 4. Cargar marcas de skip de AniSkip en segundo plano
        _ = CargarSkipTimesAsync(animeId, episodio, skipCtToken);

        // 5. Sincronizar ícono de fullscreen con el estado actual de la ventana
        string? iconoFullscreen = _windowModeCoordinator.IconoPantallaCompletaActual();
        if (iconoFullscreen != null) FullscreenIcon = iconoFullscreen;

        if (Player != null)
        {
            _relojArranque.Restart();
            _arranqueRegistrado = false;
            _cambiandoPistaAudio = false;
            _imagenLista = false;
            if (_reabiertoPorProcesador)
            {
                // El episodio anterior no pudo con la tarjeta gráfica; este se juzga por sí solo con el ajuste del usuario.
                _reabiertoPorProcesador = false;
                Player.Config.Video.VideoAcceleration = _settingsService?.ObtenerConfiguracion()?.AceleracionHardwareVideo ?? true;
            }

            // El audio arrancaba antes que la imagen (el decodificador de video tarda ~0,5-0,8 s en mostrar el primer fotograma y, al
            // reanudar, además sonaba el principio del episodio antes del salto). Se abre en silencio y VigilarArranqueAsync lo quita
            // cuando la imagen ya está en pantalla.
            try { if (Player.Audio != null) Player.Audio.Mute = true; } catch { }
            AppLogger.Debug("ReproductorViewModel", $"[Cambio] Episodio {episodio}: listo para abrir a los {_relojCambio.ElapsedMilliseconds} ms.");
            Player.OpenAsync(rutaVideo);
            _ = VigilarAperturaAsync(rutaVideo, _trackingCts.Token);
            _ = CargarFotogramasClaveAsync(rutaVideo, _trackingCts.Token);

            // Velocidad de reproducción por defecto configurable
            try
            {
                double velocidad = _settingsService?.ObtenerConfiguracion()?.VelocidadReproduccionDefecto ?? 1.0;
                Player.Speed = (float)Math.Clamp(velocidad, 0.5, 2.0);
            }
            catch { }
        }

        _ = RastrearProgresoAsync(_trackingCts.Token);

        // Quien espera a este método (navegación, pruebas) lo ve terminado con la posición guardada ya en la barra.
        await _tareaReanudacion;
    }

    /// <summary>
    /// Cancela/reinicia todo lo que quedaba en curso del episodio anterior (seek coalescido,
    /// detección de skips, pre-carga del siguiente). Devuelve el token de la detección de skips
    /// YA capturado en una variable local — a propósito, no debe releerse <c>_skipCts</c> después
    /// del primer <c>await</c> de <see cref="CargarVideoAsync"/>: una segunda llamada concurrente
    /// (doble clic en "Siguiente") podría reasignar el campo antes de que se use el token.
    /// </summary>
    private CancellationToken CancelarTrabajoPendienteDelEpisodioAnterior()
    {
        // Descartar seeks coalescidos pendientes del episodio anterior
        _seekCoordinator.Reiniciar();

        // Cancelar detección de skips previa
        Core.Cancelacion.Reemplazar(ref _skipCts);
        var currentSkipCts = _skipCts;

        Interlocked.Exchange(ref _skipTimes, new List<AniSkipResult>());
        SegmentosLineaTiempo = Array.Empty<SegmentoLineaTiempo>();
        _skipAutoEjecutados.Clear();
        _currentActiveSkip = null;
        MostrarSkipButton = false;
        MostrarSkipIntro = false;
        _autoPlayEjecutado = false;
        MostrarCuentaAtrasSiguiente = false;

        // Cancelar la pre-carga del siguiente episodio del video anterior (si seguía en curso)
        _episodeNavigator.CancelarPrecargaYReiniciar();

        return currentSkipCts.Token;
    }

    private void RecargarConfiguracionDeSesion()
    {
        if (_settingsService == null) return;

        var config = _settingsService.ObtenerConfiguracion();
        if (config == null) return;

        AutoSkipIntroOutro = config.AutoSkipIntroOutro;
        AccionFinEpisodio = string.IsNullOrWhiteSpace(config.AccionFinEpisodio) ? AccionFinEpisodioValores.AutoPlayCuentaAtras : config.AccionFinEpisodio;
        PasosSaltoSegundos = config.PasosSaltoSegundos is 5 or 10 or 30 or 60 ? config.PasosSaltoSegundos : 10;
        SubtitulosHabilitados = config.SubtitulosPorDefecto;
        SubtitulosIcon = config.SubtitulosPorDefecto ? "Subtitles" : "SubtitlesOutline";
        EstiloSubtitulos = (config.EstiloSubtitulos ?? new EstiloSubtitulos()).Normalizar();
    }

    private void AsignarMetadatosDeEpisodio(string rutaVideo, int animeId, int episodio, string tituloAnime, string? rutaPortada)
    {
        _rutaVideo = rutaVideo;
        _animeId = animeId;
        _episodio = episodio;
        OnPropertyChanged(nameof(Episodio));
        _rutaPortada = rutaPortada;
        TituloAnime = tituloAnime;
        TituloEpisodio = string.Format(LocalizationService.T("Act_EpisodioFormato"), episodio);
        _fueMarcadoComoVisto = false;

        // SMT-01: overlay nativo de Windows — título, episodio y portada del anime que arranca.
        _smtc?.ActualizarMetadatos(tituloAnime, episodio, rutaPortada);
        _durationCached = false;
        _lastNotifiedSeconds = -1;
        _lastSavedSeconds = -1;
        _resumingPositionSeconds = 0;
        _posicionInicioSegundos = 0;
        _haCompletadoOpen = false;
        _finDeEpisodioProcesado = false;
        _pistaSubtitulosElegida = false;
        ReiniciarCuesSubtitulos(); // las líneas del episodio anterior no valen para este
        _reintentosApertura = 0;
        _estrategiaSaltos = EstrategiaSaltos.Equilibrados;

        // La barra del episodio nuevo empieza en cero y sin duración hasta conocer la real. Antes seguía mostrando la del
        // anterior (p. ej. "23:59 / 24:00" con la barra llena) y los marcadores del nuevo se recortaban con esa duración vieja:
        // un ending que empezara después del final del episodio anterior desaparecía de la barra.
        CurrentSeconds = 0;
        EstablecerDuracion(0);
        ActualizarTextosTiempo(0);
    }

    private void EstablecerListaDeEpisodiosSiCorresponde(List<EpisodioItem>? listaEpisodios)
    {
        if (listaEpisodios == null) return;

        _episodeNavigator.EstablecerEpisodios(listaEpisodios);
        OnPropertyChanged(nameof(EpisodiosDelCajon));
    }


    /// <summary>
    /// Arranque sin audio adelantado: espera al primer fotograma en pantalla, hace el salto de reanudación en ese momento (con el
    /// decodificador ya listo, sin esperar al siguiente tick del bucle) y solo entonces devuelve el sonido. Medido en un AV1 1080p:
    /// la imagen aparecía ~0,75 s después que el audio, y al reanudar sonaba el principio del episodio antes de saltar.
    /// </summary>
    private async Task VigilarArranqueAsync(Player player, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        bool reabierto = false;
        try
        {
            while (!ct.IsCancellationRequested && !player.IsDisposed && reloj.ElapsedMilliseconds < 3000 && FotogramasMostrados(player) == 0)
                await Task.Delay(10, ct);
            AppLogger.Debug("ReproductorViewModel", $"[Arranque] Primer fotograma a los {_relojArranque.ElapsedMilliseconds} ms.");

            if (!ct.IsCancellationRequested && !player.IsDisposed &&
                MotorVideo.DebeReintentarPorProcesador(player.VideoDecoder?.VideoAccelerated ?? false, FotogramasMostrados(player), _reabiertoPorProcesador))
            {
                // El OpenCompleted de la reapertura lanza otro vigilante: este termina sin destapar el video ni dar por lista la imagen.
                reabierto = true;
                ReabrirPorProcesador(player);
                return;
            }

            // La consulta del punto guardado suele terminar mucho antes (milisegundos); si la base de datos va lenta, se espera un poco.
            try { await _tareaReanudacion.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (TimeoutException) { }
            // _imagenLista se activa en el finally: así el bucle de progreso solo aplica la reanudación como respaldo si este vigilante
            // falla, y nunca se adelanta a él (se destaparía el video sin esperar a que el salto se asiente).

            double duracion = TimeSpan.FromTicks(player.Duration).TotalSeconds;
            if (_posicionInicioSegundos > 5 && duracion > 0 && !ct.IsCancellationRequested)
            {
                double objetivo = Math.Min(_posicionInicioSegundos, Math.Max(0, duracion - 1.0));
                uint fotogramasAntes = FotogramasMostrados(player);
                await EnHiloDeInterfazAsync(() => AplicarSeekDeArranqueDiferidoSiCorresponde(duracion));

                // El salto rápido cae en el fotograma clave anterior (hasta ~10 s antes): se da por asentado cuando la posición está
                // cerca y ya se pintó un fotograma nuevo.
                var espera = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested && !player.IsDisposed && espera.ElapsedMilliseconds < 2500)
                {
                    double actual = TimeSpan.FromTicks(player.CurTime).TotalSeconds;
                    if (Math.Abs(actual - objetivo) < 15 && FotogramasMostrados(player) > fotogramasAntes + 1) break;
                    await Task.Delay(15, ct);
                }
                AppLogger.Debug("ReproductorViewModel", $"[Arranque] Reanudación asentada a los {_relojArranque.ElapsedMilliseconds} ms.");
            }

            // Imagen lista y, si tocaba, ya en el punto guardado: se destapa el video.
            if (!ct.IsCancellationRequested)
            {
                bool reanudo = _resumingPositionSeconds > 5;
                await EnHiloDeInterfazAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _reanudacionPendiente = false;
                    if (reanudo)
                    {
                        // El salto rápido cae en el fotograma clave anterior al punto guardado (unos segundos antes): la barra muestra
                        // desde ya la posición real, en vez de marcar 05:00 y retroceder a 04:59 al destapar.
                        double real = TimeSpan.FromTicks(player.CurTime).TotalSeconds;
                        if (real > 0) { CurrentSeconds = real; _lastNotifiedSeconds = real; ActualizarTextosTiempo(real); }
                    }
                    OcultarVideoInicio = false;
                });
                AppLogger.Debug("ReproductorViewModel", $"[Cambio] Episodio {_episodio}: imagen visible a los {_relojCambio.ElapsedMilliseconds} ms desde que se pidió.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error vigilando el arranque: {ex.Message}");
        }
        finally
        {
            if (!reabierto)
            {
                _imagenLista = true; // aunque se haya cancelado o agotado la espera: el bucle no debe quedarse sin aplicar la reanudación
                AplicarRendimientoSegunEquipo(player);
                try { if (!player.IsDisposed && player.Audio != null && !_cambiandoPistaAudio) player.Audio.Mute = IsMuted; } catch { }
            }
        }
    }

    /// <summary>El episodio actual ya se reabrió una vez sin tarjeta gráfica (se restablece al cambiar de episodio).</summary>
    private bool _reabiertoPorProcesador;

    /// <summary>Apaga la decodificación por tarjeta solo para este episodio y lo reabre: Flyleaf elige entonces libdav1d.</summary>
    private void ReabrirPorProcesador(Player player)
    {
        _reabiertoPorProcesador = true;
        string ruta = _rutaVideo;
        AppLogger.Warn("ReproductorViewModel", $"La tarjeta gráfica no mostró imagen en 3 s ({player.Video?.Codec}); se reabre decodificando por procesador.");
        try
        {
            player.Config.Video.VideoAcceleration = false;
            _cambiandoPistaAudio = false;
            _haCompletadoOpen = false;
            player.Stop();
            player.OpenAsync(ruta);
        }
        catch (Exception ex) { AppLogger.Warn("ReproductorViewModel", $"Falló la reapertura por procesador: {ex.Message}"); }
    }

    private async Task CargarFotogramasClaveAsync(string ruta, CancellationToken ct)
    {
        _fotogramasClave = null;
        try
        {
            var lista = await _fotogramasClaveService.ObtenerAsync(ruta, ct);
            if (ct.IsCancellationRequested || !string.Equals(ruta, _rutaVideo, StringComparison.OrdinalIgnoreCase)) return;
            _fotogramasClave = lista;
            AppLogger.Debug("ReproductorViewModel", lista == null
                ? "Sin lista de fotogramas clave: los saltos serán precisos."
                : $"{lista.Count} fotogramas clave leídos (saltos instantáneos cuando hay uno cerca del destino).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudieron leer los fotogramas clave: {ex.Message}");
        }
    }

    /// <summary>Cómo se resuelven los saltos en este video (ver <see cref="MotorVideo.ElegirEstrategiaSaltos"/>). Hasta conocer el
    /// decodificador real se usa la equilibrada.</summary>
    private EstrategiaSaltos _estrategiaSaltos = EstrategiaSaltos.Equilibrados;

    /// <summary>
    /// Con la imagen ya en pantalla se sabe qué decodificador se usa DE VERDAD: Flyleaf pide la tarjeta gráfica y, si el formato no
    /// está soportado (AV1 en una Intel HD 620), cae en silencio a software (libdav1d). Leído al abrir decía "hardware" siempre. Con ese
    /// dato se eligen los saltos y el escalado inteligente del modo automático, y se deja el informe para Configuración.
    /// </summary>
    private void AplicarRendimientoSegunEquipo(Player player)
    {
        try
        {
            var ajustes = _settingsService?.ObtenerConfiguracion();
            bool porHardware = player.VideoDecoder?.VideoAccelerated ?? false;
            string codec = player.Video?.Codec ?? "?";
            var tarjeta = player.Renderer?.GPUAdapter;

            _estrategiaSaltos = MotorVideo.ElegirEstrategiaSaltos(ajustes?.ModoSaltosVideo, porHardware, Environment.ProcessorCount);

            bool escalado = tarjeta != null && MotorVideo.UsarEscaladoInteligente(ajustes?.EscaladoInteligenteVideo, tarjeta.Vendor, (long)((ulong)tarjeta.VideoMemory / (1024 * 1024)));
            if (player.Config.Video.SuperResolution != escalado) player.Config.Video.SuperResolution = escalado;

            AppLogger.Info("ReproductorViewModel", $"Video: {codec} por {(porHardware ? "tarjeta gráfica" : "procesador")} · tarjeta {tarjeta?.Description ?? "?"} · " +
                $"{Environment.ProcessorCount} hilos · saltos {_estrategiaSaltos} · escalado {(escalado ? "sí" : "no")}.");
            MotorVideo.PublicarInforme(new InformeVideo(codec, porHardware, tarjeta?.Description, _estrategiaSaltos, escalado, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo evaluar el rendimiento del video: {ex.Message}");
        }
    }

    // Contadores internos de Flyleaf que suben con CADA fotograma presentado (reproduciendo y en pausa/salto). El público
    // Video.FramesDisplayed solo se refresca una vez por segundo: esperar a él retrasaba hasta 1 s el destape del video.
    private static readonly System.Reflection.FieldInfo? CampoFotogramasReproducidos =
        typeof(Player).GetField("framesDisplayed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? CampoFotogramasMostradosEnPausa =
        typeof(Player).GetField("showFrameCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static uint FotogramasMostrados(Player player)
    {
        try
        {
            if (CampoFotogramasReproducidos != null && CampoFotogramasMostradosEnPausa != null)
                return (uint)CampoFotogramasReproducidos.GetValue(player)! + (uint)CampoFotogramasMostradosEnPausa.GetValue(player)!;
            return player.Video?.FramesDisplayed ?? 0; // si una versión nueva de Flyleaf cambia los nombres: el contador público
        }
        catch { return 0; }
    }

    private static Task EnHiloDeInterfazAsync(Action accion)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            accion();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(accion).Task;
    }

    /// <summary>
    /// Si Flyleaf no termina de abrir en <see cref="TiempoMaximoApertura"/> (a veces se quedaba cargando sin fin, sobre todo al reanudar),
    /// se reintenta una vez; si tampoco, se avisa en vez de dejar el spinner para siempre.
    /// </summary>
    private async Task VigilarAperturaAsync(string ruta, CancellationToken ct)
    {
        try
        {
            for (int intento = 0; intento < 2; intento++)
            {
                await Task.Delay(TiempoMaximoApertura, ct);
                if (ct.IsCancellationRequested || _haCompletadoOpen || !string.Equals(ruta, _rutaVideo, StringComparison.OrdinalIgnoreCase)) return;

                if (intento == 0 && ReintentarAperturaSiCorresponde($"sin respuesta tras {TiempoMaximoApertura.TotalSeconds:F0} s")) continue;

                NotificarFalloAlAbrir(LocalizationService.T("Player_ErrorAbrirTiempo"));
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Un reintento por episodio: el archivo existe pero Flyleaf no lo abrió (o se quedó abriendo). True si se reintentó.</summary>
    private bool ReintentarAperturaSiCorresponde(string? error)
    {
        string ruta = _rutaVideo;
        var player = Player;
        if (_reintentosApertura >= 1 || player == null || player.IsDisposed || !System.IO.File.Exists(ruta)) return false;

        _reintentosApertura++;
        AppLogger.Warn("ReproductorViewModel", $"El episodio no se abrió ({error}); se reintenta una vez.");
        var ct = _trackingCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, ct);
                if (ct.IsCancellationRequested || player.IsDisposed || !string.Equals(ruta, _rutaVideo, StringComparison.OrdinalIgnoreCase)) return;
                try { player.Stop(); } catch { }
                _cambiandoPistaAudio = false;
                player.OpenAsync(ruta);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Warn("ReproductorViewModel", $"Falló el reintento de apertura: {ex.Message}"); }
        }, ct);
        return true;
    }

    /// <summary>True si, estando al final (cuenta atrás en curso), el usuario retrocedió o volvió a dar a reproducir.</summary>
    private bool UsuarioVolvioAReproducir()
    {
        try { return Player != null && !Player.IsDisposed && Player.Status == Status.Playing; }
        catch { return false; }
    }

    /// <summary>Si el archivo no se puede abrir (borrado, movido, dañado o formato no soportado) antes solo se veía la pantalla
    /// negra sin ningún aviso. Ahora se explica y se registra el motivo.</summary>
    private void NotificarFalloAlAbrir(string? error)
    {
        AppLogger.Error("ReproductorViewModel", $"No se pudo abrir '{_rutaVideo}': {error}");
        OcultarVideoInicio = false;
        try { if (Player?.Audio != null) Player.Audio.Mute = IsMuted; } catch { }
        PlayPauseIcon = "Play";
        string mensaje = System.IO.File.Exists(_rutaVideo)
            ? string.Format(LocalizationService.T("Player_ErrorAbrirMsj"), string.IsNullOrWhiteSpace(error) ? "?" : error)
            : LocalizationService.T("Player_ErrorAbrirNoExiste");
        AvisarEnReproductor(
            LocalizationService.T("Player_ErrorAbrirTitulo"), mensaje, "AlertCircleOutline", "#EF4444");
    }

    /// <summary>
    /// Aviso encima del video. Se marca como del reproductor para que la vista lo muestre: los avisos de fuera (una descarga que
    /// termina, un episodio nuevo en emisión…) ya no se cuelan en mitad del episodio.
    /// </summary>
    private void AvisarEnReproductor(string titulo, string mensaje, string icono, string color)
    {
        if (DialogService != null) DialogService.MostrarToastReproductor(titulo, mensaje, icono, color);
        else _ = WeakReferenceMessenger.Default.Send(new Messages.MostrarDialogoRequestMessage(titulo, mensaje, false, icono, color));
    }

    /// <summary>Nombre corto de la tecla configurada para mostrarlo en tooltips ("N", "Espacio"…).</summary>
    private string NombreTecla(string accion)
    {
        var tecla = ObtenerTeclaPara(accion);
        return tecla == System.Windows.Input.Key.None ? "—" : tecla.ToString();
    }

    /// <summary>Tecla configurada para una acción del reproductor (con fallback).</summary>
    public System.Windows.Input.Key ObtenerTeclaPara(string accion)
    {
        var config = _settingsService?.ObtenerConfiguracion();
        string porDefecto = accion switch
        {
            "PlayPausa" => "Space",
            "PantallaCompleta" => "F11",
            "Silenciar" => "M",
            "SubirVolumen" => "Up",
            "BajarVolumen" => "Down",
            "Adelantar10" => "Right",
            "Retroceder10" => "Left",
            "SaltarIntro" => "S",
            "SiguienteEpisodio" => "N",
            // PIP-01: "P" pasa a ser el atajo de Modo Mini/PiP (los tooltips ya lo anunciaban
            // así desde antes, pero el atajo real estaba tomado por AnteriorEpisodio — nunca
            // llegaba a activarse). AnteriorEpisodio se mueve a "B" para liberar "P".
            "AnteriorEpisodio" => "B",
            "ModoMini" => "P",
            "Cerrar" => "Escape",
            "CapturarFrame" => "C",
            _ => string.Empty
        };
        // Sin configuración cargada también valen las teclas de siempre (antes quedaban todas sin asignar).
        string nombre = config?.ObtenerTecla(accion, porDefecto) ?? porDefecto;

        return Enum.TryParse<System.Windows.Input.Key>(nombre, true, out var tecla)
            ? tecla
            : System.Windows.Input.Key.None;
    }

    /// <summary>
    /// Captura el fotograma actual (ver <see cref="FrameCaptureService"/>: snapshot directo del
    /// buffer Direct3D de Flyleaf, sin relanzar ffmpeg ni re-decodificar), lo copia al portapapeles
    /// y lo guarda en Imágenes\AnimeLocalTracker. Este comando solo traduce el resultado a un toast.
    /// </summary>
    [RelayCommand]
    private async Task CapturarFrameAsync()
    {
        string? ruta = await _frameCaptureService.CapturarYGuardarAsync(Player);

        if (ruta == null)
        {
            AvisarEnReproductor(
                LocalizationService.T("Player_CapturaTitulo"), LocalizationService.T("Player_CapturaErrorMsj"), "AlertCircleOutline", "#EF4444");
            return;
        }

        AvisarEnReproductor(
            LocalizationService.T("Player_CapturaTitulo"), string.Format(LocalizationService.T("Player_CapturaListaFormato"), ruta), "CameraOutline", "#4CAF50");
    }

    public async Task GuardarProgresoActualAsync(bool forzarProgresoCero = false)
    {
        if (_animeId <= 0 || _episodio <= 0) return;

        // FUN-011: un solo guardado a la vez. Si el tick de 3 s llega con otro en curso,
        // se descarta (el siguiente tick guardará el estado más reciente).
        if (!_guardadoLock.Wait(0)) return;

        // FUN-017: snapshot de identidad al iniciar — un guardado que termina después de
        // cambiar de episodio no debe notificar el progreso del episodio viejo bajo el ID
        // del nuevo (antes _animeId/_episodio se leían en el momento del envío). Esto incluye
        // _fueMarcadoComoVisto: si no se captura aquí, la notificación posterior al await lee
        // el campo YA reseteado a false por CargarVideoAsync del episodio siguiente (que arranca
        // de inmediato tras este guardado en fire-and-forget) y le avisa a DetalleView que el
        // capítulo que se acaba de terminar de ver "no está visto", aunque sí se guardó bien.
        int animeId = _animeId;
        int episodio = _episodio;
        string rutaVideo = _rutaVideo;
        bool fueMarcadoComoVisto = _fueMarcadoComoVisto;

        try
        {
            double curSec = 0;
            double durSec = 0;

            if (Player != null && !Player.IsDisposed)
            {
                curSec = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                durSec = TimeSpan.FromTicks(Player.Duration).TotalSeconds;
            }

            if (durSec <= 0 && TotalSeconds > 0) durSec = TotalSeconds;
            if (curSec <= 0 && CurrentSeconds > 0) curSec = CurrentSeconds;

            var resultado = await _playbackState.GuardarProgresoAsync(new DatosProgresoReproduccion
            {
                AnimeId = animeId,
                NumeroEpisodio = episodio,
                RutaVideo = rutaVideo,
                PosicionSegundos = curSec,
                DuracionSegundos = durSec,
                ForzarProgresoCero = forzarProgresoCero,
                FueMarcadoComoVisto = fueMarcadoComoVisto
            });

            // Notificar a DetalleViewModel para actualizar la barra de progreso en vivo
            // El "visto" que se avisa es el que quedó guardado: al volver a ver un episodio ya visto sigue visto (antes se
            // avisaba "no visto" cada 3 s y la Ficha lo desmarcaba en pantalla).
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(
                animeId, episodio, fueMarcadoComoVisto || resultado.VistoLocal, resultado.ProgresoSegundos, resultado.TotalSegundos, SoloProgreso: true));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al guardar progreso actual: {ex.Message}");
        }
        finally
        {
            _guardadoLock.Release();
        }
    }

    private async Task EvaluarLogrosAsync()
    {
        if (_logrosService == null) return;

        try
        {
            // Un logro desbloqueado por el episodio que acabas de terminar sí se avisa encima del video (como el auto-tracking).
            using (DialogService?.AvisosComoDelReproductor())
            {
                await _logrosService.EvaluarAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudieron evaluar los logros: {ex.Message}");
        }
    }

    public async Task RealizarAutoTrackingAsync()
    {
        if (_fueMarcadoComoVisto) return;
        _fueMarcadoComoVisto = true;

        try
        {
            bool ok = await _playbackState.MarcarComoVistoYSincronizarAsync(_animeId, _episodio, _rutaVideo, TotalSeconds);
            if (!ok) return;

            // Notificación flotante sutil (Toast)
            AvisarEnReproductor(
                "Auto-Tracking",
                string.Format(LocalizationService.T("Player_EpisodioMarcadoVistoMsj"), _episodio),
                "CheckCircle", "#4CAF50");

            // Avisar a la vista de detalles para que actualice la lista automáticamente
            WeakReferenceMessenger.Default.Send(new Messages.EpisodioActualizadoMensaje(_animeId, _episodio, true, 0, TotalSeconds));

            // Terminar un episodio puede desbloquear un logro: se evalúa aquí para avisar en el momento
            // (y no solo la próxima vez que abras Estadísticas o Logros).
            _ = EvaluarLogrosAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ReproductorViewModel", "Error en auto-tracking", ex);
        }
    }

    /// <summary>
    /// Qué hacer al llegar al final de un episodio, según Configuración → Reproducción → "Al terminar
    /// un episodio". Se llama UNA vez por episodio (guardado por <c>_autoPlayEjecutado</c> en el
    /// llamador). "Permanecer pausado" y "sin siguiente episodio" no hacen nada: el video se queda
    /// en el último fotograma, que es exactamente el comportamiento por defecto de Flyleaf al terminar.
    /// </summary>
    private async Task EjecutarAccionFinEpisodioAsync(CancellationToken ct)
    {
        if (!TieneEpisodioSiguiente || AccionFinEpisodio == AccionFinEpisodioValores.PermanecerPausado)
        {
            return;
        }

        if (AccionFinEpisodio == AccionFinEpisodioValores.PausarYSalirFicha)
        {
            SalirDelReproductor("fin del episodio (Pausar y volver a la ficha)");
            return;
        }

        if (AccionFinEpisodio == AccionFinEpisodioValores.AutoPlayInmediato)
        {
            var siguienteInmediato = ObtenerSiguienteEpisodio();
            string msg = siguienteInmediato != null
                ? ConAviso(string.Format(LocalizationService.T("Player_SiguienteEpisodioFormato"), siguienteInmediato.NumeroEpisodio),
                    TextoEpisodiosSaltados(_episodio, siguienteInmediato.NumeroEpisodio))
                : LocalizationService.T("Player_SiguienteEpisodioGenerico");

            AvisarEnReproductor(
                "Auto-Play", msg, "FastForward", "#4CAF50");

            await Task.Delay(1500, ct);
            if (!ct.IsCancellationRequested && !UsuarioVolvioAReproducir())
            {
                IrAEpisodio(ObtenerSiguienteEpisodio(), avisarSalto: false);
            }
            return;
        }

        // AccionFinEpisodioValores.AutoPlayCuentaAtras (default): cuenta atrás de 5s, cancelable.
        var siguiente = ObtenerSiguienteEpisodio();
        TituloSiguienteEnCuentaAtras = siguiente != null
            ? string.Format(LocalizationService.T("Player_SiguienteEpisodioFormato"), siguiente.NumeroEpisodio)
            : LocalizationService.T("Player_SiguienteEpisodioGenerico");
        SegundosCuentaAtrasSiguiente = 5;
        MostrarCuentaAtrasSiguiente = true;

        try
        {
            for (int i = 5; i > 0; i--)
            {
                await Task.Delay(1000, ct);
                if (!MostrarCuentaAtrasSiguiente) return; // el usuario canceló (CancelarAutoPlayCommand)
                if (UsuarioVolvioAReproducir())
                {
                    // Retrocedió para ver otra vez la escena poscréditos (o dio a reproducir): antes la cuenta atrás seguía y a
                    // los pocos segundos lo sacaba al siguiente episodio igualmente.
                    MostrarCuentaAtrasSiguiente = false;
                    return;
                }
                SegundosCuentaAtrasSiguiente = i - 1;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        MostrarCuentaAtrasSiguiente = false;
        IrAEpisodio(ObtenerSiguienteEpisodio(), avisarSalto: false); // la cuenta atrás ya mostraba el aviso de salto
    }

    private async Task RastrearProgresoAsync(CancellationToken ct)
    {
        while (Player != null && !Player.IsDisposed && !ct.IsCancellationRequested)
        {
            try
            {
                // Red de seguridad: si el seek de reanudación nunca llega a aplicarse (archivo
                // que falla al abrir, nunca alcanza Status.Playing, etc.) no dejar el video
                // oculto indefinidamente.
                if (OcultarVideoInicio && DateTime.UtcNow - _ocultarVideoDesdeUtc > MaxOcultarVideoInicio)
                {
                    OcultarVideoInicio = false;
                    _reanudacionPendiente = false;
                }

                if (Player.Status == Status.Playing)
                {
                    if (!_arranqueRegistrado)
                    {
                        _arranqueRegistrado = true;
                        AppLogger.Debug("ReproductorViewModel", $"[Arranque] Primer tick reproduciendo a los {_relojArranque.ElapsedMilliseconds} ms (duración {TimeSpan.FromTicks(Player.Duration).TotalSeconds:F1} s, fotogramas {Player.Video?.FramesDisplayed}).");
                    }
                    double curSeconds = TimeSpan.FromTicks(Player.CurTime).TotalSeconds;
                    double durSeconds = TimeSpan.FromTicks(Player.Duration).TotalSeconds;

                    if (_imagenLista) AplicarSeekDeArranqueDiferidoSiCorresponde(durSeconds);

                    if (!IsDraggingSlider)
                    {
                        ActualizarPosicionYDuracion(curSeconds, durSeconds);
                    }

                    if (PlayPauseIcon != "Pause") PlayPauseIcon = "Pause";

                    // Guardado continuo periódico cada 3 segundos: ya se persiste directo a SQLite
                    // (WAL, durable en cuanto termina el await) — este intervalo solo acota cuánto
                    // progreso se puede perder si la luz se va o Windows se reinicia a la fuerza
                    // entre guardado y guardado.
                    if (Math.Abs(curSeconds - _lastSavedSeconds) >= 3.0)
                    {
                        _lastSavedSeconds = curSeconds;
                        _ = GuardarProgresoActualAsync();
                    }

                    double porcentaje = durSeconds > 0 ? curSeconds / durSeconds : 0;

                    // PERF: cerca del final, pre-abrir (y descartar) el demuxer del siguiente episodio
                    // en un Demuxer aparte del Player en reproducción — adelanta el sondeo de
                    // contenedor/streams y calienta la caché de E/S de Windows para ese archivo, sin
                    // tocar en absoluto la reproducción actual. Como máximo una vez por episodio.
                    if (TieneEpisodioSiguiente && !EsEntornoPruebas())
                    {
                        _episodeNavigator.ConsiderarPrecarga(porcentaje, _siguienteEpisodioCache?.RutaCompleta);
                    }

                    // Auto-Tracking al umbral configurado (FUN-003: antes fijo en 90%). NO se espera: sincroniza con AniList, y con
                    // AniList lento (esperas de 30-60 s en los logs) la barra, el botón de saltar el ending y los subtítulos se
                    // quedaban congelados todo ese rato. La marca local se guarda primero, al instante.
                    if (porcentaje >= UmbralMarcadoVistoActual && !_fueMarcadoComoVisto)
                    {
                        _ = RealizarAutoTrackingAsync();
                    }

                    _finDeEpisodioProcesado = false; // volvió a reproducir: un nuevo final se vuelve a procesar

                    ProcesarDeteccionDeSkip(curSeconds);
                    ActualizarLineasSubtitulosSolapados(curSeconds);
                }
                else if (Player?.Status == Status.Ended && _haCompletadoOpen && !_finDeEpisodioProcesado)
                {
                    _finDeEpisodioProcesado = true;
                    AjustarPosicionAlFinal();
                    await ManejarFinDeEpisodioAsync(ct);
                }

                // Sondeo adaptativo: 250ms mientras reproduce, 1000ms cuando está en pausa/detenido
                // Mientras abre, cada 100 ms: antes el primer tick "reproduciendo" llegaba al segundo de abrir.
                int delayMs = Player?.Status == Status.Playing ? 250 : (_haCompletadoOpen ? 1000 : 100);
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ReproductorViewModel", $"Error en bucle de progreso: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Seek de arranque diferido (reanudación o scrub del usuario durante la apertura). Se aplica
    /// aquí, con el video YA reproduciendo y la duración conocida: aplicar CurTime en
    /// OpenCompleted interrumpe la creación del contexto de video en algunos archivos (HEVC/VFR)
    /// y deja pantalla negra. Solo se consume el diferido del coordinador una vez que se conoce
    /// la duración: si aún no se conoce, se queda pendiente para un tick futuro en vez de perderse.
    /// </summary>
    private void AplicarSeekDeArranqueDiferidoSiCorresponde(double durSeconds)
    {
        double? seekDiferido = durSeconds > 0 ? _seekCoordinator.ConsumirSeekPendienteAlAbrir() : null;
        if (!(durSeconds > 0 && (seekDiferido.HasValue || _posicionInicioSegundos > 5))) return;

        bool esReanudacion = !seekDiferido.HasValue && _posicionInicioSegundos > 5;
        double posToSeek = seekDiferido ?? _posicionInicioSegundos;

        // FUN-006: nunca buscar más allá de la duración real del archivo que se
        // está reproduciendo (un archivo reemplazado por otro más corto haría
        // que el seek quedara fuera de rango y el video terminara al instante).
        if (durSeconds > 0 && posToSeek >= durSeconds)
        {
            posToSeek = Math.Max(0, durSeconds - 1.0);
        }

        _posicionInicioSegundos = 0;
        _reanudacionPendiente = false; // la ventana de "settle" protege ahora la barra hasta que el salto se asiente

        // Antes de disparar el seek nativo: congelar el repintado para que la
        // barra no "rebote" a la posición vieja mientras el seek se procesa.
        _seekCoordinator.IniciarVentanaDeSettle();
        _lastNotifiedSeconds = posToSeek;
        CurrentSeconds = posToSeek;

        AppLogger.Debug("ReproductorViewModel", $"[Arranque] Seek de {(esReanudacion ? "reanudación" : "arranque")} a {posToSeek:F1} s a los {_relojArranque.ElapsedMilliseconds} ms (fotogramas mostrados: {Player?.Video?.FramesDisplayed}).");
        // Reanudación con salto rápido: cae en el fotograma clave anterior (unos segundos antes, que dan contexto) y tarda ~1 s; el preciso
        // tardaba ~2 s con la imagen tapada.
        _seekCoordinator.SolicitarSeek(Player, posToSeek, () => _haCompletadoOpen);

        if (esReanudacion)
        {
            string tiempoFormateado = FormatearTiempo(posToSeek, durSeconds);

            AvisarEnReproductor(
                LocalizationService.T("Player_ReanudarReproduccionTitulo"),
                string.Format(LocalizationService.T("Player_ContinuandoDesdeFormato"), tiempoFormateado),
                "PlaySpeed", "#2196F3");
        }
    }

    /// <summary>Cachea la duración la primera vez, revela el video tras la ventana de settle, y
    /// notifica la posición actual si el cambio es significativo. Solo se llama sin arrastre.</summary>
    private void ActualizarPosicionYDuracion(double curSeconds, double durSeconds)
    {
        // Cachear la duración (no cambia durante la reproducción; independiente del settle)
        if (!_durationCached && durSeconds > 0)
        {
            EstablecerDuracion(durSeconds);
            _durationCached = true;
        }

        // Con el salto al punto guardado aún pendiente, el reproductor va por el 0:00: la barra se queda donde se dejó el episodio.
        if (_reanudacionPendiente) return;

        // Durante la ventana de settle tras un seek, el reproductor aún reporta la
        // posición vieja: no repintar para que la barra no "rebote" hacia atrás.
        bool enSettleSeek = _seekCoordinator.EnVentanaDeSettle;

        // Solo notificar si el cambio es significativo. Con el sondeo cada 250 ms, el umbral viejo de 0,3 s dejaba pasar un tick
        // de cada dos: la barra y el reloj se movían a saltos de medio segundo y el segundo mostrado iba hasta 0,5 s tarde.
        if (!enSettleSeek && Math.Abs(curSeconds - _lastNotifiedSeconds) >= 0.2)
        {
            CurrentSeconds = curSeconds;
            _lastNotifiedSeconds = curSeconds;
            ActualizarTextosTiempo(curSeconds);
        }
    }

    /// <summary>Detección de Skip Intro/Outro con AniSkip: auto-salta o muestra el botón, según
    /// configuración, y limpia el estado del botón cuando no hay ningún skip activo.</summary>
    private void ProcesarDeteccionDeSkip(double curSeconds)
    {
        if (_skipTimes.Count == 0)
        {
            if (MostrarSkipButton)
            {
                MostrarSkipButton = false;
                MostrarSkipIntro = false;
                _currentActiveSkip = null;
            }
            return;
        }

        var skip = _skipCoordinator.ObtenerSkipActivo(curSeconds, _skipTimes, margenFinalSegundos: 0.5);
        if (skip == null)
        {
            if (MostrarSkipButton)
            {
                MostrarSkipButton = false;
                MostrarSkipIntro = false;
                _currentActiveSkip = null;
            }
            return;
        }

        string skipKey = $"{skip.SkipType}_{skip.Interval.StartTime:F1}";
        if (AutoSkipIntroOutro && !_skipAutoEjecutados.Contains(skipKey))
        {
            _skipAutoEjecutados.Add(skipKey);
            // FUN-007: el salto automático se acota al final del video
            // (igual que el manual) para no disparar Ended prematuramente.
            double destino = skip.Interval.EndTime + 0.2;
            if (TotalSeconds > 0 && destino > TotalSeconds) destino = TotalSeconds;
            Saltar(destino, TipoSalto.SaltarTramo);
            MostrarSkipButton = false;
            MostrarSkipIntro = false;
            _currentActiveSkip = null;

            AvisarEnReproductor(
                "AniSkip",
                string.Format(LocalizationService.T("Player_SkipAutoFormato"), skip.TextoBoton),
                skip.IconoBoton, "#2196F3");
        }
        else if (!AutoSkipIntroOutro)
        {
            _currentActiveSkip = skip;
            SkipButtonTexto = $"{skip.TextoBoton} ({NombreTecla("SaltarIntro")})";
            SkipButtonIcon = skip.IconoBoton;
            MostrarSkipButton = true;
            MostrarSkipIntro = true;
        }
    }

    /// <summary>Al terminar un episodio: resetea el progreso a 0, marca como visto si llegó al
    /// final real, y ejecuta la acción de fin de episodio configurada (una sola vez).</summary>
    private async Task ManejarFinDeEpisodioAsync(CancellationToken ct)
    {
        _smtc?.ActualizarEstadoReproduccion(false);

        // Al finalizar, resetear progreso a 0
        _ = GuardarProgresoActualAsync(forzarProgresoCero: true);

        // FUN-012: solo marcar como visto si se alcanzó el final REAL del archivo:
        // un video truncado/corrupto también dispara Ended antes de tiempo.
        bool llegoAlFinalReal = TotalSeconds > 0 && CurrentSeconds >= TotalSeconds * UmbralMarcadoVistoActual;
        if (!_fueMarcadoComoVisto && llegoAlFinalReal)
        {
            _ = RealizarAutoTrackingAsync(); // sin esperar a AniList: la cuenta atrás al siguiente episodio arranca ya
        }

        // Acción configurable al terminar el episodio (Configuración → Reproducción)
        if (!_autoPlayEjecutado)
        {
            _autoPlayEjecutado = true;
            await EjecutarAccionFinEpisodioAsync(ct);
        }
    }

    [RelayCommand]
    public void SkipIntro()
    {
        SkipIntroOutro();
    }

    [RelayCommand]
    public void SkipIntroOutro()
    {
        var skipActivo = _currentActiveSkip ?? _skipCoordinator.ObtenerSkipActivo(CurrentSeconds, _skipTimes);

        if (skipActivo != null)
        {
            double destino = skipActivo.Interval.EndTime + 0.2;
            if (TotalSeconds > 0 && destino > TotalSeconds) destino = TotalSeconds;
            Saltar(destino, TipoSalto.SaltarTramo);
        }

        MostrarSkipButton = false;
        MostrarSkipIntro = false;
        _currentActiveSkip = null;
    }

    [RelayCommand]
    public void Cerrar() => SalirDelReproductor("botón o tecla Cerrar");

    /// <summary>Tecla de cerrar (Esc por defecto): solo sale del reproductor, igual que el botón de retroceder.</summary>
    public void TeclaCerrar() => SalirDelReproductor("tecla Cerrar");

    /// <summary>
    /// Única salida del reproductor (botón de retroceder, ✕, Esc, fin de episodio): guarda, cierra el PiP y vuelve a la vista
    /// anterior. La pantalla completa se conserva (decisión del usuario): solo se sale del reproductor, no del modo de la ventana. Registra el motivo en el log: si alguna vez "vuelve solo a la ficha", el log dirá qué lo pidió.
    /// </summary>
    private void SalirDelReproductor(string motivo)
    {
        AppLogger.Info("ReproductorViewModel", $"Saliendo del reproductor (episodio {_episodio}, {TimeSpan.FromSeconds(CurrentSeconds):mm\\:ss}): {motivo}.");
        try { AppLogger.Debug("ReproductorViewModel", $"[Arranque] Al salir: CurTime={TimeSpan.FromTicks(Player?.CurTime ?? 0).TotalSeconds:F1} s, estado {Player?.Status}, fotogramas {Player?.Video?.FramesDisplayed}, audio {Player?.Audio?.FramesDisplayed}, spec={Player?.VideoDecoder?.CurCodecSpec.Name} hw={Player?.VideoDecoder?.CurCodecSpec.IsHW} accel={Player?.VideoDecoder?.VideoAccelerated} pixfmt={Player?.Video?.PixelFormat} codec={Player?.Video?.Codec}."); } catch { }
        EsModoMini = false;
        _windowModeCoordinator.SalirModoMini();
        _ = GuardarProgresoActualAsync();
        Dispose();

        // Navegar a la vista anterior (detalle del anime), no a la galería
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_VolverDelReproductor());

        // Forzar el foco de vuelta a la ventana principal para que F11 funcione de inmediato
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            () =>
            {
                var window = System.Windows.Application.Current?.MainWindow;
                if (window != null)
                {
                    window.Focus();
                    System.Windows.Input.Keyboard.Focus(window);
                }
            });
    }

    private bool _disposeHecho;

    public void Dispose()
    {
        // Idempotente: Cerrar() dispone y OnVistaActualChanged también dispone al navegar fuera
        if (_disposeHecho) return;
        _disposeHecho = true;
        GC.SuppressFinalize(this);

        CerrarDibujoAss();

        if (_smtc != null)
        {
            _smtc.PlayRequested -= OnSmtcPlayRequested;
            _smtc.PauseRequested -= OnSmtcPauseRequested;
            _smtc.NextRequested -= OnSmtcNextRequested;
            _smtc.PreviousRequested -= OnSmtcPreviousRequested;
            _smtc.Deshabilitar();
        }

        _ = GuardarProgresoActualAsync();

        if (_itemReproduciendose != null)
        {
            _itemReproduciendose.EsReproduciendose = false; // los EpisodioItem son los de la ficha: no dejarlos marcados al salir
            _itemReproduciendose = null;
        }

        _seekCoordinator.Dispose();

        try
        {
            Core.Cancelacion.Detener(ref _subtitleCuesCts);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error al cancelar la extracción de subtítulos: {ex.Message}");
        }

        try
        {
            Core.Cancelacion.Detener(ref _skipCts);
            Core.Cancelacion.Detener(ref _preanalisisCts);
        }
        catch { }

        try
        {
            _trackingCts?.Cancel();
            _trackingCts?.Dispose();
        }
        catch { }
        _trackingCts = null;

        try
        {
            _episodeNavigator.Dispose();
        }
        catch { }

        if (Player != null)
        {
            try
            {
                if (Player.Status == Status.Playing)
                {
                    Player.Pause();
                }
                Player.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Player dispose cleanup: {ex.Message}");
            }
            Player = null!;
        }

        if (_prioridadElevada)
        {
            _prioridadElevada = false;
            PrioridadReproduccion.Restaurar();
        }

        DetenerProteccionPantalla();
    }

    private bool _prioridadElevada;

    private readonly ILogrosService? _logrosService;

    /// <summary>
    /// Los avisos (toast) de la ventana principal quedan TAPADOS por el video: Flyleaf dibuja en su propia
    /// ventana nativa. La vista del reproductor los dibuja por su cuenta, encima del video, a partir de esto.
    /// </summary>
    public IDialogService? DialogService { get; }
}
