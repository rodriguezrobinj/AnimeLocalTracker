using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.EnlacesMusica;
using AnimeLocalTracker.Services.Native;
using AnimeLocalTracker.Services.Python;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// La ficha de un anime. Lleva la cabecera (datos del anime, etiquetas, espacio en disco, cuenta atrás del próximo episodio) y
/// coordina sus tres piezas: <see cref="Episodios"/> (la lista y lo que se hace con cada episodio), <see cref="Musica"/>
/// (openings y endings) y <see cref="Seguimiento"/> (editor de AniList). Se crea una por visita y se descarta al salir.
/// </summary>
public partial class DetalleViewModel : ObservableObject,
    IRecipient<UsuarioLogeadoMensaje>,
    IRecipient<UsuarioDesconectadoMensaje>,
    IDisposable,
    IVistaReutilizable
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Cancela las cargas de fondo de ESTA ficha (espacio en disco, próximos episodios, próxima
    /// emisión, datos extra, preferencias de emisión, enriquecimiento de episodios): si el usuario
    /// navega a otro anime antes de que terminen, no tiene sentido seguir golpeando disco/red por
    /// uno que ya no está en pantalla. Se crea uno nuevo en cada InicializarAsync (también en el
    /// refresco desde AniList) y el anterior se cancela.
    /// </summary>
    private CancellationTokenSource? _ctsCargaFicha;

    private bool _liberado;

    /// <summary>La ficha ya se descartó (se salió de ella): no atiende más mensajes ni cargas.</summary>
    internal bool EstaLiberado => _liberado;

    // Lo llama NavigationService al salir de la ficha hacia cualquier otro sitio. Suelta todo lo que la mantendría viva y
    // trabajando sin estar en pantalla: antes cada ficha abierta se quedaba en memoria hasta cerrar la app, y las viejas
    // seguían atendiendo los avisos de descargas y de configuración (una miniatura y un aviso por cada vez que se abrió).
    public void Dispose()
    {
        if (_liberado) return;
        _liberado = true;

        WeakReferenceMessenger.Default.UnregisterAll(this);

        DetenerContador();
        _ctsCargaFicha?.Cancel();
        _ctsCargaFicha?.Dispose();
        Episodios.Dispose();
        // Con "seguir sonando fuera de la ficha" y un tema sonando, la música no se libera: se queda de fondo.
        if (_musicaDeFondo?.Conservar(Musica) != true) Musica.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    private AnimeItem? _animeSeleccionado;

    /// <summary>La lista de episodios: filtro, orden, marcar, descargar, reproducir y las herramientas sobre los archivos.</summary>
    public EpisodiosFichaViewModel Episodios { get; }

    /// <summary>Openings y endings del anime: lista, descargas y reproducción (su ventana es <c>PanelMusicaView</c>).</summary>
    public MusicaFichaViewModel Musica { get; private set; }

    private readonly IMusicaDeFondoService? _musicaDeFondo;

    /// <summary>
    /// Si la música de este anime seguía sonando de fondo, esta ficha se la queda tal como iba (lista, tema, posición) en vez
    /// de cargar la suya desde cero.
    /// </summary>
    private bool RecuperarMusicaDeFondo(AnimeItem anime)
    {
        var deFondo = _musicaDeFondo?.Recuperar(anime.AniListId);
        if (deFondo == null) return false;

        var propia = Musica;
        deFondo.UsarEpisodioMasAltoVisto(Episodios.EpisodioMasAltoVisto);
        Musica = deFondo;
        if (!ReferenceEquals(propia, deFondo)) propia.Dispose();
        OnPropertyChanged(nameof(Musica));
        return true;
    }

    /// <summary>Editor de seguimiento de AniList (su ventana es <c>EditorSeguimientoView</c>).</summary>
    public SeguimientoEditorViewModel Seguimiento { get; }

    partial void OnAnimeSeleccionadoChanged(AnimeItem? value)
    {
        Episodios.Anime = value;
        Musica.Anime = value;
        Seguimiento.Anime = value;
    }

    // === ACCIONES HERO Y DETALLES ===
    [ObservableProperty] private bool _sinopsisExpandida = false;
    [ObservableProperty] private bool _esFavoritoAnime = false;

    /// <summary>Interruptor "Conservar los videos" del anime abierto (persiste en AnimeItem.ConservarVideos).</summary>
    [ObservableProperty] private bool _conservarVideosAnime;

    [ObservableProperty] private bool _estaConectado;

    public DetalleViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IFileScannerService fileScannerService,
        IDialogService dialogService,
        IDownloadService downloadService,
        PythonEpisodeEnricher? enricher = null,
        IPluginService? pluginService = null,
        IVideoIntegrityService? videoIntegrityService = null,
        IProximaEmisionService? proximaEmision = null,
        IDatosExtraService? datosExtra = null,
        IEmisionMonitorService? monitorEmision = null,
        IAnimeThemesService? animeThemesService = null,
        IAnimeThemesDownloadService? animeThemesDownload = null,
        IAudioTrackPlayer? audioTrackPlayer = null,
        IAudioDurationService? audioDuration = null,
        INyaaSourceService? nyaaSourceService = null,
        ISelectorTorrentService? selectorTorrentService = null,
        ISettingsService? settingsService = null,
        IEnlacesMusicaService? enlacesMusica = null,
        IEstadoConexionService? estadoConexion = null,
        IMusicaDeFondoService? musicaDeFondo = null)
    {
        _musicaDeFondo = musicaDeFondo;
        _proximaEmision = proximaEmision;
        _datosExtra = datosExtra;
        _monitorEmision = monitorEmision;
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        Episodios = new EpisodiosFichaViewModel(databaseService, fileScannerService, dialogService, downloadService, enricher,
            videoIntegrityService, nyaaSourceService, selectorTorrentService, settingsService);
        Seguimiento = new SeguimientoEditorViewModel(animeTrackingService, databaseService, authService, dialogService,
            () => Episodios.Todos.Count > 0 ? Episodios.Todos.Count : Episodios.EpisodiosDelAnime.Count);
        Musica = new MusicaFichaViewModel(dialogService, animeThemesService, animeThemesDownload, audioTrackPlayer, audioDuration,
            enlacesMusica, settingsService, Episodios.EpisodioMasAltoVisto, estadoConexion, musicaDeFondo);

        // Lo que la lista de episodios no sabe hacer por sí misma: el espacio en disco es de la cabecera, y la música no debe
        // sonar bajo el video.
        Episodios.ArchivosCambiados += () => _ = CalcularEspacioEnDiscoAsync();
        Episodios.ReproduccionSolicitada += () => Musica.DetenerMusica();

        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        EstaConectado = _authService.EstaAutenticado();
    }

    public void Receive(UsuarioLogeadoMensaje message) => EstaConectado = true;
    public void Receive(UsuarioDesconectadoMensaje message) => EstaConectado = false;

    public async Task InicializarAsync(AnimeItem anime)
    {
        ReiniciarContadorProximo();
        ReiniciarExtras();
        bool musicaRecuperada = RecuperarMusicaDeFondo(anime);
        if (!musicaRecuperada) Musica.ReiniciarMusica();
        EstaConectado = _authService.EstaAutenticado();
        AnimeSeleccionado = anime;
        EsFavoritoAnime = anime.EsFavorito;
        // Manda la base de datos, no la copia con la que llega el anime (la Galería pudo leerlo antes de activar la protección).
        anime.ConservarVideos = await _databaseService.ObtenerConservarVideosAsync(anime.AniListId);
        ConservarVideosAnime = anime.ConservarVideos;
        var reloj = Stopwatch.StartNew();

        // 1. La lista de episodios: base de datos y disco, sin internet. Se lee fuera del hilo de la interfaz y se muestra aquí,
        //    en el mismo paso en que se arranca lo demás: un segundo salto de hilo esperaría a que se pintaran las filas.
        var lectura = await Episodios.LeerAsync(anime, reloj);

        // Se salió de la ficha mientras leía la base de datos y el disco: no se arranca ninguna carga de fondo para ella.
        if (_liberado) return;

        long msEnInterfaz = reloj.ElapsedMilliseconds;
        Episodios.Mostrar(anime, lectura);
        RegistrarTiempoDeApertura(anime.AniListId, lectura.Filas.Count, lectura.EnDisco, lectura.MsDatos, msEnInterfaz, reloj);

        // Navegación rápida entre fichas (flechas, clics seguidos): se cancelan las cargas de
        // fondo de la ficha anterior en vez de dejarlas terminar para un anime que ya no se ve.
        Core.Cancelacion.Reemplazar(ref _ctsCargaFicha);
        var ctFicha = _ctsCargaFicha.Token;

        // 2. Lo demás, en segundo plano: miniaturas y datos técnicos de los archivos, cuenta atrás, espacio en disco,
        //    etiquetas, avisos y música.
        _ = Episodios.EnriquecerEnSegundoPlanoAsync(ctFicha);
        _ = CargarProximaEmisionAsync(ctFicha);
        _ = CalcularEspacioEnDiscoAsync(ctFicha);
        _ = CargarDatosExtraAsync(ctFicha);
        _ = CargarPreferenciasEmisionAsync(ctFicha);
        if (!musicaRecuperada)
        {
            Musica.CargarEnlacesMusica(anime);
            Musica.IniciarCargaDeTemas(ctFicha);
        }
    }

    /// <summary>
    /// Deja en el registro cuánto tardó la ficha en abrirse (igual que el [Perf] de la Galería): cuándo estaban los datos
    /// (base de datos + carpeta + filas, fuera del hilo de la interfaz), cuándo pudo la interfaz ponerlos en la lista (antes
    /// está ocupada construyendo la vista), cuándo se pintó y cuándo dejó de moverse todo. Sin esto no hay forma de saber si
    /// un cambio la acelera o la frena.
    /// </summary>
    private static void RegistrarTiempoDeApertura(int aniListId, int filas, int enDisco, long msDatos, long msEnInterfaz, Stopwatch reloj)
    {
        string texto = $"[Perf] Ficha {aniListId}: {filas} episodios ({enDisco} en disco); datos listos a los {msDatos} ms, " +
                       $"en la lista a los {reloj.ElapsedMilliseconds} ms (la interfaz quedó libre a los {msEnInterfaz})";

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            AppLogger.Info("DetalleViewModel", texto + ".");
            return;
        }

        // Loaded llega tras el primer dibujado con las filas; ContextIdle, cuando ya no queda nada pendiente (etiquetas,
        // música, espacio en disco y demás cargas de fondo ya colocadas).
        long msPintada = 0;
        _ = dispatcher.InvokeAsync(() => msPintada = reloj.ElapsedMilliseconds, System.Windows.Threading.DispatcherPriority.Loaded);
        _ = dispatcher.InvokeAsync(
            () => AppLogger.Info("DetalleViewModel", $"{texto}; pintada a los {msPintada} ms, asentada a los {reloj.ElapsedMilliseconds} ms."),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Abre la CARPETA DEL ANIME en el Explorador (acción única de nivel anime,
    /// no por episodio — evita saturar la lista de episodios).
    /// </summary>
    [RelayCommand]
    private void AbrirCarpetaAnime()
    {
        if (AnimeSeleccionado == null || string.IsNullOrWhiteSpace(AnimeSeleccionado.RutaCarpeta))
        {
            _ = _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_CarpetaNoEncontradaTitulo"), LocalizationService.T("Det_SinCarpetaLocalMsj"), false, "AlertCircleOutline", "#F59E0B");
            return;
        }
        if (!Directory.Exists(AnimeSeleccionado.RutaCarpeta))
        {
            _ = _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_CarpetaNoEncontradaTitulo"), LocalizationService.T("Det_CarpetaYaNoExisteMsj"), false, "AlertCircleOutline", "#F59E0B");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AnimeSeleccionado.RutaCarpeta}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"Error abriendo carpeta del anime: {ex.Message}");
        }
    }

    [RelayCommand]
    private void VolverAGaleria()
    {
        Pestanas.Galeria.Abrir();
    }
    
    [RelayCommand]
    private async Task EliminarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;

        if (await EliminacionAnime.ConfirmarYEliminarAsync(AnimeSeleccionado, _dialogService, _databaseService))
            VolverAGaleria();
    }
    
    [RelayCommand]
    private void AlternarSinopsis()
    {
        SinopsisExpandida = !SinopsisExpandida;
    }

    /// <summary>Favorito individual por anime (no un estado/categoría): persiste en AnimeItem.EsFavorito.</summary>
    [RelayCommand]
    private async Task AlternarFavoritoAnimeAsync()
    {
        if (AnimeSeleccionado == null) return;

        AnimeSeleccionado.EsFavorito = !AnimeSeleccionado.EsFavorito;
        EsFavoritoAnime = AnimeSeleccionado.EsFavorito;
        await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
    }

    /// <summary>
    /// "Conservar los videos": con la protección activa, "Eliminar el video tras verlo" no borra nada de este anime. Si el guardado
    /// falla el interruptor vuelve a como estaba y se avisa: mostrar "protegido" sin estarlo llevaría a perder videos.
    /// </summary>
    [RelayCommand]
    private async Task AlternarConservarVideosAsync()
    {
        // El anime sobre el que se pulsó: si el guardado falla, AnimeSeleccionado puede ser ya otro.
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        bool nuevo = !ConservarVideosAnime;
        anime.ConservarVideos = nuevo;
        ConservarVideosAnime = nuevo;
        try
        {
            await _databaseService.GuardarConservarVideosAsync(anime.AniListId, nuevo);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DetalleViewModel", $"No se pudo guardar 'Conservar los videos' de {anime.Titulo}: {ex.Message}");
            anime.ConservarVideos = !nuevo;
            if (ReferenceEquals(AnimeSeleccionado, anime)) ConservarVideosAnime = !nuevo;
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ConservarVideos"), LocalizationService.T("Det_ConservarVideosErrorMsj"), false, "AlertCircleOutline", "#F59E0B");
        }
    }

    [RelayCommand]
    private void AbrirWebView()
    {
        if (AnimeSeleccionado == null) return;
        string url = $"https://anilist.co/anime/{AnimeSeleccionado.AniListId}";
        try
        {
            Core.Shell.Abrir(url);
        }
        catch (Exception ex)
        {
            AppLogger.Error("DetalleViewModel", "Error abriendo WebView de AniList", ex);
        }
    }

    [RelayCommand]
    private async Task ActualizarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(AnimeSeleccionado.AniListId);
        if (datosFrescos != null)
        {
            int episodiosEmitidos = datosFrescos.EpisodiosEmitidos(AnimeSeleccionado.TotalEpisodios);

            var titulosAlt = new List<string>();
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.English)) titulosAlt.Add(datosFrescos.Title.English);
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.UserPreferred) && datosFrescos.Title.UserPreferred != datosFrescos.Title.Romaji) titulosAlt.Add(datosFrescos.Title.UserPreferred);
            // El título nativo (japonés) es clave para el catálogo del sitio (aka ja-jp)
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.Native)) titulosAlt.Add(datosFrescos.Title.Native!);
            if (datosFrescos.Synonyms != null) titulosAlt.AddRange(datosFrescos.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
            AnimeSeleccionado.NombresAlternativos = string.Join(" | ", titulosAlt.Distinct());

            AnimeSeleccionado.TotalEpisodios = episodiosEmitidos;
            AnimeSeleccionado.Estado = datosFrescos.Status ?? "UNKNOWN";
            AnimeSeleccionado.Generos = datosFrescos.Genres != null ? string.Join(", ", datosFrescos.Genres) : "";
            AnimeSeleccionado.UrlPortada = datosFrescos.CoverImage?.ExtraLarge ?? datosFrescos.CoverImage?.Large ?? AnimeSeleccionado.UrlPortada;
            
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            
            _forzarProximaEmision = true; // el botón "Actualizar" también revalida la hora del próximo episodio
            await InicializarAsync(AnimeSeleccionado);
            
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_ActualizadoTitulo"), string.Format(LocalizationService.T("Det_ActualizadoMsj"), episodiosEmitidos), false, "CheckCircleOutline", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Dlg_ErrorTitulo"), LocalizationService.T("Det_ErrorConectarAniListMsj"), false, "AlertCircleOutline", "#E53935");
        }
    }
    
}
