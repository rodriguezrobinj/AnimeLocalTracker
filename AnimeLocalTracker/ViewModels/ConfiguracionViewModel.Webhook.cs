using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Webhook;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Configuración → General → "Avisos en un canal de Discord". La URL del webhook es secreta: se guarda cifrada y nunca se vuelve
/// a mostrar (el campo queda vacío y una línea dice que hay una guardada). Ver docs/plan-webhook-discord.md.
/// </summary>
public partial class ConfiguracionViewModel
{
    private readonly IWebhookDiscordService? _webhook;

    [ObservableProperty] private string _urlWebhook = string.Empty;
    [ObservableProperty] private bool _hayWebhookGuardado;
    [ObservableProperty] private bool _webhookAvisarEmision = true;
    [ObservableProperty] private bool _webhookAvisarSerieTerminada = true;
    [ObservableProperty] private bool _probandoWebhook;

    private bool UrlWebhookEscritaInvalida => !string.IsNullOrWhiteSpace(UrlWebhook) && !EmbedWebhook.EsUrlValida(UrlWebhook);

    private Task AvisarUrlWebhookInvalida() => _dialogService.MostrarDialogoAsync(
        LocalizationService.T("Cfg_WebhookInvalidoTitulo"), LocalizationService.T("Cfg_WebhookInvalidoMsj"), false, "AlertCircleOutline", "#EF4444");

    /// <summary>Con el resto de ajustes, al pulsar "Guardar preferencias": una URL escrita sustituye a la guardada.</summary>
    private void GuardarUrlWebhookEscrita()
    {
        if (_webhook == null || string.IsNullOrWhiteSpace(UrlWebhook)) return;
        _webhook.GuardarUrl(UrlWebhook.Trim());
        UrlWebhook = string.Empty;
        HayWebhookGuardado = true;
    }

    [RelayCommand]
    private async Task ProbarWebhookAsync()
    {
        if (_webhook == null || ProbandoWebhook) return;
        string? url = string.IsNullOrWhiteSpace(UrlWebhook) ? _webhook.UrlGuardada : UrlWebhook.Trim();
        if (!EmbedWebhook.EsUrlValida(url))
        {
            await AvisarUrlWebhookInvalida();
            return;
        }

        ProbandoWebhook = true;
        bool ok = await _webhook.ProbarAsync(url!);
        ProbandoWebhook = false;

        await _dialogService.MostrarDialogoAsync(
            LocalizationService.T(ok ? "Cfg_WebhookPruebaOkTitulo" : "Cfg_WebhookPruebaErrorTitulo"),
            LocalizationService.T(ok ? "Cfg_WebhookPruebaOkMsj" : "Cfg_WebhookPruebaErrorMsj"),
            false, ok ? "CheckCircle" : "AlertCircleOutline", ok ? "#4CAF50" : "#EF4444");
    }

    [RelayCommand]
    private void QuitarWebhook()
    {
        _webhook?.GuardarUrl(null);
        UrlWebhook = string.Empty;
        HayWebhookGuardado = false;
    }
}
