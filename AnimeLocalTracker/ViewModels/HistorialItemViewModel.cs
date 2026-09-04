using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

public partial class HistorialItemViewModel : ObservableObject
{
    public int AniListId { get; init; }
    public int NumeroEpisodio { get; init; }
    public string TituloAnime { get; init; } = string.Empty;
    public string TituloEpisodio { get; init; } = string.Empty;
    public string RutaArchivo { get; init; } = string.Empty;
    public string? RutaMiniatura { get; init; }
    public string? RutaPortada { get; init; }
    public string? Resolucion { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(EnProgreso))]
    private double _progresoSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(EnProgreso))]
    private double _totalSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    [NotifyPropertyChangedFor(nameof(EnProgreso))]
    private bool _vistoLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FechaRelativaTexto))]
    [NotifyPropertyChangedFor(nameof(GrupoTemporal))]
    private DateTime? _ultimaReproduccion;

    public bool ExisteArchivoLocal => !string.IsNullOrWhiteSpace(RutaArchivo) && File.Exists(RutaArchivo);

    /// <summary>
    /// Miniatura si existe en disco; si no, la portada. Se resuelve UNA vez al construir
    /// el ítem (el File.Exists por evaluación de binding era IO en el hilo de UI).
    /// </summary>
    public string? RutaImagenMostrar { get; init; }

    public double PorcentajeProgreso =>
        TotalSegundos > 0 ? Math.Clamp(ProgresoSegundos / TotalSegundos, 0.0, 1.0) : 0.0;

    public bool EnProgreso =>
        !VistoLocal && ProgresoSegundos > 5 && (TotalSegundos <= 0 || ProgresoSegundos < TotalSegundos * 0.95);

    public string ProgresoFormateado
    {
        get
        {
            if (VistoLocal && ProgresoSegundos <= 0)
            {
                if (TotalSegundos > 0)
                {
                    var tTot = TimeSpan.FromSeconds(TotalSegundos);
                    string totStr = tTot.ToString(tTot.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                    return $"{LocalizationService.T("Hist_FiltroCompletados")} ({totStr})";
                }
                return LocalizationService.T("Hist_FiltroCompletados");
            }

            if (ProgresoSegundos <= 0) return "00:00";
            var tCur = TimeSpan.FromSeconds(ProgresoSegundos);
            string curStr = tCur.ToString(tCur.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
            if (TotalSegundos > 0)
            {
                var tTot = TimeSpan.FromSeconds(TotalSegundos);
                string totStr = tTot.ToString(tTot.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                return $"{curStr} / {totStr}";
            }
            return curStr;
        }
    }

    public string FechaRelativaTexto =>
        UltimaReproduccion.HasValue
            ? CalcularFechaRelativa(UltimaReproduccion.Value)
            : string.Empty;

    public string GrupoTemporal =>
        UltimaReproduccion.HasValue
            ? CalcularGrupoTemporal(UltimaReproduccion.Value)
            : LocalizationService.T("Hist_FechaAnteriores");

    public static string CalcularFechaRelativa(DateTime fechaGuardada)
    {
        // La BD devuelve las fechas (siempre guardadas en UTC) con Kind "Unspecified":
        // hay que tratarlas como UTC explícitamente para convertirlas a hora local.
        var local = fechaGuardada.Kind == DateTimeKind.Local ? fechaGuardada : fechaGuardada.ToLocalTime();
        var ahora = DateTime.Now;
        var diferencia = ahora - local;

        if (diferencia.TotalMinutes < 1)
            return $"{LocalizationService.T("Hist_FechaHoy")} ({local:HH:mm})";
        if (diferencia.TotalHours < 1)
            return string.Format(LocalizationService.T("Hist_HaceMin"), (int)Math.Max(1, diferencia.TotalMinutes));
        if (local.Date == ahora.Date)
            return $"{LocalizationService.T("Hist_FechaHoy")} {local:HH:mm}";
        if (local.Date == ahora.Date.AddDays(-1))
            return $"{LocalizationService.T("Hist_FechaAyer")} {local:HH:mm}";
        if (diferencia.TotalDays < 7)
            return $"{local:dddd} {local:HH:mm}";

        return local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string CalcularGrupoTemporal(DateTime fechaGuardada)
    {
        var local = fechaGuardada.Kind == DateTimeKind.Local ? fechaGuardada : fechaGuardada.ToLocalTime();
        var hoy = DateTime.Today;
        if (local.Date == hoy) return LocalizationService.T("Hist_FechaHoy");
        if (local.Date == hoy.AddDays(-1)) return LocalizationService.T("Hist_FechaAyer");
        if (local.Date >= hoy.AddDays(-7)) return LocalizationService.T("Hist_FechaSemana");
        if (local.Date >= hoy.AddDays(-30)) return LocalizationService.T("Hist_FechaMes");
        return LocalizationService.T("Hist_FechaAnteriores");
    }
}
