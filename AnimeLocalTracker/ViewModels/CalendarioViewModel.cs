using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

public partial class CalendarioViewModel : ObservableObject
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;

    [ObservableProperty] private bool _estaCargando;
    [ObservableProperty] private int _totalAnimesEnEmision;

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
        
        _ = CargarCalendarioAsync();
    }

    [RelayCommand]
    private async Task CargarCalendarioAsync()
    {
        EstaCargando = true;
        LimpiarListas();

        var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
        TotalAnimesEnEmision = animes.Count(a => a.Estado.Equals("RELEASING", StringComparison.OrdinalIgnoreCase));
        
        var dicPortadas = animes.ToDictionary(a => a.AniListId, a => a.PortadaVisible);
        var ids = animes.Select(a => a.AniListId).ToList();

        if (ids.Count == 0)
        {
            EstaCargando = false;
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

        EstaCargando = false;
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
