using System;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Ítem del feed de actualizaciones: un episodio recién emitido de un anime de tu
/// biblioteca que aún no tienes descargado, con estado de descarga en vivo.
/// </summary>
public partial class ActualizacionItemViewModel : ObservableObject
{
    public int AniListId { get; init; }
    public string TituloAnime { get; init; } = string.Empty;
    public int NumeroEpisodio { get; init; }
    public string RutaCarpeta { get; init; } = string.Empty;
    public string RutaPortada { get; init; } = string.Empty;
    public DateTime FechaEmision { get; init; }

    [ObservableProperty]
    private bool _descargado;

    [ObservableProperty]
    private string _rutaArchivo = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    private double _downloadProgress;

    public bool EstaEnEspera => IsDownloading && DownloadProgress <= 0.0;
    public bool ProgresoDescargaActivo => IsDownloading && DownloadProgress > 0.0;

    public string EpisodioTexto => string.Format(LocalizationService.T("Act_EpisodioFormato"), NumeroEpisodio);

    public string FechaTexto
    {
        get
        {
            var local = FechaEmision.Kind == DateTimeKind.Local ? FechaEmision : FechaEmision.ToLocalTime();
            var diferencia = DateTime.Now - local;
            if (diferencia.TotalMinutes < 1) return LocalizationService.T("Hist_FechaHoy");
            if (diferencia.TotalHours < 1) return string.Format(LocalizationService.T("Hist_HaceMin"), (int)Math.Max(1, diferencia.TotalMinutes));
            if (local.Date == DateTime.Today) return $"{LocalizationService.T("Hist_FechaHoy")} {local:HH:mm}";
            if (local.Date == DateTime.Today.AddDays(-1)) return $"{LocalizationService.T("Hist_FechaAyer")} {local:HH:mm}";
            var dias = (DateTime.Today - local.Date).Days;
            return string.Format(LocalizationService.T("Act_HaceDias"), Math.Max(2, dias));
        }
    }

    /// <summary>Cabecera de agrupación por fecha: Hoy / Ayer / Hace N días.</summary>
    public string GrupoTemporal
    {
        get
        {
            var local = FechaEmision.Kind == DateTimeKind.Local ? FechaEmision : FechaEmision.ToLocalTime();
            var dias = (DateTime.Today - local.Date).Days;
            if (dias <= 0) return LocalizationService.T("Hist_FechaHoy");
            if (dias == 1) return LocalizationService.T("Hist_FechaAyer");
            return string.Format(LocalizationService.T("Act_HaceDias"), dias);
        }
    }
}
