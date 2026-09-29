using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.EnlacesMusica;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Openings/endings del anime (AnimeThemes.moe): catálogo, vista previa antes de guardar, descarga+conversión a mp3 y
/// reproducción con barra de progreso. Los mp3 ya descargados también alimentan la detección de saltos de OP/ED por
/// audio de referencia en SkipTimesCoordinator — descargar aquí "activa" ese salto más preciso.
/// La vista previa es la canción completa en una caché temporal: "Guardar" la mueve a la carpeta de música sin bajarla otra vez.
/// </summary>
public partial class DetalleViewModel
{
    /// <summary>El volumen elegido dura toda la sesión (no se vuelve a poner al abrir otra ficha).</summary>
    private static double _volumenMusicaSesion = 0.8;

    private readonly IAnimeThemesService? _animeThemesService;
    private readonly IAnimeThemesDownloadService? _animeThemesDownload;
    private readonly IAudioTrackPlayer? _audioTrackPlayerInyectado;
    private readonly IAudioDurationService? _audioDuration;
    private readonly IEnlacesMusicaService? _enlacesMusica;
    private ControlReproduccionTemas? _controlMusica;
    private CancellationTokenSource _ctsMusica = new();

    /// <summary>
    /// Cada carga de la lista lleva un número; solo la más reciente la rellena. Al abrir la misma ficha dos veces seguidas, dos
    /// cargas podían solaparse (ahora esperan a la comprobación de archivos en segundo plano) y dejar cada tema repetido.
    /// </summary>
    private int _versionCargaTemas;
    private readonly object _candadoTemas = new();
    private CancellationTokenSource? _ctsGuardadoAjustesMusica;
    private bool _ajustesMusicaPendientes;
    private bool _aplicandoAjustesMusicaGuardados;

