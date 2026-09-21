using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Cuenta atrás hasta el próximo episodio de un anime en emisión. El contador corre en el equipo (cada segundo, sin red);
/// los datos vienen de la copia local de <see cref="IProximaEmisionService"/>, que solo pregunta a AniList cuando
/// puede haber cambiado la programación.
/// </summary>
public partial class DetalleViewModel
{
    private IProximaEmisionService? _proximaEmision;
    private ProximaEmision? _proximaEmisionActual;
    private DispatcherTimer? _timerContador;
    private bool _refrescandoProximaEmision;
    private bool _forzarProximaEmision;
    private DateTime _ultimoIntentoTrasEmisionUtc = DateTime.MinValue;

    [ObservableProperty] private bool _tieneContadorProximo;
    [ObservableProperty] private string _contadorProximoTexto = string.Empty;
    [ObservableProperty] private string _contadorProximoTooltip = string.Empty;

    /// <summary>Estado limpio al cargar otro anime: sin contador ni temporizador de la ficha anterior.</summary>
    private void ReiniciarContadorProximo()
    {
        _proximaEmisionActual = null;
        TieneContadorProximo = false;
        ContadorProximoTexto = string.Empty;
        ContadorProximoTooltip = string.Empty;
        DetenerContador();
    }

    private async Task CargarProximaEmisionAsync()
    {
        var servicio = _proximaEmision;
        var anime = AnimeSeleccionado;
        if (servicio == null || anime == null || _refrescandoProximaEmision) return;

        bool forzar = _forzarProximaEmision;
        _forzarProximaEmision = false;
        _refrescandoProximaEmision = true;
        try
        {
            var proxima = await servicio.ObtenerAsync(anime.AniListId, anime.Estado, forzar);
            if (!ReferenceEquals(anime, AnimeSeleccionado)) return; // el usuario ya cambió de anime

            _proximaEmisionActual = proxima;
            ActualizarContadorProximo();
            if (proxima != null) ReanudarContador();
            else DetenerContador();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo cargar la próxima emisión: {ex.Message}");
        }
        finally
        {
            _refrescandoProximaEmision = false;
        }
    }

    /// <summary>Arranca el temporizador de 1 s (solo si hay una emisión programada). Idempotente.</summary>
    public void ReanudarContador()
    {
        if (_proximaEmisionActual == null || Application.Current == null) return;

        _timerContador ??= CrearTimer();
        if (!_timerContador.IsEnabled) _timerContador.Start();
    }

    /// <summary>Detiene el temporizador (al salir de la ficha): no debe seguir corriendo con la vista oculta.</summary>
    public void DetenerContador() => _timerContador?.Stop();

    private DispatcherTimer CrearTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => AlTickContador();
        return timer;
    }

    private void AlTickContador()
    {
        ActualizarContadorProximo();

        // Pasada la hora de emisión: se pide el siguiente episodio. El servicio limita las consultas reales a AniList
        // (mínimo 15 min entre intentos); aquí solo se evita preguntarle al servicio en cada segundo.
        var proxima = _proximaEmisionActual;
        if (proxima != null && proxima.EmisionUtc <= DateTime.UtcNow && DateTime.UtcNow - _ultimoIntentoTrasEmisionUtc > TimeSpan.FromMinutes(2))
        {
            _ultimoIntentoTrasEmisionUtc = DateTime.UtcNow;
            _ = CargarProximaEmisionAsync();
        }
    }

    internal void ActualizarContadorProximo()
    {
        var proxima = _proximaEmisionActual;
        if (proxima == null)
        {
            TieneContadorProximo = false;
            return;
        }

        var restante = proxima.EmisionUtc - DateTime.UtcNow;
        ContadorProximoTexto = restante > TimeSpan.Zero
            ? string.Format(LocalizationService.T("Det_ProximoEnFormato"), proxima.Episodio, FormatearCuentaAtras(restante))
            : string.Format(LocalizationService.T("Det_ProximoDisponibleFormato"), proxima.Episodio);
        ContadorProximoTooltip = string.Format(LocalizationService.T("Det_ProximoTooltipFormato"), proxima.Episodio,
            proxima.EmisionUtc.ToLocalTime().ToString("f", LocalizationService.Cultura));
        TieneContadorProximo = true;
    }

    /// <summary>"3 d 5 h" a partir de un día; "05:12:33" cuando falta menos de un día.</summary>
    internal static string FormatearCuentaAtras(TimeSpan restante)
    {
        if (restante < TimeSpan.Zero) restante = TimeSpan.Zero;
        if (restante.TotalDays >= 1)
        {
            return string.Format(LocalizationService.T("Det_ProximoDiasHoras"), (int)restante.TotalDays, restante.Hours);
        }
        return $"{(int)restante.TotalHours:D2}:{restante.Minutes:D2}:{restante.Seconds:D2}";
    }
}
