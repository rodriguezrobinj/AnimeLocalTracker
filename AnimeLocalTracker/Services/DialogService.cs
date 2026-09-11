using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.Services;

public partial class DialogService : ObservableObject, IDialogService
{
    // === DIÁLOGOS CUSTOM ===
    [ObservableProperty] private bool _dialogoVisible;
    [ObservableProperty] private string _dialogoTitulo = "";
    [ObservableProperty] private string _dialogoMensaje = "";
    [ObservableProperty] private bool _dialogoEsConfirmacion;
    [ObservableProperty] private string _dialogoIcono = "InformationOutline";
    [ObservableProperty] private string _dialogoColor = "#3F51B5";
    
    private TaskCompletionSource<bool>? _dialogTcs;

    // === TOAST NOTIFICATIONS ===
    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private string _toastTitulo = "";
    [ObservableProperty] private string _toastMensaje = "";
    [ObservableProperty] private string _toastIcono = "InformationOutline";
    [ObservableProperty] private string _toastColor = "#3F51B5";

    public async Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        if (!esConfirmacion)
        {
            MostrarToast(titulo, mensaje, icono, color);
            return true;
        }

        // Ejecutar en el hilo de UI
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            return await System.Windows.Application.Current.Dispatcher.Invoke(() => MostrarDialogoInternoAsync(titulo, mensaje, esConfirmacion, icono, color));
        }
        else
        {
            return await MostrarDialogoInternoAsync(titulo, mensaje, esConfirmacion, icono, color);
        }
    }

    private async Task<bool> MostrarDialogoInternoAsync(string titulo, string mensaje, bool esConfirmacion, string icono, string color)
    {
        DialogoTitulo = titulo;
        DialogoMensaje = mensaje;
        DialogoEsConfirmacion = esConfirmacion;
        DialogoIcono = icono;
        DialogoColor = color;
        
        DialogoVisible = true;
        
        _dialogTcs = new TaskCompletionSource<bool>();
        return await _dialogTcs.Task;
    }

    public void MostrarToast(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5")
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => MostrarToastInterno(titulo, mensaje, icono, color));
            return;
        }
        
        MostrarToastInterno(titulo, mensaje, icono, color);
    }

    private void MostrarToastInterno(string titulo, string mensaje, string icono, string color)
    {
        ToastTitulo = titulo;
        ToastMensaje = mensaje;
        ToastIcono = icono;
        ToastColor = color;
        ToastVisible = true;
        
        // Ocultar automáticamente
        _ = Task.Run(async () => 
        {
            try
            {
                await Task.Delay(3500);
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => ToastVisible = false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DialogService", $"Error al ocultar toast: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void AceptarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(false);
    }
}