    /// <summary>Espera tras el último cambio de volumen antes de escribir settings.json (cada guardado reescribe el archivo entero).
    /// Ajustable solo en pruebas.</summary>
    internal TimeSpan RetrasoGuardadoAjustesMusica { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>La carga de la lista de temas lanzada al abrir la ficha (se espera para abrir el panel desde otra pestaña).</summary>
    internal Task CargaTemasMusicalesTarea { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Desde "Reproducir" en el historial de Descargas: cuando la lista de temas está lista, abre el panel de música y
    /// reproduce ese tema (tipo|slug|versión). Si ya no está descargado, solo abre el panel.
    /// </summary>
    public async Task AbrirMusicaYReproducirAsync(string temaClave)
    {
        try { await CargaTemasMusicalesTarea; }
        catch (Exception ex) { AppLogger.Debug("DetalleViewModel", $"La lista de temas no cargó: {ex.Message}"); }

        var tema = TemasMusicales.FirstOrDefault(t => t.Info.ClaveEstable() == temaClave);
        if (tema == null) return;

        FiltroTemasMusicales = "Todos";
        MostrandoPanelMusica = true;
        if (tema.PuedeReproducir) ReproducirTema(tema);
    }

    /// <summary>Última lectura de duraciones en curso (para que las pruebas puedan esperarla).</summary>
    internal Task CargaDuracionesTarea { get; private set; } = Task.CompletedTask;

    public ObservableCollection<TemaAnimeItem> TemasMusicales { get; } = [];

    /// <summary>Lo que realmente ve la lista: todos los temas, o solo openings/endings según <see cref="FiltroTemasMusicales"/>.
    /// Un anime con muchos temas (One Piece: 34 openings + 39 endings) es difícil de recorrer en una sola lista larga.</summary>
    public ObservableCollection<TemaAnimeItem> TemasMusicalesVisibles { get; } = [];

    [ObservableProperty] private bool _cargandoTemasMusicales;
    [ObservableProperty] private bool _mostrandoPanelMusica;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsFiltroTemasTodos))]
    [NotifyPropertyChangedFor(nameof(EsFiltroTemasOpenings))]
    [NotifyPropertyChangedFor(nameof(EsFiltroTemasEndings))]
    private string _filtroTemasMusicales = "Todos";

    public bool EsFiltroTemasTodos => FiltroTemasMusicales == "Todos";
    public bool EsFiltroTemasOpenings => FiltroTemasMusicales == "Openings";
    public bool EsFiltroTemasEndings => FiltroTemasMusicales == "Endings";

    /// <summary>Los chips de filtro solo aportan si hay de los dos tipos: con uno solo no hay nada que distinguir.</summary>
    public bool MostrarFiltroTemasMusicales => TemasMusicales.Any(t => t.Tipo == "OP") && TemasMusicales.Any(t => t.Tipo == "ED");

    /// <summary>Volumen de la música de la ficha, de 0 a 1.</summary>
    [ObservableProperty] private double _volumenMusica = _volumenMusicaSesion;

    /// <summary>Al terminar un tema, pasa solo al siguiente que se pueda escuchar (guardado o con vista previa).</summary>
    [ObservableProperty] private bool _reproduccionContinuaMusica = true;

    public bool TieneTemasMusicales => TemasMusicales.Count > 0;

    /// <summary>Hay un tema sonando ahora mismo: el botón "Música" de la ficha lo indica aunque el panel esté cerrado.</summary>
    [ObservableProperty] private bool _musicaSonando;

    // === "Descargar todos" ===

    /// <summary>Descargas a la vez en "Descargar todos": la conversión a mp3 usa bastante CPU y AnimeThemes es un proyecto de
    /// voluntarios; con 2 se aprovecha la conexión sin saturar ni una cosa ni la otra.</summary>
    internal const int DescargasSimultaneasTodos = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarProgresoDescargaTodos))]
    private bool _descargandoTodos;

    /// <summary>"12 / 34" mientras avanza "Descargar todos".</summary>
    [ObservableProperty] private string _progresoDescargaTodosTexto = string.Empty;

    private int _aniListIdDescargaTodos;
    private CancellationTokenSource? _ctsDescargaTodos;
    private readonly HashSet<string> _pendientesDescargaTodos = [];

    /// <summary>El progreso (y el botón de cancelar) solo se ve en la ficha del anime que se está descargando.</summary>
    public bool MostrarProgresoDescargaTodos => DescargandoTodos && AnimeSeleccionado?.AniListId == _aniListIdDescargaTodos;

    /// <summary>Solo para pruebas: sustituye al Explorador de Windows al abrir la carpeta de música.</summary>
    internal Action<string>? AbrirCarpetaEnExplorador { get; set; }

    /// <summary>Enlaces externos del anime (p. ej. AniPlaylist con Spotify, Apple Music y Deezer). Salen de los proveedores registrados.</summary>
    public ObservableCollection<EnlaceMusicaItem> EnlacesMusica { get; } = [];

    public bool TieneEnlacesMusica => EnlacesMusica.Count > 0;

    private void CargarEnlacesMusica(AnimeItem anime)
    {
        EnlacesMusica.Clear();
        if (_enlacesMusica != null)
        {
            foreach (var enlace in _enlacesMusica.ObtenerEnlaces(anime))
                EnlacesMusica.Add(new EnlaceMusicaItem(enlace));
        }
        OnPropertyChanged(nameof(TieneEnlacesMusica));
    }

    [RelayCommand]
    private void AbrirEnlaceMusica(EnlaceMusicaItem? item)
    {
        if (item == null || _enlacesMusica == null) return;

        if (!_enlacesMusica.Abrir(item.Enlace))
        {
            _dialogService.MostrarToast(
                LocalizationService.T("Det_MusicaEnlaceErrorTitulo"),
                string.Format(LocalizationService.T("Det_MusicaEnlaceErrorMsjFormato"), item.Nombre),
                "AlertCircleOutline", "#EF4444");
        }
    }

    /// <summary>Controlador de reproducción (se crea al primer uso: abrir una ficha no debe crear un reproductor).</summary>
    internal ControlReproduccionTemas ControlMusica => _controlMusica ??= CrearControlMusica();

    private ControlReproduccionTemas CrearControlMusica()
    {
        var control = new ControlReproduccionTemas(_audioTrackPlayerInyectado ?? new AudioTrackPlayer()) { Volumen = VolumenMusica };
        control.FalloReproduccion += (_, tema) => _dialogService.MostrarToast(
            LocalizationService.T("Det_MusicaErrorReproducirTitulo"),
            string.Format(LocalizationService.T("Det_MusicaErrorReproducirMsjFormato"), tema.TituloCancion),
            "AlertCircleOutline", "#EF4444");
        control.PistaTerminada += (_, tema) => AlTerminarPistaMusica(tema);
        control.EstadoCambiado += (_, _) => MusicaSonando = control.Sonando;
        return control;
    }

    partial void OnVolumenMusicaChanged(double value)
    {
        _volumenMusicaSesion = Math.Clamp(value, 0, 1);
        if (_controlMusica != null) _controlMusica.Volumen = value;
        if (!_aplicandoAjustesMusicaGuardados) ProgramarGuardadoAjustesMusica(conRetraso: true);
    }

    partial void OnReproduccionContinuaMusicaChanged(bool value)
    {
        if (!_aplicandoAjustesMusicaGuardados) ProgramarGuardadoAjustesMusica(conRetraso: false);
    }

    /// <summary>Lee los ajustes guardados de la música (volumen y reproducción continua). Sin servicio de ajustes se usan los de la sesión.</summary>
    private void CargarAjustesMusica()
    {
        var config = _settingsService?.ObtenerConfiguracion();
        if (config == null) return;

        // Aplicar lo guardado no es un cambio del usuario: no debe volver a escribir el archivo.
        _aplicandoAjustesMusicaGuardados = true;
        try
        {
            VolumenMusica = double.IsFinite(config.VolumenMusica) ? Math.Clamp(config.VolumenMusica, 0, 1) : VolumenMusica;
            ReproduccionContinuaMusica = config.ReproduccionContinuaMusica;
        }
        finally
        {
            _aplicandoAjustesMusicaGuardados = false;
        }
    }

    /// <summary>
    /// Guarda el volumen y la reproducción continua en settings.json. El volumen se guarda al dejar de mover el slider
    /// (no en cada paso: cada guardado reescribe todo el archivo y avisa a quien escucha los cambios de configuración).
    /// </summary>
    private void ProgramarGuardadoAjustesMusica(bool conRetraso)
    {
        if (_settingsService == null) return;

        _ajustesMusicaPendientes = true;
        _ctsGuardadoAjustesMusica?.Cancel();
        _ctsGuardadoAjustesMusica?.Dispose();
        _ctsGuardadoAjustesMusica = new CancellationTokenSource();

        _ = GuardarAjustesMusicaTrasRetrasoAsync(conRetraso ? RetrasoGuardadoAjustesMusica : TimeSpan.Zero, _ctsGuardadoAjustesMusica.Token);
    }

    private async Task GuardarAjustesMusicaTrasRetrasoAsync(TimeSpan retraso, CancellationToken ct)
    {
        try
        {
            if (retraso > TimeSpan.Zero) await Task.Delay(retraso, ct);
            await GuardarAjustesMusicaAsync();
        }
        catch (OperationCanceledException)
        {
            // Llegó otro cambio: el guardado pendiente lo hará ese.
        }
    }

    /// <summary>Escribe ya lo pendiente (si hay algo). También al salir de la ficha, para no perder un cambio reciente.</summary>
    internal async Task GuardarAjustesMusicaAsync()
    {
        var servicio = _settingsService;
        if (servicio == null || !_ajustesMusicaPendientes) return;
        _ajustesMusicaPendientes = false;

        try
        {
            var config = servicio.ObtenerConfiguracion();
            if (config == null) return;

            config.VolumenMusica = Math.Clamp(VolumenMusica, 0, 1);
            config.ReproduccionContinuaMusica = ReproduccionContinuaMusica;
            await servicio.GuardarConfiguracionAsync(config);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron guardar los ajustes de música: {ex.Message}");
        }
    }

    private void GuardarAjustesMusicaPendientesYa()
    {
        _ctsGuardadoAjustesMusica?.Cancel();
        _ = GuardarAjustesMusicaAsync();
    }

    [RelayCommand]
    private void ToggleMusica() => MostrandoPanelMusica = !MostrandoPanelMusica;

    [RelayCommand]
    private void AlternarReproduccionContinuaMusica() => ReproduccionContinuaMusica = !ReproduccionContinuaMusica;

    [RelayCommand]
    private void CambiarFiltroTemas(string filtro) => FiltroTemasMusicales = filtro;

    partial void OnFiltroTemasMusicalesChanged(string value) => AplicarFiltroTemas();

    /// <summary>Reconstruye <see cref="TemasMusicalesVisibles"/> a partir de <see cref="TemasMusicales"/> y el filtro actual.
    /// La reproducción (continua, "siguiente que se pueda escuchar"…) sigue recorriendo TODOS los temas, filtrados o no:
    /// el filtro es solo para mirar la lista, nunca cambia qué suena.</summary>
    private void AplicarFiltroTemas()
    {
        TemasMusicalesVisibles.Clear();
        foreach (var tema in TemasMusicales)
        {
            bool visible = FiltroTemasMusicales switch
            {
                "Openings" => tema.Tipo == "OP",
                "Endings" => tema.Tipo == "ED",
                _ => true
            };
            if (visible) TemasMusicalesVisibles.Add(tema);
        }
    }

    internal async Task CargarTemasMusicalesAsync(CancellationToken cancellationToken = default)
    {
        var servicio = _animeThemesService;
        var descargas = _animeThemesDownload;
        var anime = AnimeSeleccionado;
        if (servicio == null || descargas == null || anime == null || cancellationToken.IsCancellationRequested) return;

        int version = Interlocked.Increment(ref _versionCargaTemas);
        bool EsLaUltima() => Volatile.Read(ref _versionCargaTemas) == version;

        CargandoTemasMusicales = true;
        try
        {
            var temas = await servicio.ObtenerTemasAsync(anime.AniListId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado)) return;

            // Todo lo que toca disco va fuera del hilo de la interfaz (One Piece: 73 temas = 146 comprobaciones de archivo).
            // Antes de mirar qué está descargado se ponen al día los nombres de los mp3 cuyo rango cambió en AnimeThemes.
            int aniListId = anime.AniListId;
            var catalogo = temas;
            var estados = await Task.Run(() =>
            {
                // Sin lista de AnimeThemes (sin conexión y nunca guardada): al menos los mp3 ya descargados se pueden escuchar.
                if (temas.Count == 0) temas = TemasDesdeDescargasLocales(descargas, aniListId);
                else descargas.ReconciliarDescargasLocales(aniListId, temas);

                return temas.Select(tema =>
                {
                    bool descargado = descargas.EstaDescargado(aniListId, tema);
                    bool descargando = !descargado && descargas.EstaDescargando(aniListId, tema);
                    // Una vista previa ya preparada (p. ej. al volver a la ficha) se puede escuchar sin bajarla otra vez.
                    bool vistaPrevia = !descargado && descargas.ObtenerRutaVistaPrevia(aniListId, tema) != null;
                    return (Tema: tema, Descargado: descargado, Descargando: descargando, VistaPrevia: vistaPrevia);
                }).ToList();
            }, cancellationToken);

            // La lista se rellena de una vez y solo desde la carga más reciente (en la app siempre en el hilo de la interfaz;
            // el candado cubre a quien la llame desde otro hilo, como las pruebas).
            lock (_candadoTemas)
            {
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado) || !EsLaUltima()) return;

                int episodioMasAltoVisto = EpisodioMasAltoVisto();
                TemasMusicales.Clear();
                foreach (var estado in estados)
                {
                    var item = new TemaAnimeItem
                    {
                        Info = estado.Tema,
                        Descargado = estado.Descargado,
                        VistaPreviaLista = estado.VistaPrevia,
                        OcultoPorSpoiler = DebeOcultarSpoiler(estado.Tema, anime, episodioMasAltoVisto),
                        EnCola = !estado.Descargado && EstaEnColaDescargaTodos(aniListId, estado.Tema)
                    };
                    TemasMusicales.Add(item);

                    // La descarga empezó antes de salir de la ficha y sigue en marcha: la fila nueva vuelve a mostrar su avance.
                    if (estado.Descargando) _ = EjecutarDescargaTemaAsync(item, aniListId, avisarSiFalla: false, CancellationToken.None);
                }
                OnPropertyChanged(nameof(TieneTemasMusicales));
                OnPropertyChanged(nameof(MostrarFiltroTemasMusicales));
                OnPropertyChanged(nameof(MostrarProgresoDescargaTodos));
                AplicarFiltroTemas();
                CargaDuracionesTarea = LeerDuracionesAsync(TemasMusicales.ToList(), anime.AniListId, _ctsMusica.Token);
            }

            // Los mp3 descargados antes de que la app los etiquetara reciben título, artista y portada, en segundo plano.
            if (catalogo.Count > 0) _ = EtiquetarMusicaEnSegundoPlanoAsync(descargas, aniListId, catalogo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Se cambió de ficha mientras se comprobaban los archivos: normal.
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron cargar los openings/endings: {ex.Message}");
        }
        finally
        {
            if (EsLaUltima()) CargandoTemasMusicales = false;
        }
    }

    /// <summary>
    /// Los mp3 guardados del anime, reconstruidos desde el nombre de cada archivo (tipo, slug, versión, rango). Sin título ni
    /// artista (eso solo lo da AnimeThemes) y sin enlace de audio: se pueden escuchar y borrar, no volver a bajar.
    /// </summary>
    private static List<AnimeThemeInfo> TemasDesdeDescargasLocales(IAnimeThemesDownloadService descargas, int aniListId) =>
        (descargas.ListarDescargasLocales(aniListId) ?? [])
            .OrderBy(t => t.Tipo == "OP" ? 0 : 1)
            .ThenBy(t => t.Slug, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Version)
            .Select(t => new AnimeThemeInfo
            {
                Slug = t.Slug,
                Tipo = t.Tipo,
                Version = t.Version,
                RangoEpisodios = t.RangoEpisodios,
                AudioUrlOgg = string.Empty
            })
            .ToList();

    private static async Task EtiquetarMusicaEnSegundoPlanoAsync(IAnimeThemesDownloadService descargas, int aniListId, IReadOnlyList<AnimeThemeInfo> catalogo)
    {
        try
        {
            int etiquetados = await Task.Run(() => descargas.EtiquetarDescargasLocalesAsync(aniListId, catalogo));
            if (etiquetados > 0) AppLogger.Info("DetalleViewModel", $"{etiquetados} mp3 de AniListId {aniListId} con etiquetas y portada.");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron etiquetar los mp3 de {aniListId}: {ex.Message}");
        }
    }

    /// <summary>"Escuchar antes de descargar": prepara la canción completa en la caché temporal y la reproduce.</summary>
    [RelayCommand]
    private async Task PrevisualizarTemaAsync(TemaAnimeItem? tema)
    {
        var servicio = _animeThemesDownload;
        var anime = AnimeSeleccionado;
        if (tema == null || servicio == null || anime == null) return;
        if (tema.Descargado || tema.Descargando || tema.PreparandoVistaPrevia || !tema.TieneAudioEnLinea) return;

        if (tema.VistaPreviaLista)
        {
            ReproducirTema(tema);
            return;
        }

        var ct = _ctsMusica.Token;
        tema.ProgresoPreparacion = 0;
        tema.PreparandoVistaPrevia = true;
        try
        {
            var progreso = new Progress<double>(p => tema.ProgresoPreparacion = p);
            string? ruta = await servicio.PrepararVistaPreviaAsync(anime.AniListId, tema.Info, progreso, ct);

            // Si se cambió de ficha (o se cerró) mientras se preparaba, no se arranca sonido de un anime que ya no está en pantalla.
            if (ct.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado)) return;

            tema.VistaPreviaLista = ruta != null;
            if (ruta == null)
            {
                _dialogService.MostrarToast(
                    LocalizationService.T("Det_MusicaErrorPrevistaTitulo"),
                    string.Format(LocalizationService.T("Det_MusicaErrorPrevistaMsjFormato"), tema.TituloCancion),
                    "AlertCircleOutline", "#EF4444");
                return;
            }

            _ = LeerDuracionAsync(tema, anime.AniListId, ct);
            ControlMusica.Alternar(tema, ruta);
        }
        finally
        {
            tema.PreparandoVistaPrevia = false;
        }
    }

    /// <summary>
    /// Descarga el mp3 a la carpeta de música. Si ya se había escuchado en vista previa, solo mueve ese archivo
    /// (instantáneo, sin bajar nada de nuevo, y sin cortar lo que esté sonando).
    /// </summary>
    [RelayCommand]
    private async Task DescargarTemaAsync(TemaAnimeItem? tema)
    {
        if (tema == null || tema.Descargando || tema.PreparandoVistaPrevia || !tema.TieneAudioEnLinea || _animeThemesDownload == null) return;
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        if (tema.VistaPreviaLista && GuardarVistaPrevia(tema, anime.AniListId)) return;

        await EjecutarDescargaTemaAsync(tema, anime.AniListId, avisarSiFalla: true);
    }

    /// <summary>
    /// Descarga (o se une a la descarga ya en marcha de) un tema y refleja su avance en la fila. La descarga no se cancela al
    /// salir de la ficha. <paramref name="avisarSiFalla"/> es false al volver a engancharse a una descarga ya empezada: quien
    /// la empezó ya avisa si falla, y no deben salir dos avisos.
    /// </summary>
    /// <returns>true = descargado, false = falló, null = lo canceló el usuario desde la pestaña Descargas (no es un fallo).</returns>
    private async Task<bool?> EjecutarDescargaTemaAsync(TemaAnimeItem tema, int aniListId, bool avisarSiFalla, CancellationToken ct = default)
    {
        var servicio = _animeThemesDownload;
        if (servicio == null) return false;

        tema.ProgresoDescarga = 0;
        tema.Descargando = true;
        try
        {
            var progreso = new Progress<double>(p => tema.ProgresoDescarga = p);
            string? ruta = await servicio.DescargarYConvertirAsync(aniListId, tema.Info, progreso, ct);
            tema.Descargado = ruta != null;
            if (ruta != null) _ = LeerDuracionAsync(tema, aniListId, _ctsMusica.Token);
            if (ruta == null && servicio.FueCanceladaPorUsuario(aniListId, tema.Info)) return null;
            if (ruta == null && avisarSiFalla)
            {
                _dialogService.MostrarToast(
                    LocalizationService.T("Det_MusicaErrorDescargaTitulo"),
                    string.Format(LocalizationService.T("Det_MusicaErrorDescargaMsjFormato"), tema.TituloCancion),
                    "AlertCircleOutline", "#EF4444");
            }
            return ruta != null;
        }
        finally
        {
            tema.Descargando = false;
        }
    }

    // === Spoilers ===

    /// <summary>
    /// Se tapa un tema que AnimeThemes marca como spoiler si corresponde a episodios que el usuario aún no ha visto. Con el
    /// anime terminado (o todos los episodios vistos) ya no hay nada que destripar.
    /// </summary>
    /// <param name="episodioMasAltoVisto">El episodio más avanzado que el usuario ha visto: cuenta más que cuántos lleva (quien
    /// empezó One Piece en el 1000 lleva 180 vistos pero ya va por el 1180).</param>
    internal static bool DebeOcultarSpoiler(AnimeThemeInfo tema, AnimeItem anime, int episodioMasAltoVisto = 0)
    {
        if (!tema.EsSpoiler) return false;
        if (string.Equals(anime.EstadoUsuario, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return false;

        int alcanzado = Math.Max(anime.EpisodiosVistos, episodioMasAltoVisto);
        if (anime.TotalEpisodios > 0 && alcanzado >= anime.TotalEpisodios) return false;

        int? primero = tema.PrimerEpisodio();
        return primero == null || primero > alcanzado;
    }

    private int EpisodioMasAltoVisto()
    {
        try { return _todosLosEpisodios.Where(e => e.Visto).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max(); }
        catch (InvalidOperationException) { return 0; } // la lista cambió mientras se leía: basta con el recuento
    }

    [RelayCommand]
    private void RevelarSpoilerTema(TemaAnimeItem? tema)
    {
        if (tema != null) tema.OcultoPorSpoiler = false;
    }

    // === Carpeta de música ===

    /// <summary>Abre en el Explorador la carpeta con los mp3 de este anime (o la de música si aún no hay ninguno).</summary>
    [RelayCommand]
    private void AbrirCarpetaMusica()
    {
        var anime = AnimeSeleccionado;
        var descargas = _animeThemesDownload;
        if (anime == null || descargas == null) return;

        try
        {
            string carpetaAnime = descargas.CarpetaDescargas(anime.AniListId);
            string carpeta = Directory.Exists(carpetaAnime) ? carpetaAnime : Path.GetDirectoryName(carpetaAnime)!;
            Directory.CreateDirectory(carpeta);

            if (AbrirCarpetaEnExplorador != null) AbrirCarpetaEnExplorador(carpeta);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{carpeta}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo abrir la carpeta de música: {ex.Message}");
        }
    }

    // === Descargar todos ===

    private bool EstaEnColaDescargaTodos(int aniListId, AnimeThemeInfo tema)
    {
        lock (_pendientesDescargaTodos)
            return DescargandoTodos && _aniListIdDescargaTodos == aniListId && _pendientesDescargaTodos.Contains(tema.ClaveEstable());
    }

    /// <summary>La fila que se ve ahora para ese tema (si el usuario salió de la ficha y volvió, es otra fila distinta).</summary>
    private TemaAnimeItem? FilaActual(int aniListId, string clave)
    {
        if (AnimeSeleccionado?.AniListId != aniListId) return null;
        lock (_candadoTemas) return TemasMusicales.FirstOrDefault(t => t.Info.ClaveEstable() == clave);
    }

    /// <summary>
    /// Descarga de una vez los temas de la lista que se ve (todos, o solo openings/endings según el filtro), 2 a la vez, tras
    /// confirmar cuántos son y cuánto pesan. Sigue aunque se salga de la ficha; al volver se ve por dónde va. Pulsarlo otra vez
    /// mientras descarga lo cancela. Al terminar, un solo aviso con el resumen (no uno por canción).
    /// </summary>
    // AllowConcurrentExecutions: el mismo botón cancela mientras descarga; sin esto quedaría deshabilitado durante la descarga.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DescargarTodosAsync()
    {
        if (DescargandoTodos)
        {
            if (MostrarProgresoDescargaTodos) _ctsDescargaTodos?.Cancel();
            else _dialogService.MostrarToast(LocalizationService.T("Det_MusicaDescargarTodosTitulo"), LocalizationService.T("Det_MusicaDescargarTodosOcupado"), "InformationOutline", "#2563EB");
            return;
        }

        var anime = AnimeSeleccionado;
        if (anime == null || _animeThemesDownload == null) return;

        var pendientes = TemasMusicalesVisibles.Where(t => t.PuedeDescargar && !t.Descargando && !t.PreparandoVistaPrevia).ToList();
        if (pendientes.Count == 0)
        {
            _dialogService.MostrarToast(LocalizationService.T("Det_MusicaDescargarTodosTitulo"), LocalizationService.T("Det_MusicaDescargarTodosNada"), "CheckCircleOutline", "#10B981");
            return;
        }

        // Las vistas previas ya preparadas no se bajan: se guardan (moverlas es instantáneo), así que no pesan.
        var aBajar = pendientes.Where(t => !t.VistaPreviaLista).ToList();
        string mensaje = aBajar.All(t => t.Info.TamanoBytes is > 0)
            ? string.Format(LocalizationService.T("Det_MusicaDescargarTodosMsjFormato"), pendientes.Count, TemaAnimeItem.FormatearTamano(aBajar.Sum(t => t.Info.TamanoBytes ?? 0)))
            : string.Format(LocalizationService.T("Det_MusicaDescargarTodosMsjSinTamanoFormato"), pendientes.Count);
        if (!await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_MusicaDescargarTodosTitulo"), mensaje, true, "DownloadMultiple", "#2563EB")) return;

        await DescargarVariosAsync(pendientes, anime);
    }

    private async Task DescargarVariosAsync(List<TemaAnimeItem> pendientes, AnimeItem anime)
    {
        int aniListId = anime.AniListId;
        int total = pendientes.Count, hechos = 0, fallidos = 0;
        using var cts = new CancellationTokenSource();
        _ctsDescargaTodos = cts;
        _aniListIdDescargaTodos = aniListId;
        lock (_pendientesDescargaTodos)
        {
            _pendientesDescargaTodos.Clear();
            foreach (var t in pendientes) _pendientesDescargaTodos.Add(t.Info.ClaveEstable());
        }
        foreach (var t in pendientes) t.EnCola = true;
        DescargandoTodos = true;

        void ActualizarTexto() => ProgresoDescargaTodosTexto = $"{Volatile.Read(ref hechos) + Volatile.Read(ref fallidos)} / {total}";
        ActualizarTexto();

        using var semaforo = new SemaphoreSlim(DescargasSimultaneasTodos);
        try
        {
            await Task.WhenAll(pendientes.Select(async original =>
            {
                string clave = original.Info.ClaveEstable();
                try { await semaforo.WaitAsync(cts.Token); }
                catch (OperationCanceledException) { return; }

                try
                {
                    lock (_pendientesDescargaTodos) _pendientesDescargaTodos.Remove(clave);
                    var fila = FilaActual(aniListId, clave) ?? original;
                    original.EnCola = false;
                    fila.EnCola = false;
                    if (cts.IsCancellationRequested || fila.Descargado) return;

                    bool? ok = fila.VistaPreviaLista && GuardarVistaPrevia(fila, aniListId)
                        ? true
                        : await EjecutarDescargaTemaAsync(fila, aniListId, avisarSiFalla: false, cts.Token);
                    if (ok == true) Interlocked.Increment(ref hechos);
                    else if (ok == false && !cts.IsCancellationRequested) Interlocked.Increment(ref fallidos);
                    ActualizarTexto();
                }
                finally
                {
                    semaforo.Release();
                }
            }));
        }
        finally
        {
            lock (_pendientesDescargaTodos) _pendientesDescargaTodos.Clear();
            foreach (var t in pendientes) t.EnCola = false;
            if (AnimeSeleccionado?.AniListId == aniListId)
                foreach (var t in TemasMusicales) t.EnCola = false;
            _ctsDescargaTodos = null;
            DescargandoTodos = false;
        }

        AvisarResumenDescargaTodos(anime, total, hechos, fallidos, cts.IsCancellationRequested);
    }

    private void AvisarResumenDescargaTodos(AnimeItem anime, int total, int hechos, int fallidos, bool cancelado)
    {
        string titulo = anime.Titulo;
        if (cancelado)
            _dialogService.MostrarToast(titulo, string.Format(LocalizationService.T("Det_MusicaDescargarTodosCanceladoFormato"), hechos, total), "CloseCircleOutline", "#94A3B8");
        else if (fallidos > 0)
            _dialogService.MostrarToast(titulo, string.Format(LocalizationService.T("Det_MusicaDescargarTodosConFallosFormato"), hechos, total, fallidos), "AlertCircleOutline", "#F59E0B");
        else
            _dialogService.MostrarToast(titulo, string.Format(LocalizationService.T("Det_MusicaDescargarTodosListoFormato"), hechos), "CheckCircleOutline", "#10B981");
    }

    /// <summary>Convierte la vista previa en descarga moviendo el archivo. False si no se pudo (entonces se descarga normalmente).</summary>
    private bool GuardarVistaPrevia(TemaAnimeItem tema, int aniListId)
    {
        var servicio = _animeThemesDownload!;
        bool guardado = false;

        ControlMusica.ConservandoPosicion(tema, () =>
        {
            guardado = servicio.GuardarVistaPrevia(aniListId, tema.Info);
            return guardado ? servicio.ObtenerRutaLocalEsperada(aniListId, tema.Info) : null;
        });

        if (!guardado) return false;

        tema.Descargado = true;
        tema.VistaPreviaLista = false;
        return true;
    }

    [RelayCommand]
    private void ReproducirTema(TemaAnimeItem? tema)
    {
        var anime = AnimeSeleccionado;
        if (tema == null || anime == null || !tema.PuedeReproducir || _animeThemesDownload == null) return;

        string? ruta = RutaDeTema(tema, anime.AniListId);
        if (ruta == null) return;

        try
        {
            ControlMusica.Alternar(tema, ruta);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo reproducir '{tema.Slug}': {ex.Message}");
        }
    }

    /// <summary>Salto desde la barra de progreso (segundos). Solo vale para la pista que está cargada.</summary>
    internal void BuscarTema(TemaAnimeItem? tema, double segundos)
    {
        if (tema == null || _controlMusica == null) return;
        _controlMusica.Buscar(tema, segundos);
    }

    [RelayCommand]
    private void EliminarTema(TemaAnimeItem? tema)
    {
        var anime = AnimeSeleccionado;
        if (tema == null || anime == null || _animeThemesDownload == null) return;

        // Suelta el archivo antes de borrarlo: Windows no deja borrar uno que el reproductor tiene abierto.
        if (ReferenceEquals(_controlMusica?.Actual, tema)) _controlMusica!.Detener();

        _animeThemesDownload.Eliminar(anime.AniListId, tema.Info);
        tema.Descargado = false;
        tema.DuracionArchivoSegundos = 0;
    }

    /// <summary>El archivo que suena para este tema: el guardado, o si no la vista previa. Null si no hay ninguno.</summary>
    private string? RutaDeTema(TemaAnimeItem tema, int aniListId)
    {
        var descargas = _animeThemesDownload;
        if (descargas == null) return null;

        return tema.Descargado
            ? descargas.ObtenerRutaLocalEsperada(aniListId, tema.Info)
            : descargas.ObtenerRutaVistaPrevia(aniListId, tema.Info);
    }

    /// <summary>Lee la duración de los temas que ya tienen archivo, para mostrarla antes de reproducir. En segundo plano y de uno en uno.</summary>
    private async Task LeerDuracionesAsync(System.Collections.Generic.IReadOnlyList<TemaAnimeItem> temas, int aniListId, CancellationToken ct)
    {
        foreach (var tema in temas)
        {
            if (ct.IsCancellationRequested) return;
            if (tema.PuedeReproducir) await LeerDuracionAsync(tema, aniListId, ct);
        }
    }

    private async Task LeerDuracionAsync(TemaAnimeItem tema, int aniListId, CancellationToken ct)
    {
        var servicio = _audioDuration;
        if (servicio == null) return;

        try
        {
            string? ruta = RutaDeTema(tema, aniListId);
            if (ruta == null) return;

            var duracion = await servicio.ObtenerDuracionAsync(ruta, ct);
            if (duracion is { } d && !ct.IsCancellationRequested) tema.DuracionArchivoSegundos = d.TotalSeconds;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo leer la duración de '{tema.Slug}': {ex.Message}");
        }
    }

    /// <summary>
    /// Reproducción continua: al terminar un tema pasa al siguiente de la lista que se pueda escuchar (guardado o con vista
    /// previa), saltándose los que aún no tienen archivo. Al llegar al final se detiene. No baja nada por su cuenta.
    /// </summary>
    private void AlTerminarPistaMusica(TemaAnimeItem terminado)
    {
        if (!ReproduccionContinuaMusica) return;

        int indice = TemasMusicales.IndexOf(terminado);
        if (indice < 0) return;

        for (int i = indice + 1; i < TemasMusicales.Count; i++)
        {
            var siguiente = TemasMusicales[i];
            if (!siguiente.PuedeReproducir) continue;

            // Se aplaza un instante: el reproductor está en mitad de su evento de "terminó" y no debe cerrarse desde dentro.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => ReproducirTema(siguiente)), System.Windows.Threading.DispatcherPriority.Background);
            else
                ReproducirTema(siguiente);
            return;
        }
    }

    /// <summary>Estado limpio al cargar otro anime: corta el sonido y cancela lo que se estuviera preparando.</summary>
    private void ReiniciarMusica()
    {
        _ctsMusica.Cancel();
        _ctsMusica.Dispose();
        _ctsMusica = new CancellationTokenSource();

        _controlMusica?.Detener();
        TemasMusicales.Clear();
        TemasMusicalesVisibles.Clear();
        FiltroTemasMusicales = "Todos"; // el filtro de una ficha no debe seguir aplicado en la siguiente
        EnlacesMusica.Clear();
        OnPropertyChanged(nameof(TieneEnlacesMusica));
        MostrandoPanelMusica = false;
        OnPropertyChanged(nameof(TieneTemasMusicales));
        OnPropertyChanged(nameof(MostrarFiltroTemasMusicales));
        OnPropertyChanged(nameof(MostrarProgresoDescargaTodos));
    }

    /// <summary>Corta el sonido sin tocar la lista (al salir de la ficha hacia otra pestaña).</summary>
    public void DetenerMusica()
    {
        _controlMusica?.Detener();
        GuardarAjustesMusicaPendientesYa();
    }

    private void LiberarMusica()
    {
        _ctsDescargaTodos?.Cancel();
        GuardarAjustesMusicaPendientesYa();
        _ctsMusica.Cancel();
        _ctsMusica.Dispose();
        _controlMusica?.Dispose();
    }
}
