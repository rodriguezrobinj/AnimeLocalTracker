using System;
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

public partial class CalendarioViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly SemaphoreSlim _cargaLock = new(1, 1);

    // CA1001: el semáforo y el temporizador se liberan en el cierre de la app (singleton DI)
    public void Dispose()
    {
        _temporizadorEstado?.Stop();
        _cargaLock.Dispose();
        GC.SuppressFinalize(this);
    }

    [ObservableProperty] private bool _estaCargando;
    [ObservableProperty] private int _totalAnimesEnEmision;

    // Día actual para el badge "HOY" del calendario (formato invariante: LUNES, MARTES, ...)
    private static readonly string[] NombresDias = { "DOMINGO", "LUNES", "MARTES", "MIÉRCOLES", "JUEVES", "VIERNES", "SÁBADO" };
    public string DiaActual => NombresDias[(int)DateTime.Now.DayOfWeek];

    public ObservableCollection<AiringEpisode> Lunes { get; } = new();
    public ObservableCollection<AiringEpisode> Martes { get; } = new();
    public ObservableCollection<AiringEpisode> Miercoles { get; } = new();
    public ObservableCollection<AiringEpisode> Jueves { get; } = new();
    public ObservableCollection<AiringEpisode> Viernes { get; } = new();
    public ObservableCollection<AiringEpisode> Sabado { get; } = new();
    public ObservableCollection<AiringEpisode> Domingo { get; } = new();

    public CalendarioViewModel(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;

        // Refresco en vivo del estado "EMITIDO": cada minuto se revalúa la hora de
        // emisión para que el badge cambie sin necesidad de pulsar ACTUALIZAR.
        _temporizadorEstado = CrearTemporizadorEstado();

        _ = CargarCalendarioAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer? _temporizadorEstado;

    private System.Windows.Threading.DispatcherTimer? CrearTemporizadorEstado()
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted) return null;

        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(60),
            System.Windows.Threading.DispatcherPriority.Background,
            (s, e) => RefrescarEstadosEmitidos(),
            app.Dispatcher);
        timer.Start();
        return timer;
    }

    /// <summary>Revalúa y notifica el estado de emisión de todas las tarjetas visibles.</summary>
    public void RefrescarEstadosEmitidos()
    {
        foreach (var episodio in Lunes.Concat(Martes).Concat(Miercoles).Concat(Jueves)
                     .Concat(Viernes).Concat(Sabado).Concat(Domingo))
        {
            episodio.ActualizarEstadoEmitido();
        }
    }

    [RelayCommand]
    private async Task CargarCalendarioAsync()
    {
        // Evitar cargas concurrentes (doble click en ACTUALIZAR + navegación)
        if (!await _cargaLock.WaitAsync(0)) return;

        try
        {
            EstaCargando = true;
            LimpiarListas();

            // PERF-02: proyección ligera (sin Sinopsis) para la lista del calendario.
            var animes = await _databaseService.ObtenerAnimesLigerosAsync();
            foreach (var a in animes) a.ResolverPortadaLocal();
            TotalAnimesEnEmision = animes.Count(a => a.Estado.Equals("RELEASING", StringComparison.OrdinalIgnoreCase));
            
            // GroupBy: tolera AniListIds duplicados en la BD (ToDictionary lanzaría excepción
            // y dejaría el calendario cargando para siempre)
            var dicPortadas = animes
                .GroupBy(a => a.AniListId)
                .ToDictionary(g => g.Key, g => g.First().PortadaVisible);
            var ids = animes.Select(a => a.AniListId).Distinct().ToList();

            if (ids.Count == 0)
            {
                return;
            }

            // Calcular inicio y fin de la semana actual
            DateTime ahora = DateTime.Now;
            int diff = (7 + (ahora.DayOfWeek - DayOfWeek.Monday)) % 7;
            DateTime inicioSemana = ahora.AddDays(-1 * diff).Date;
            DateTime finSemana = inicioSemana.AddDays(7).AddTicks(-1);

            long timestampInicio = ((DateTimeOffset)inicioSemana).ToUnixTimeSeconds();
            long timestampFin = ((DateTimeOffset)finSemana).ToUnixTimeSeconds();

            var schedule = await _animeTrackingService.ObtenerCalendarioEmisionAsync(ids, timestampInicio, timestampFin);

            int animesConEmisionSemanal = schedule.Select(e => e.AniListId).Distinct().Count();
            if (animesConEmisionSemanal > TotalAnimesEnEmision)
            {
                TotalAnimesEnEmision = animesConEmisionSemanal;
            }

            foreach (var eps in schedule.OrderBy(e => e.FechaEmision))
            {
                if (dicPortadas.TryGetValue(eps.AniListId, out var portadaLocal) && !string.IsNullOrEmpty(portadaLocal))
                {
                    eps.UrlPortada = portadaLocal;
                }

                switch (eps.DiaSemana)
                {
                    case DayOfWeek.Monday: Lunes.Add(eps); break;
                    case DayOfWeek.Tuesday: Martes.Add(eps); break;
                    case DayOfWeek.Wednesday: Miercoles.Add(eps); break;
                    case DayOfWeek.Thursday: Jueves.Add(eps); break;
                    case DayOfWeek.Friday: Viernes.Add(eps); break;
                    case DayOfWeek.Saturday: Sabado.Add(eps); break;
                    case DayOfWeek.Sunday: Domingo.Add(eps); break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("CalendarioViewModel", "Error al cargar el calendario de emisión", ex);
        }
        finally
        {
            // Garantizar que el spinner nunca quede pegado
            EstaCargando = false;
            _cargaLock.Release();
        }
    }

    /// <summary>
    /// Indica si el calendario aún no muestra ningún dato (carga inicial fallida o sin datos).
    /// </summary>
    public bool EstaVacio =>
        Lunes.Count == 0 && Martes.Count == 0 && Miercoles.Count == 0 && Jueves.Count == 0 &&
        Viernes.Count == 0 && Sabado.Count == 0 && Domingo.Count == 0;

    /// <summary>
    /// Abre la ficha del anime directamente desde el calendario (clic en la tarjeta del
    /// episodio). Si el anime no está en la biblioteca local, avisa y sugiere añadirlo.
    /// </summary>
    [RelayCommand]
    private async Task AbrirAnimeAsync(AiringEpisode? episodio)
    {
        if (episodio?.AniListId is not > 0) return;

        try
        {
            var anime = await _databaseService.ObtenerAnimePorIdAsync(episodio.AniListId);
            if (anime == null)
            {
                _ = WeakReferenceMessenger.Default.Send(new MostrarDialogoRequestMessage(
                    "No está en tu biblioteca",
                    $"'{episodio.Titulo}' aún no está en tu biblioteca local.\n\nAñádelo desde la pestaña + para poder verlo o descargarlo.",
                    false,
                    "BookOpenPageVariantOutline",
                    "#60A5FA"));
                return;
            }

            anime.ResolverPortadaLocal();
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
        }
        catch (Exception ex)
        {
            AppLogger.Error("CalendarioViewModel", $"Error al abrir el anime '{episodio.Titulo}' desde el calendario", ex);
        }
    }

    private void LimpiarListas()
    {
        Lunes.Clear();
        Martes.Clear();
        Miercoles.Clear();
        Jueves.Clear();
        Viernes.Clear();
        Sabado.Clear();
        Domingo.Clear();
    }
}
