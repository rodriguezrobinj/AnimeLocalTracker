using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Actualizaciones: episodios recién emitidos (últimos 7 días) de los animes en emisión
/// de tu biblioteca que aún no tienes descargados, con descarga directa desde la lista.
/// </summary>
public partial class ActualizacionesViewModel : ObservableObject, IDisposable,
    IRecipient<DescargaProgresoMensaje>, IRecipient<EpisodioActualizadoMensaje>, IRecipient<IdiomaCambiadoMensaje>
{
    // Claves de los filtros (chips)
    public const string FiltroTodos = "Todos";
    public const string FiltroPorDescargar = "PorDescargar";
    public const string FiltroSinVer = "SinVer";
    public const string FiltroListos = "Listos";

    private const int LimiteActualizaciones = 40;

    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDownloadService _downloadService;
    private readonly IFileScannerService _fileScannerService;
    private readonly PythonEpisodeEnricher? _enricher;
    private readonly IDialogService? _dialogService;
    private string _filtroActual = FiltroTodos;
    private readonly SemaphoreSlim _cargaLock = new(1, 1);

    // CA1001: el semáforo se libera en el cierre de la app (singleton DI)
    public void Dispose()
    {
        _cargaLock.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty]
    private ObservableCollection<ActualizacionItemViewModel> _items = [];

    /// <summary>Lista agrupada para la UI: alterna cabeceras de fecha y tarjetas.</summary>
    [ObservableProperty]
    private ObservableCollection<object> _itemsAgrupados = [];

    [ObservableProperty]
    private bool _estaCargando;

    public bool TieneItems => Items.Count > 0;
    public bool EstaVacio => !EstaCargando && Items.Count == 0;

    // === Resumen, filtros y acciones en bloque ===

    /// <summary>Episodios nuevos de esta semana (todos los cargados).</summary>
    [ObservableProperty]
    private int _totalNuevos;

    /// <summary>Todavía sin archivo local (incluye los que se están descargando).</summary>
    [ObservableProperty]
    private int _totalPorDescargar;

    /// <summary>Con archivo y sin ver: se pueden ver ya.</summary>
    [ObservableProperty]
    private int _totalListos;

    [ObservableProperty]
    private int _totalSinVer;

    /// <summary>Sin archivo y sin descarga en curso: lo que "Descargar pendientes" encolaría.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeDescargarPendientes))]
    private int _pendientesEncolables;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeMarcarVistos))]
    private int _sinVerParaMarcar;

    [ObservableProperty]
    private string _descargarPendientesTexto = string.Empty;

    [ObservableProperty]
    private string _marcarVistosTexto = string.Empty;

    [ObservableProperty]
    private ObservableCollection<FiltroChip> _filtros = [];

    public bool PuedeDescargarPendientes => PendientesEncolables > 0;
    public bool PuedeMarcarVistos => SinVerParaMarcar > 0;

    /// <summary>Hay tarjetas que mostrar con el filtro actual.</summary>
    public bool TieneResultados => ItemsAgrupados.Count > 0;

    /// <summary>Hay episodios, pero ninguno cumple el filtro elegido.</summary>
    public bool SinResultados => !EstaCargando && Items.Count > 0 && ItemsAgrupados.Count == 0;

    // NAV-01: evita recorrer toda la BD + volver a llamar a AniList en cada visita a la
    // pestaña — se recarga como mucho una vez cada 30 s (mismo cooldown que Historial).
    private static readonly TimeSpan CooldownRecarga = TimeSpan.FromSeconds(30);
    private DateTime _ultimaCargaUtc = DateTime.MinValue;
    public bool NecesitaRecargar() =>
        Items.Count == 0 || (DateTime.UtcNow - _ultimaCargaUtc) >= CooldownRecarga;

    public ActualizacionesViewModel(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IDownloadService downloadService,
        IFileScannerService fileScannerService,
        PythonEpisodeEnricher? enricher = null,
        IDialogService? dialogService = null)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _downloadService = downloadService;
        _fileScannerService = fileScannerService;
        _enricher = enricher;
        _dialogService = dialogService;

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    [RelayCommand]
    public async Task CargarActualizacionesAsync()
    {
        if (!await _cargaLock.WaitAsync(0)) return;

        try
        {
            EstaCargando = true;
            _ultimaCargaUtc = DateTime.UtcNow;
            NotificarEstados();

            // 1) Animes de la biblioteca (proyección ligera). NO se filtra por Estado == RELEASING:
            // al actualizar datos de AniList (Galería/Detalle) un anime que acaba de emitir su final
            // pasa a FINISHED, y con ese filtro sus episodios de los últimos 7 días desaparecían del
            // feed de golpe (y no volvían). El límite de "recién emitido" ya lo pone la ventana de 7
            // días de la consulta a AniList, así que solo se excluyen los que aún no se estrenan.
            var animes = (await _databaseService.ObtenerAnimesLigerosAsync() ?? new List<AnimeItem>())
                .Where(a => !string.Equals(a.Estado, "NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var ids = animes.Select(a => a.AniListId).Distinct().ToList();
            if (ids.Count == 0)
            {
                Items.Clear();
                ItemsAgrupados.Clear();
                ActualizarResumenYFiltro();
                return;
            }

            // 2) Episodios ya emitidos en los últimos 7 días (hasta ahora)
            long ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long inicio = ahora - 7L * 24 * 60 * 60;
            var (exito, schedule) = await _animeTrackingService.ObtenerCalendarioEmisionAsync(ids, inicio, ahora);
            if (!exito)
            {
                // Sin conexión (o AniList caído/limitando): se conserva el feed ya cargado —
                // no tiene sentido vaciar los episodios recién descargados/vistos por no poder refrescar.
                AppLogger.Debug("ActualizacionesViewModel", "No se pudieron consultar las actualizaciones; se conserva el feed anterior.");
                return;
            }

            if (schedule.Count == 0)
            {
                Items.Clear();
                ItemsAgrupados.Clear();
                ActualizarResumenYFiltro();
                return;
            }

            var dicAnimes = animes.ToDictionary(a => a.AniListId);
            var dicPortadas = animes.ToDictionary(a => a.AniListId, a => a.PortadaVisible);

            // 3) Episodios que ya tienes localmente por anime/episodio: se escanea la
            // carpeta en disco (como DetalleViewModel/NewEpisodeNotifier), NO el registro
            // de la BD — un episodio recién descargado (el caso típico de esta pestaña)
            // no tiene fila en RegistroEpisodio hasta que se reproduce/marca por primera
            // vez, así que confiar en la BD lo mostraba como "no descargado" aunque el
            // archivo ya estuviera en disco.
            var idsConEmision = schedule.Select(e => e.AniListId).Distinct();
            var dicDescargados = new Dictionary<(int AniListId, int NumeroEpisodio), EpisodioItem>();
            foreach (var animeId in idsConEmision)
            {
                if (!dicAnimes.TryGetValue(animeId, out var anime)) continue;
                if (string.IsNullOrWhiteSpace(anime.RutaCarpeta)) continue;

                try
                {
                    var episodiosEnDisco = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta)
                        ?? new List<EpisodioItem>();
                    foreach (var epDisco in episodiosEnDisco)
                    {
                        dicDescargados[(animeId, epDisco.NumeroEpisodio)] = epDisco;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("ActualizacionesViewModel", $"No se pudo escanear '{anime.RutaCarpeta}': {ex.Message}");
                }
            }

            // Visto/progreso SÍ se leen del registro de la BD (a diferencia de "descargado",
            // ver comentario arriba): el visionado y el progreso de reproducción solo existen ahí.
            var registros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<RegistroEpisodio>();
            var dicRegistros = registros
                .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
                .ToDictionary(g => g.Key, g => g.First());

            // 4) Feed: episodios recién emitidos de la biblioteca (estén o no descargados)
            var lista = new List<ActualizacionItemViewModel>();
            var vistos = new HashSet<(int AniListId, int NumeroEpisodio)>();

            // NAV-02: FechaEmision viene de DateTimeOffset.FromUnixTimeSeconds(...).DateTime,
            // que queda Kind=Unspecified aunque el valor YA es UTC — llamar a ToUniversalTime()
            // la trata como hora local y la desplaza otra vez por el huso horario, retrasando
            // hasta varias horas la aparición de episodios recién emitidos (visibles ya en
            // Calendario, que no hace esta conversión). Reinterpretar el Kind sin convertir.
            foreach (var ep in schedule
                         .Where(e => DateTime.SpecifyKind(e.FechaEmision, DateTimeKind.Utc) <= DateTime.UtcNow)
                         .OrderByDescending(e => e.FechaEmision))
            {
                if (!vistos.Add((ep.AniListId, ep.NumeroEpisodio))) continue;
                if (!dicAnimes.TryGetValue(ep.AniListId, out var anime)) continue;

                bool descargado = dicDescargados.TryGetValue((ep.AniListId, ep.NumeroEpisodio), out var epInfo);
                dicRegistros.TryGetValue((ep.AniListId, ep.NumeroEpisodio), out var registro);

                lista.Add(new ActualizacionItemViewModel
                {
                    AniListId = ep.AniListId,
                    TituloAnime = anime.Titulo,
                    NumeroEpisodio = ep.NumeroEpisodio,
                    RutaCarpeta = anime.RutaCarpeta,
                    RutaPortada = dicPortadas[ep.AniListId] ?? string.Empty,
                    FechaEmision = ep.FechaEmision,
                    Descargado = descargado,
                    RutaArchivo = epInfo?.RutaCompleta ?? string.Empty,
                    TamanoArchivoFormateado = epInfo?.TamanoArchivoFormateado ?? string.Empty,
                    Visto = registro?.VistoLocal ?? false,
                    ProgresoSegundos = registro?.ProgresoSegundos ?? 0,
                    TotalSegundos = registro?.TotalSegundos ?? 0
                });

                if (lista.Count >= LimiteActualizaciones) break;
            }

            Items = new ObservableCollection<ActualizacionItemViewModel>(lista);
            ActualizarResumenYFiltro();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ActualizacionesViewModel", "Error al cargar las actualizaciones", ex);
        }
        finally
        {
            EstaCargando = false;
            NotificarEstados();
            _cargaLock.Release();
        }
    }

    private static ObservableCollection<object> AgruparPorFecha(IEnumerable<ActualizacionItemViewModel> items)
    {
        var salida = new List<object>();
        foreach (var grupo in items.GroupBy(i => i.GrupoTemporal))
        {
            salida.Add(grupo.Key);
            salida.AddRange(grupo);
        }
        return new ObservableCollection<object>(salida);
    }

    [RelayCommand]
    private async Task DescargarAsync(ActualizacionItemViewModel? item)
    {
        if (item == null || item.IsDownloading || item.Descargado) return;

        item.IsDownloading = true;
        item.DownloadProgress = 0;
        try
        {
            await _downloadService.IniciarDescargaEpisodioAsync(
                item.AniListId,
                item.TituloAnime,
                item.RutaCarpeta,
                item.NumeroEpisodio);
        }
        catch (Exception ex)
        {
            item.IsDownloading = false;
            AppLogger.Error("ActualizacionesViewModel", $"Error al encolar descarga de {item.TituloAnime} Ep {item.NumeroEpisodio}", ex);
        }
    }

    [RelayCommand]
    private async Task ReproducirAsync(ActualizacionItemViewModel? item)
    {
        if (item == null || !item.Descargado || string.IsNullOrWhiteSpace(item.RutaArchivo)) return;

        if (!System.IO.File.Exists(item.RutaArchivo))
        {
            item.Descargado = false;
            item.RutaArchivo = string.Empty;
            item.TamanoArchivoFormateado = string.Empty;
            return;
        }

        // NAV-03: sin esto el reproductor abría el capítulo sin lista de episodios y el
        // usuario no podía moverse al anterior/siguiente (siempre aparecía como si no
        // hubiera ninguno) — Detalle sí la arma porque ya tiene todos los episodios en
        // memoria; aquí hay que escanear la carpeta igual que hace ella.
        List<EpisodioItem> episodiosDisponibles;
        try
        {
            episodiosDisponibles = await _fileScannerService.EscanearEpisodiosAsync(item.RutaCarpeta)
                ?? new List<EpisodioItem>();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActualizacionesViewModel", $"No se pudo escanear '{item.RutaCarpeta}' para navegación de episodios: {ex.Message}");
            episodiosDisponibles = new List<EpisodioItem>();
        }

        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
            item.RutaArchivo,
            item.AniListId,
            item.TituloAnime,
            item.NumeroEpisodio,
            EpisodiosDisponibles: episodiosDisponibles,
            RutaPortada: item.RutaPortada));
    }

    [RelayCommand]
    private async Task NavegarDetalleAsync(ActualizacionItemViewModel? item)
    {
        if (item == null) return;

        var anime = await _databaseService.ObtenerAnimePorIdAsync(item.AniListId);
        if (anime != null)
        {
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
        }
    }

    /// <summary>Actualiza el círculo de progreso de la fila cuando cambia la descarga.</summary>
    public void Receive(DescargaProgresoMensaje message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => RecibirProgresoDescarga(message));
            return;
        }

        RecibirProgresoDescarga(message);
    }

    private void RecibirProgresoDescarga(DescargaProgresoMensaje message)
    {
        var item = Items.FirstOrDefault(i =>
            i.AniListId == message.AniListId && i.NumeroEpisodio == message.NumeroEpisodio);
        if (item == null) return;

        if (message.IsCompleted)
        {
            item.IsDownloading = false;
            item.DownloadProgress = 100;
            item.Descargado = true;
            if (!string.IsNullOrWhiteSpace(message.RutaArchivo))
            {
                item.RutaArchivo = message.RutaArchivo;
                try
                {
                    if (File.Exists(message.RutaArchivo))
                    {
                        item.TamanoArchivoFormateado = EpisodioItem.FormatearTamano(new FileInfo(message.RutaArchivo).Length);
                    }
                }
                catch { }
            }

            // Generar la miniatura automáticamente: si el capítulo se descarga desde aquí
            // (en vez de desde la Ficha), nunca se pasa por DetalleViewModel.InicializarAsync
            // — que es lo único que dispara el enriquecimiento en segundo plano — así que sin
            // esto el episodio se quedaba sin miniatura hasta que el usuario entrara
            // manualmente a la ficha del anime.
            _ = Task.Run(() => GenerarMiniaturaTrasDescargaAsync(item));
            ActualizarResumenYFiltro();
            return;
        }

        bool cambioDeEstado = item.IsDownloading != message.IsDownloading;
        item.IsDownloading = message.IsDownloading;
        item.DownloadProgress = message.Progreso;

        // Los contadores solo cambian al empezar/terminar una descarga, no en cada porcentaje.
        if (cambioDeEstado) ActualizarResumenYFiltro();
    }

    private async Task GenerarMiniaturaTrasDescargaAsync(ActualizacionItemViewModel item)
    {
        if (_enricher == null || string.IsNullOrWhiteSpace(item.RutaArchivo)) return;

        try
        {
            string thumbPath = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(item.RutaArchivo);
            bool generada = await _enricher.ExtraerMiniaturaAsync(item.RutaArchivo, thumbPath);
            if (!generada) return;

            // Preservar visto/progreso si ya existía un registro (p. ej. una descarga previa
            // de este mismo episodio que el usuario ya empezó a ver) — GuardarRegistroEpisodioAsync
            // sobrescribe esos campos con lo que se le pase.
            var existentes = await _databaseService.ObtenerRegistrosPorAnimeAsync(item.AniListId);
            var existente = existentes.FirstOrDefault(r => r.NumeroEpisodio == item.NumeroEpisodio);

            await _databaseService.GuardarRegistroEpisodioAsync(new RegistroEpisodio
            {
                AniListId = item.AniListId,
                NumeroEpisodio = item.NumeroEpisodio,
                RutaArchivo = item.RutaArchivo,
                RutaMiniatura = thumbPath,
                VistoLocal = existente?.VistoLocal ?? false,
                FavoritoLocal = existente?.FavoritoLocal ?? false,
                ProgresoSegundos = existente?.ProgresoSegundos ?? 0,
                TotalSegundos = existente?.TotalSegundos ?? 0,
                UltimaReproduccion = existente?.UltimaReproduccion
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActualizacionesViewModel", $"Error generando miniatura tras descarga de {item.TituloAnime} Ep {item.NumeroEpisodio}: {ex.Message}");
        }
    }

    private void NotificarEstados()
    {
        OnPropertyChanged(nameof(TieneItems));
        OnPropertyChanged(nameof(EstaVacio));
        OnPropertyChanged(nameof(TieneResultados));
        OnPropertyChanged(nameof(SinResultados));
    }

    /// <summary>
    /// Recalcula los contadores, los chips, los textos de los botones y la lista visible según el filtro.
    /// Se llama al cargar y cada vez que cambia el estado de un episodio (descarga, visto, progreso).
    /// </summary>
    private void ActualizarResumenYFiltro()
    {
        TotalNuevos = Items.Count;
        TotalPorDescargar = Items.Count(i => i.PorDescargar);
        TotalListos = Items.Count(i => i.ListoParaVer);
        TotalSinVer = Items.Count(i => i.SinVer);
        PendientesEncolables = Items.Count(i => i.PorDescargar && !i.IsDownloading);
        SinVerParaMarcar = TotalSinVer;

        DescargarPendientesTexto = string.Format(LocalizationService.T("Act_DescargarPendientesFormato"), PendientesEncolables);
        MarcarVistosTexto = string.Format(LocalizationService.T("Act_MarcarVistosFormato"), SinVerParaMarcar);

        string Chip(string claveTexto, int cantidad) =>
            string.Format(LocalizationService.T("Act_FiltroFormato"), LocalizationService.T(claveTexto), cantidad);

        Filtros = new ObservableCollection<FiltroChip>
        {
            new(FiltroTodos, Chip("Act_Filtro_Todos", TotalNuevos), _filtroActual == FiltroTodos),
            new(FiltroPorDescargar, Chip("Act_Filtro_PorDescargar", TotalPorDescargar), _filtroActual == FiltroPorDescargar),
            new(FiltroListos, Chip("Act_Filtro_Listos", TotalListos), _filtroActual == FiltroListos),
            new(FiltroSinVer, Chip("Act_Filtro_SinVer", TotalSinVer), _filtroActual == FiltroSinVer),
        };

        IEnumerable<ActualizacionItemViewModel> visibles = _filtroActual switch
        {
            FiltroPorDescargar => Items.Where(i => i.PorDescargar),
            FiltroListos => Items.Where(i => i.ListoParaVer),
            FiltroSinVer => Items.Where(i => i.SinVer),
            _ => Items
        };

        ItemsAgrupados = AgruparPorFecha(visibles);
        NotificarEstados();
    }

    [RelayCommand]
    private void SeleccionarFiltro(string? clave)
    {
        _filtroActual = clave ?? FiltroTodos;
        ActualizarResumenYFiltro();
    }

    /// <summary>
    /// El botón principal de la tarjeta: descarga si no hay archivo; si lo hay, reproduce (continúa si estaba a
    /// medias, o lo vuelve a ver si ya estaba visto).
    /// </summary>
    [RelayCommand]
    private async Task EjecutarAccionAsync(ActualizacionItemViewModel? item)
    {
        if (item == null) return;

        if (item.Descargado) await ReproducirAsync(item);
        else await DescargarAsync(item);
    }

    [RelayCommand]
    private async Task DescargarPendientesAsync()
    {
        var pendientes = Items.Where(i => i.PorDescargar && !i.IsDownloading).ToList();
        if (pendientes.Count == 0) return;

        if (_dialogService != null)
        {
            bool confirmado = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Act_ConfirmDescargarTitulo"),
                string.Format(LocalizationService.T("Act_ConfirmDescargarMsj"), pendientes.Count),
                true, "DownloadMultiple", "#60A5FA");
            if (!confirmado) return;
        }

        foreach (var item in pendientes) await DescargarAsync(item);

        _dialogService?.MostrarToast(
            LocalizationService.T("Act_Titulo"),
            string.Format(LocalizationService.T("Act_DescargaEncoladaFormato"), pendientes.Count),
            "DownloadMultiple", "#60A5FA");
        ActualizarResumenYFiltro();
    }

    /// <summary>
    /// Marca como vistos todos los episodios sin ver de la lista. Marcado MANUAL: no fabrica fecha de visionado
    /// (persistence.md #5) y conserva favorito y duración de lo que ya hubiera guardado.
    /// </summary>
    [RelayCommand]
    private async Task MarcarTodoVistoAsync()
    {
        var sinVer = Items.Where(i => i.SinVer).ToList();
        if (sinVer.Count == 0) return;

        if (_dialogService != null)
        {
            bool confirmado = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Act_ConfirmVistosTitulo"),
                string.Format(LocalizationService.T("Act_ConfirmVistosMsj"), sinVer.Count),
                true, "EyeCheckOutline", "#A78BFA");
            if (!confirmado) return;
        }

        try
        {
            var existentes = (await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<RegistroEpisodio>())
                .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
                .ToDictionary(g => g.Key, g => g.First());

            var registros = sinVer.Select(i =>
            {
                existentes.TryGetValue((i.AniListId, i.NumeroEpisodio), out var existente);
                return new RegistroEpisodio
                {
                    AniListId = i.AniListId,
                    NumeroEpisodio = i.NumeroEpisodio,
                    RutaArchivo = string.IsNullOrWhiteSpace(i.RutaArchivo) ? existente?.RutaArchivo ?? string.Empty : i.RutaArchivo,
                    VistoLocal = true,
                    FavoritoLocal = existente?.FavoritoLocal ?? false,
                    TotalSegundos = existente?.TotalSegundos ?? 0,
                    ProgresoSegundos = 0
                };
            }).ToList();

            await _databaseService.GuardarRegistrosEpisodioBulkAsync(registros);
        }
        catch (Exception ex)
        {
            AppLogger.Error("ActualizacionesViewModel", "Error al marcar los episodios como vistos", ex);
            return;
        }

        foreach (var item in sinVer)
        {
            item.Visto = true;
            item.ProgresoSegundos = 0;
            WeakReferenceMessenger.Default.Send(new EpisodioActualizadoMensaje(item.AniListId, item.NumeroEpisodio, true, 0, 0));
        }

        _dialogService?.MostrarToast(
            LocalizationService.T("Act_Titulo"),
            string.Format(LocalizationService.T("Act_MarcadosVistosFormato"), sinVer.Count),
            "EyeCheckOutline", "#A78BFA");
        ActualizarResumenYFiltro();
    }

    /// <summary>Visto/progreso en vivo: lo que pasa en el reproductor o en la ficha se refleja aquí sin recargar.</summary>
    public void Receive(EpisodioActualizadoMensaje message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => RecibirEpisodioActualizado(message));
            return;
        }

        RecibirEpisodioActualizado(message);
    }

    private void RecibirEpisodioActualizado(EpisodioActualizadoMensaje message)
    {
        // Episodio 0 = cambio en bloque (p. ej. "marcar vistos" desde la ficha): no se sabe cuáles, se recarga.
        if (message.NumeroEpisodio <= 0)
        {
            _ultimaCargaUtc = DateTime.MinValue;
            return;
        }

        var item = Items.FirstOrDefault(i => i.AniListId == message.AnimeId && i.NumeroEpisodio == message.NumeroEpisodio);
        if (item == null) return;

        item.Visto = message.VistoLocal;
        item.ProgresoSegundos = message.ProgresoSegundos;
        if (message.TotalSegundos > 0) item.TotalSegundos = message.TotalSegundos;
        ActualizarResumenYFiltro();
    }

    /// <summary>Los textos de las tarjetas, chips y botones se construyen localizados: al cambiar de idioma se rehacen.</summary>
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
        foreach (var item in Items) item.RefrescarTextos();
        ActualizarResumenYFiltro();
    }
}
