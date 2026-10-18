using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Mal;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Configuración → Descargas y cuenta → MyAnimeList: conectar la cuenta, desconectarla y sincronizar ya. El grupo solo aparece
/// con el Client ID de la app configurado (ver docs/plan-sincronizacion-mal.md).
/// </summary>
public partial class ConfiguracionViewModel
{
    private readonly IMalAuthService? _malAuth;
    private readonly IMalApiClient? _malApi;
    private readonly IMalSyncService? _malSync;
    private string? _malUsuario;

    [ObservableProperty] private bool _malConectado;
    [ObservableProperty] private string _malEstadoTexto = string.Empty;
    [ObservableProperty] private bool _malConectando;
    [ObservableProperty] private bool _malSincronizando;
    [ObservableProperty] private string _malUltimaPasadaTexto = string.Empty;

    public bool MalDisponible => _malAuth?.Configurado == true;

    /// <summary>La lectura del nombre de usuario de MAL al abrir (para que las pruebas puedan esperarla).</summary>
    internal Task CargaMal { get; private set; } = Task.CompletedTask;

    private void CargarEstadoMal()
    {
        if (!MalDisponible) return;
        ActualizarEstadoMal();
        if (_malSync?.UltimaPasada is { } ultima) MostrarPasadaMal(ultima);
        if (_malSync != null)
        {
            // Las pasadas automáticas (cada 5 min) terminan en un hilo de fondo.
            _malSync.PasadaTerminada += r => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => MostrarPasadaMal(r));
        }
        if (MalConectado) CargaMal = CargarUsuarioMalAsync();
    }

    private void ActualizarEstadoMal()
    {
        MalConectado = _malAuth?.Conectado == true;
        MalEstadoTexto = !MalConectado ? LocalizationService.T("Cfg_MalSinConectar")
            : _malUsuario is { } usuario ? string.Format(LocalizationService.T("Cfg_MalConectadoComoFormato"), usuario)
            : LocalizationService.T("Cfg_MalConectado");
    }

    private async Task CargarUsuarioMalAsync()
    {
        if (_malApi == null) return;
        _malUsuario = await _malApi.UsuarioAsync(CancellationToken.None);
        ActualizarEstadoMal();
    }

    private void MostrarPasadaMal(ResumenSincronizacionMal resumen)
        => MalUltimaPasadaTexto = string.Format(LocalizationService.T("Cfg_MalUltimaFormato"),
               resumen.FinUtc.ToLocalTime().ToString("t", LocalizationService.Cultura), resumen.Enviados)
           + (resumen.Completa ? string.Empty : " · " + LocalizationService.T("Cfg_MalIncompleta"));

    [RelayCommand]
    private async Task ConectarMalAsync()
    {
        if (_malAuth == null || MalConectando) return;

        MalConectando = true;
        MalEstadoTexto = LocalizationService.T("Cfg_MalEsperandoNavegador");
        bool ok = await _malAuth.ConectarAsync(CancellationToken.None);
        MalConectando = false;

        if (!ok)
        {
            ActualizarEstadoMal();
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Cfg_MalErrorConectarTitulo"),
                LocalizationService.T("Cfg_MalErrorConectarMsj"), false, "AlertCircleOutline", "#EF4444");
            return;
        }

        _malUsuario = null;
        ActualizarEstadoMal();
        if (_malSync != null) await _malSync.OlvidarEnviadosAsync(); // cuenta nueva: se vuelve a revisar todo
        await CargarUsuarioMalAsync();
        await SincronizarMalAsync(); // la primera pasada envía la biblioteca (sin bajar nada de lo que MAL ya tenga)
    }

    [RelayCommand]
    private void DesconectarMal()
    {
        _malAuth?.Desconectar();
        _malUsuario = null;
        MalUltimaPasadaTexto = string.Empty;
        ActualizarEstadoMal();
    }

    /// <summary>Al cambiar de idioma: los textos del grupo de MAL se componen en código.</summary>
    private void RefrescarTextosMal()
    {
        if (!MalDisponible) return;
        if (!MalConectando) ActualizarEstadoMal();
        if (MalUltimaPasadaTexto.Length > 0 && _malSync?.UltimaPasada is { } ultima) MostrarPasadaMal(ultima);
    }

    [RelayCommand]
    private async Task SincronizarMalAsync()
    {
        if (_malSync == null || MalSincronizando) return;

        MalSincronizando = true;
        var resumen = await _malSync.SincronizarAhoraAsync(CancellationToken.None);
        MalSincronizando = false;
        if (resumen != null) MostrarPasadaMal(resumen);
    }
}
