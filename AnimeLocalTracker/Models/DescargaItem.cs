using System;
using System.Collections.Generic;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

public partial class DescargaItem : ObservableObject
{
    public int AniListId { get; set; }
    public string AnimeTitulo { get; set; } = string.Empty;
    public int NumeroEpisodio { get; set; }
    public string Fuente { get; set; } = "AnimeAv1";
    public string RutaArchivo { get; set; } = string.Empty;
    public long Orden { get; set; }

    public string TituloEpisodio => string.Format(LocalizationService.T("Act_EpisodioFormato"), NumeroEpisodio);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoTexto))]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(EstadoTexto))]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private double _progreso;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgresoTexto))]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(EstadoTexto))]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private bool _isPaused;

    /// <summary>Espera un slot libre (todavía no transfiere datos).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaEnEspera))]
    [NotifyPropertyChangedFor(nameof(EstadoTexto))]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private bool _enCola;

    /// <summary>Reintentos automáticos por cortes de red.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstadoTexto))]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private int _reintentos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private string _velocidadDescarga = string.Empty;

    /// <summary>Velocidad en bytes/s (para sumar la velocidad total en el resumen).</summary>
    public double VelocidadBps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetalleTexto))]
    private string _tiempoRestante = string.Empty;

    private DateTime _marcaAnterior = DateTime.MinValue;
    private double _progresoAnterior;
    private double _tasaSuavizada; // % por segundo

    partial void OnProgresoChanged(double value)
    {
        var ahora = DateTime.UtcNow;
        if (_marcaAnterior != DateTime.MinValue && value > _progresoAnterior)
        {
            double dt = (ahora - _marcaAnterior).TotalSeconds;
            if (dt >= 0.5)
            {
                double tasa = (value - _progresoAnterior) / dt;
                _tasaSuavizada = _tasaSuavizada <= 0 ? tasa : _tasaSuavizada * 0.8 + tasa * 0.2;
                _marcaAnterior = ahora;
                _progresoAnterior = value;
                TiempoRestante = _tasaSuavizada > 0 && value < 100
                    ? FormatearDuracion(TimeSpan.FromSeconds((100 - value) / _tasaSuavizada))
                    : string.Empty;
            }
        }
        else
        {
            _marcaAnterior = ahora;
            _progresoAnterior = value;
            if (value <= 0)
            {
                _tasaSuavizada = 0;
                TiempoRestante = string.Empty;
            }
        }
    }

    /// <summary>Al pausar o reanudar, la estimación anterior deja de valer.</summary>
    partial void OnIsPausedChanged(bool value)
    {
        _marcaAnterior = DateTime.MinValue;
        _tasaSuavizada = 0;
        TiempoRestante = string.Empty;
    }

    internal static string FormatearDuracion(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds:D2} s";
        return $"{Math.Max(1, (int)t.TotalSeconds)} s";
    }

    /// <summary>Porcentaje ("42%"); vacío mientras espera.</summary>
    public string ProgresoTexto => IsPaused || Progreso > 0 ? $"{Progreso:F0}%" : string.Empty;

    public bool EstaEnEspera => Progreso <= 0.0 && !IsPaused;

    public string EstadoTexto =>
        IsPaused ? LocalizationService.T("Desc_EnPausa")
        : EnCola ? LocalizationService.T("Desc_EstadoEnCola")
        : Reintentos > 0 && Progreso <= 0 ? string.Format(LocalizationService.T("Desc_ReintentoFormato"), Reintentos)
        : LocalizationService.T("Desc_EstadoDescargando");

    /// <summary>Segunda línea: velocidad · tiempo restante · reintentos.</summary>
    public string DetalleTexto
    {
        get
        {
            if (IsPaused || EnCola) return string.Empty;
            var partes = new List<string>(3);
            if (!string.IsNullOrEmpty(VelocidadDescarga)) partes.Add(VelocidadDescarga);
            if (!string.IsNullOrEmpty(TiempoRestante)) partes.Add(string.Format(LocalizationService.T("Desc_EtaFormato"), TiempoRestante));
            if (Reintentos > 0 && Progreso > 0) partes.Add(string.Format(LocalizationService.T("Desc_ReintentoFormato"), Reintentos));
            return string.Join("  ·  ", partes);
        }
    }

    [ObservableProperty]
    private bool _isDownloading = true;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private string? _error;
}
