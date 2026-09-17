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
public partial class ActualizacionesViewModel : ObservableObject, IDisposable, IRecipient<DescargaProgresoMensaje>
{
    private const int LimiteActualizaciones = 40;

    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDownloadService _downloadService;
    private readonly IFileScannerService _fileScannerService;
    private readonly PythonEpisodeEnricher? _enricher;
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
        PythonEpisodeEnricher? enricher = null)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _downloadService = downloadService;
        _fileScannerService = fileScannerService;
        _enricher = enricher;

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

            // 1) Animes de la biblioteca en emisión (proyección ligera)
            var animes = (await _databaseService.ObtenerAnimesLigerosAsync() ?? new List<AnimeItem>())
                .Where(a => string.Equals(a.Estado, "RELEASING", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var ids = animes.Select(a => a.AniListId).Distinct().ToList();
            if (ids.Count == 0)
            {
                Items.Clear();
                ItemsAgrupados.Clear();
                return;
            }

            // 2) Episodios ya emitidos en los últimos 7 días (hasta ahora)
            long ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long inicio = ahora - 7L * 24 * 60 * 60;
            var schedule = await _animeTrackingService.ObtenerCalendarioEmisionAsync(ids, inicio, ahora);
            if (schedule == null || schedule.Count == 0)
            {
                Items.Clear();
                ItemsAgrupados.Clear();
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
            ItemsAgrupados = AgruparPorFecha(lista);
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
            return;
        }

        item.IsDownloading = message.IsDownloading;
        item.DownloadProgress = message.Progreso;
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
    }
}
