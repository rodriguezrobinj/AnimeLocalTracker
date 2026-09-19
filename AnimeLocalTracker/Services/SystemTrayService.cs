using System;
using System.ComponentModel;
using System.Windows;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class SystemTrayService : ISystemTrayService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private Window? _ventana;
    private bool _saliendoRealmente;

    public event EventHandler? ReanudarUltimoAnimeSolicitado;
    public event EventHandler? BuscarNuevosEpisodiosSolicitado;

    public SystemTrayService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsService.ConfiguracionModificada += OnConfiguracionModificada;
    }

    public void Habilitar(Window ventanaPrincipal)
    {
        _ventana = ventanaPrincipal;
        _ventana.Closing += OnClosing;
        _ventana.StateChanged += OnStateChanged;
        ActualizarVisibilidadIcono();
    }

    private void OnConfiguracionModificada(AppSettings _) => ActualizarVisibilidadIcono();

    private void ActualizarVisibilidadIcono()
    {
        bool activo = _settingsService.ObtenerConfiguracion()?.MinimizarABandejaAlCerrar ?? false;
        if (activo && _notifyIcon == null)
        {
            CrearIcono();
        }
        else if (!activo && _notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    private void CrearIcono()
    {
        System.Drawing.Icon icono;
        try
        {
            icono = (Environment.ProcessPath != null ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null)
                    ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icono = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(LocalizationService.T("Tray_AbrirApp"), null, (_, _) => RestaurarVentana());
        menu.Items.Add(LocalizationService.T("Tray_ReanudarUltimo"), null, (_, _) =>
        {
            RestaurarVentana();
            ReanudarUltimoAnimeSolicitado?.Invoke(this, EventArgs.Empty);
        });
        menu.Items.Add(LocalizationService.T("Tray_BuscarNuevos"), null, (_, _) =>
            BuscarNuevosEpisodiosSolicitado?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(LocalizationService.T("Tray_Salir"), null, (_, _) => SalirDeVerdad());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icono,
            Text = LocalizationService.T("Tray_Titulo"),
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => RestaurarVentana();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saliendoRealmente) return;
        if (_settingsService.ObtenerConfiguracion()?.MinimizarABandejaAlCerrar == true)
        {
            e.Cancel = true;
            _ventana?.Hide();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_ventana?.WindowState == WindowState.Minimized
            && _settingsService.ObtenerConfiguracion()?.MinimizarABandejaAlCerrar == true)
        {
            _ventana.Hide();
        }
    }

    private void RestaurarVentana()
    {
        if (_ventana == null) return;
        _ventana.Show();
        _ventana.WindowState = WindowState.Normal;
        _ventana.Activate();
    }

    public void MostrarNotificacion(string titulo, string mensaje)
    {
        _notifyIcon?.ShowBalloonTip(4000, titulo, mensaje, System.Windows.Forms.ToolTipIcon.Info);
    }

    private void SalirDeVerdad()
    {
        _saliendoRealmente = true;
        if (_notifyIcon != null) _notifyIcon.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _settingsService.ConfiguracionModificada -= OnConfiguracionModificada;
        if (_ventana != null)
        {
            _ventana.Closing -= OnClosing;
            _ventana.StateChanged -= OnStateChanged;
        }
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
