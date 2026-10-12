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
    [ObservableProperty] private bool _toastDelReproductor;

    // Viaja con el flujo asíncrono (también a través de los await de otros servicios): ver AvisosComoDelReproductor.
    private static readonly System.Threading.AsyncLocal<bool> _flujoDelReproductor = new();

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

    public void MostrarToast(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5") =>
        MostrarToastConOrigen(titulo, mensaje, icono, color, _flujoDelReproductor.Value);

    public void MostrarToastReproductor(string titulo, string mensaje, string icono = "InformationOutline", string color = "#3F51B5") =>
        MostrarToastConOrigen(titulo, mensaje, icono, color, delReproductor: true);

    public IDisposable AvisosComoDelReproductor()
    {
        bool anterior = _flujoDelReproductor.Value;
        _flujoDelReproductor.Value = true;
        return new Restaurar(() => _flujoDelReproductor.Value = anterior);
    }

    private sealed class Restaurar(Action accion) : IDisposable
    {
        public void Dispose() => accion();
    }

    private void MostrarToastConOrigen(string titulo, string mensaje, string icono, string color, bool delReproductor)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => MostrarToastInterno(titulo, mensaje, icono, color, delReproductor));
            return;
        }
        
        MostrarToastInterno(titulo, mensaje, icono, color, delReproductor);
    }

    /// <summary>Cuánto dura un aviso en pantalla. Las pruebas lo acortan.</summary>
    internal TimeSpan DuracionToast { get; set; } = TimeSpan.FromMilliseconds(3500);

    // Solo hay un hueco de aviso. Cada aviso que lo ocupa lleva su número: el temporizador de uno anterior ya no manda.
    private int _versionToast;
    // ponytail: un solo aviso de fuera en espera (el último). Si llegan varios mientras se ve uno del reproductor, los anteriores
    // se pierden, como se perdían antes al pisarse; hace falta una cola si eso llega a notarse.
    private (string Titulo, string Mensaje, string Icono, string Color)? _toastEnEspera;

    private void MostrarToastInterno(string titulo, string mensaje, string icono, string color, bool delReproductor)
    {
        // Un aviso de fuera (una descarga que termina, un episodio nuevo en emisión) no le quita el sitio al del reproductor que se
        // está viendo: antes lo cortaba al instante, y durante un episodio ni siquiera se veía qué lo había cortado. Espera su turno.
        if (ToastVisible && ToastDelReproductor && !delReproductor)
        {
            _toastEnEspera = (titulo, mensaje, icono, color);
            return;
        }
        OcuparElHueco(titulo, mensaje, icono, color, delReproductor);
    }

    private void OcuparElHueco(string titulo, string mensaje, string icono, string color, bool delReproductor)
    {
        ToastDelReproductor = delReproductor;
        ToastTitulo = titulo;
        ToastMensaje = mensaje;
        ToastIcono = icono;
        ToastColor = color;
        ToastVisible = true;

        // Ocultar automáticamente
        int version = ++_versionToast;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DuracionToast);
                Core.HiloUi.Ejecutar(() => AlCaducarElToast(version));
            }
            catch (Exception ex)
            {
                AppLogger.Debug("DialogService", $"Error al ocultar toast: {ex.Message}");
            }
        });
    }

    private void AlCaducarElToast(int version)
    {
        // Mientras tanto llegó otro aviso: este temporizador era del anterior. Sin esto, un aviso que llegaba a los 3 s de otro
        // solo se veía medio segundo.
        if (version != _versionToast) return;

        if (_toastEnEspera is { } enEspera)
        {
            _toastEnEspera = null;
            OcuparElHueco(enEspera.Titulo, enEspera.Mensaje, enEspera.Icono, enEspera.Color, delReproductor: false);
            return;
        }
        ToastVisible = false;
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
