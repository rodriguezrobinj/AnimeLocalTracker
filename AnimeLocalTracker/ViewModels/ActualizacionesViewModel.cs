using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
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

    public ActualizacionesViewModel(
        IDatabaseService databaseService,
        IAnimeTrackingService animeTrackingService,
        IDownloadService downloadService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _downloadService = downloadService;

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    [RelayCommand]
    public async Task CargarActualizacionesAsync()
    {
        if (!await _cargaLock.WaitAsync(0)) return;

        try
        {
            EstaCargando = true;
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

            // 3) Episodios que ya tienes localmente (con archivo) por anime/episodio
            var registros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<RegistroEpisodio>();
            var dicDescargados = registros
                .Where(r => !string.IsNullOrWhiteSpace(r.RutaArchivo))
                .GroupBy(r => (r.AniListId, r.NumeroEpisodio))
                .ToDictionary(g => g.Key, g => g.First().RutaArchivo);

            var dicAnimes = animes.ToDictionary(a => a.AniListId);
            var dicPortadas = animes.ToDictionary(a => a.AniListId, a => a.PortadaVisible);

            // 4) Feed: episodios recién emitidos de la biblioteca (estén o no descargados)
            var lista = new List<ActualizacionItemViewModel>();
            var vistos = new HashSet<(int AniListId, int NumeroEpisodio)>();

            foreach (var ep in schedule
                         .Where(e => e.FechaEmision.ToUniversalTime() <= DateTime.UtcNow)
                         .OrderByDescending(e => e.FechaEmision))
            {
                if (!vistos.Add((ep.AniListId, ep.NumeroEpisodio))) continue;
                if (!dicAnimes.TryGetValue(ep.AniListId, out var anime)) continue;

                bool descargado = dicDescargados.TryGetValue((ep.AniListId, ep.NumeroEpisodio), out var rutaArchivo);

                lista.Add(new ActualizacionItemViewModel
                {
                    AniListId = ep.AniListId,
                    TituloAnime = anime.Titulo,
                    NumeroEpisodio = ep.NumeroEpisodio,
                    RutaCarpeta = anime.RutaCarpeta,
                    RutaPortada = dicPortadas[ep.AniListId] ?? string.Empty,
                    FechaEmision = ep.FechaEmision,
                    Descargado = descargado,
                    RutaArchivo = rutaArchivo ?? string.Empty
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
    private void Reproducir(ActualizacionItemViewModel? item)
    {
        if (item == null || !item.Descargado || string.IsNullOrWhiteSpace(item.RutaArchivo)) return;

        if (!System.IO.File.Exists(item.RutaArchivo))
        {
            item.Descargado = false;
            item.RutaArchivo = string.Empty;
            return;
        }

        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
            item.RutaArchivo,
            item.AniListId,
            item.TituloAnime,
            item.NumeroEpisodio,
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
            }
            return;
        }

        item.IsDownloading = message.IsDownloading;
        item.DownloadProgress = message.Progreso;
    }

    private void NotificarEstados()
    {
        OnPropertyChanged(nameof(TieneItems));
        OnPropertyChanged(nameof(EstaVacio));
    }
}
