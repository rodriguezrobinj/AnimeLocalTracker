using System;
using System.Collections.ObjectModel;
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
    private CancellationTokenSource? _ctsGuardadoAjustesMusica;
    private bool _ajustesMusicaPendientes;
    private bool _aplicandoAjustesMusicaGuardados;

    /// <summary>Espera tras el último cambio de volumen antes de escribir settings.json (cada guardado reescribe el archivo entero).
    /// Ajustable solo en pruebas.</summary>
    internal TimeSpan RetrasoGuardadoAjustesMusica { get; set; } = TimeSpan.FromMilliseconds(500);

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

        CargandoTemasMusicales = true;
        try
        {
            var temas = await servicio.ObtenerTemasAsync(anime.AniListId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado)) return;

            TemasMusicales.Clear();
            foreach (var tema in temas)
            {
                var item = new TemaAnimeItem { Info = tema };
                item.Descargado = descargas.EstaDescargado(anime.AniListId, tema);
                // Una vista previa ya preparada (p. ej. al volver a la ficha) se puede escuchar sin bajarla otra vez.
                item.VistaPreviaLista = !item.Descargado && descargas.ObtenerRutaVistaPrevia(anime.AniListId, tema) != null;
                TemasMusicales.Add(item);
            }
            OnPropertyChanged(nameof(TieneTemasMusicales));
            OnPropertyChanged(nameof(MostrarFiltroTemasMusicales));
            AplicarFiltroTemas();
            CargaDuracionesTarea = LeerDuracionesAsync(TemasMusicales.ToList(), anime.AniListId, _ctsMusica.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron cargar los openings/endings: {ex.Message}");
        }
        finally
        {
            CargandoTemasMusicales = false;
        }
    }

    /// <summary>"Escuchar antes de descargar": prepara la canción completa en la caché temporal y la reproduce.</summary>
    [RelayCommand]
    private async Task PrevisualizarTemaAsync(TemaAnimeItem? tema)
    {
        var servicio = _animeThemesDownload;
        var anime = AnimeSeleccionado;
        if (tema == null || servicio == null || anime == null) return;
        if (tema.Descargado || tema.Descargando || tema.PreparandoVistaPrevia) return;

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
        if (tema == null || tema.Descargando || tema.PreparandoVistaPrevia || _animeThemesDownload == null) return;
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        if (tema.VistaPreviaLista && GuardarVistaPrevia(tema, anime.AniListId)) return;

        tema.ProgresoDescarga = 0;
        tema.Descargando = true;
        try
        {
            var progreso = new Progress<double>(p => tema.ProgresoDescarga = p);
            string? ruta = await _animeThemesDownload.DescargarYConvertirAsync(anime.AniListId, tema.Info, progreso, CancellationToken.None);
            tema.Descargado = ruta != null;
            if (ruta != null) _ = LeerDuracionAsync(tema, anime.AniListId, _ctsMusica.Token);
            if (ruta == null)
            {
                _dialogService.MostrarToast(
                    LocalizationService.T("Det_MusicaErrorDescargaTitulo"),
                    string.Format(LocalizationService.T("Det_MusicaErrorDescargaMsjFormato"), tema.TituloCancion),
                    "AlertCircleOutline", "#EF4444");
            }
        }
        finally
        {
            tema.Descargando = false;
        }
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
    }

    /// <summary>Corta el sonido sin tocar la lista (al salir de la ficha hacia otra pestaña).</summary>
    public void DetenerMusica()
    {
        _controlMusica?.Detener();
        GuardarAjustesMusicaPendientesYa();
    }

    private void LiberarMusica()
    {
        GuardarAjustesMusicaPendientesYa();
        _ctsMusica.Cancel();
        _ctsMusica.Dispose();
        _controlMusica?.Dispose();
    }
}
