using System;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// En qué punto está un episodio nuevo. De él salen el texto, el color, el icono y la acción principal
/// de la tarjeta, así el usuario ve de un vistazo QUÉ puede hacer con cada episodio.
/// </summary>
public enum EstadoActualizacion
{
    SinDescargar,
    Descargando,
    ListoParaVer,
    EnProgreso,
    Visto
}

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
    [NotifyPropertyChangedFor(nameof(Estado))]
    [NotifyPropertyChangedFor(nameof(PorDescargar))]
    [NotifyPropertyChangedFor(nameof(ListoParaVer))]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    private bool _descargado;

    [ObservableProperty]
    private string _rutaArchivo = string.Empty;

    [ObservableProperty]
    private string _tamanoArchivoFormateado = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    [NotifyPropertyChangedFor(nameof(Estado))]
    [NotifyPropertyChangedFor(nameof(SinVer))]
    [NotifyPropertyChangedFor(nameof(ListoParaVer))]
    private bool _visto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    [NotifyPropertyChangedFor(nameof(Estado))]
    private double _progresoSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PorcentajeProgreso))]
    [NotifyPropertyChangedFor(nameof(TieneProgresoGuardado))]
    [NotifyPropertyChangedFor(nameof(ProgresoFormateado))]
    [NotifyPropertyChangedFor(nameof(Estado))]
    private double _totalSegundos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    [NotifyPropertyChangedFor(nameof(Estado))]
    [NotifyPropertyChangedFor(nameof(PorDescargar))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(ProgresoDescargaActivo))]
    [NotifyPropertyChangedFor(nameof(Estado))]
    private double _downloadProgress;

    public bool EstaEnEspera => IsDownloading && DownloadProgress <= 0.0;
    public bool ProgresoDescargaActivo => IsDownloading && DownloadProgress > 0.0;

    // === Progreso de reproducción (igual que EpisodioItem en la Ficha) ===
    public double PorcentajeProgreso => TotalSegundos > 0 ? Math.Clamp(ProgresoSegundos / TotalSegundos, 0.0, 1.0) : 0.0;

    // FUN-003/FUN-010: mismo umbral de "terminado" que en la Ficha (90%).
    public bool TieneProgresoGuardado => ProgresoSegundos > 5 && !Visto && (TotalSegundos <= 0 || ProgresoSegundos < TotalSegundos * 0.90);

    public string ProgresoFormateado
    {
        get
        {
            if (ProgresoSegundos <= 0) return string.Empty;
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

    public string EpisodioTexto => string.Format(LocalizationService.T("Act_EpisodioFormato"), NumeroEpisodio);

    /// <summary>Etiqueta corta de la píldora de la tarjeta: "EP 12".</summary>
    public string EpisodioCorto => string.Format(LocalizationService.T("Act_EpisodioCortoFormato"), NumeroEpisodio);

    // === Estado y acción principal ===

    /// <summary>Filtros y contadores de la pestaña: pendiente de descargar / sin ver / listo para ver.</summary>
    public bool PorDescargar => !Descargado;
    public bool SinVer => !Visto;
    public bool ListoParaVer => Descargado && !Visto;

    public EstadoActualizacion Estado =>
        IsDownloading ? EstadoActualizacion.Descargando
        : Visto ? EstadoActualizacion.Visto
        : Descargado ? (TieneProgresoGuardado ? EstadoActualizacion.EnProgreso : EstadoActualizacion.ListoParaVer)
        : EstadoActualizacion.SinDescargar;

    public string EstadoTexto => Estado switch
    {
        EstadoActualizacion.Descargando => DownloadProgress > 0
            ? string.Format(LocalizationService.T("Act_Estado_DescargandoFormato"), (int)Math.Round(DownloadProgress))
            : LocalizationService.T("Act_Estado_EnCola"),
        EstadoActualizacion.Visto => LocalizationService.T("Act_Estado_Visto"),
        EstadoActualizacion.EnProgreso => LocalizationService.T("Act_Estado_EnProgreso"),
        EstadoActualizacion.ListoParaVer => LocalizationService.T("Act_Estado_Listo"),
        _ => LocalizationService.T("Act_Estado_SinDescargar")
    };

    // Solo colores ya presentes en la paleta de la app.
    public string EstadoColor => Estado switch
    {
        EstadoActualizacion.Descargando => "#60A5FA",
        EstadoActualizacion.Visto => "#A78BFA",
        EstadoActualizacion.EnProgreso => "#FBBF24",
        EstadoActualizacion.ListoParaVer => "#34D399",
        _ => "#94A3B8"
    };

    public string EstadoIcono => Estado switch
    {
        EstadoActualizacion.Descargando => "ProgressClock",
        EstadoActualizacion.Visto => "EyeCheckOutline",
        EstadoActualizacion.EnProgreso => "PlayCircleOutline",
        EstadoActualizacion.ListoParaVer => "CheckCircleOutline",
        _ => "CloudDownloadOutline"
    };

    /// <summary>Verbo del botón principal: Descargar / Ver ahora / Continuar / Ver de nuevo.</summary>
    public string AccionTexto => Estado switch
    {
        EstadoActualizacion.Visto => LocalizationService.T(Descargado ? "Act_Accion_VerDeNuevo" : "Act_Descargar"),
        EstadoActualizacion.EnProgreso => LocalizationService.T("Act_Accion_Continuar"),
        EstadoActualizacion.ListoParaVer => LocalizationService.T("Act_Accion_VerAhora"),
        _ => LocalizationService.T("Act_Descargar")
    };

    public string AccionIcono => Estado switch
    {
        EstadoActualizacion.Visto => Descargado ? "Replay" : "Download",
        EstadoActualizacion.EnProgreso or EstadoActualizacion.ListoParaVer => "Play",
        _ => "Download"
    };

    /// <summary>La acción principal destaca (relleno) salvo cuando el episodio ya está visto: ahí es secundaria.</summary>
    public bool AccionEsPrimaria => Estado != EstadoActualizacion.Visto;

    /// <summary>Mientras se descarga no hay botón: se muestra el círculo de progreso.</summary>
    public bool MostrarAccion => Estado != EstadoActualizacion.Descargando;

    public bool MostrarAccionPrimaria => MostrarAccion && AccionEsPrimaria;
    public bool MostrarAccionSecundaria => MostrarAccion && !AccionEsPrimaria;

    /// <summary>Vuelve a leer todos los textos localizados (al cambiar de idioma con la pestaña ya cargada).</summary>
    public void RefrescarTextos() => OnPropertyChanged(string.Empty);

    /// <summary>Todo lo que se deriva de <see cref="Estado"/> se refresca junto con él.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Estado)) return;

        OnPropertyChanged(nameof(EstadoTexto));
        OnPropertyChanged(nameof(EstadoColor));
        OnPropertyChanged(nameof(EstadoIcono));
        OnPropertyChanged(nameof(AccionTexto));
        OnPropertyChanged(nameof(AccionIcono));
        OnPropertyChanged(nameof(AccionEsPrimaria));
        OnPropertyChanged(nameof(MostrarAccion));
        OnPropertyChanged(nameof(MostrarAccionPrimaria));
        OnPropertyChanged(nameof(MostrarAccionSecundaria));
    }

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
