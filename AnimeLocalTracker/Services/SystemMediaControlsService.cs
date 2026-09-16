using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Media;
using Windows.Storage.Streams;

namespace AnimeLocalTracker.Services;

/// <summary>
/// SMT-01: implementación real de <see cref="ISystemMediaControlsService"/> con
/// Windows.Media.SystemMediaTransportControls (WinRT). Las apps de escritorio "clásicas"
/// (no UWP, no empaquetadas MSIX) no pueden llamar a SystemMediaTransportControls.GetForCurrentView()
/// porque requiere una CoreWindow que no existe fuera de UWP; la alternativa documentada por
/// Microsoft es el COM de interop ISystemMediaTransportControlsInterop::GetForWindow, obtenido con
/// un HWND propio — ver "WinRT APIs not supported in desktop apps" (learn.microsoft.com).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public class SystemMediaControlsService : ISystemMediaControlsService
{
    // IID de ISystemMediaTransportControlsInterop (systemmediatransportcontrolsinterop.h).
    // Hereda de IInspectable en C++ (MIDL_INTERFACE):
    // vtable: 0=QI, 1=AddRef, 2=Release, 3=GetIids, 4=GetRuntimeClassName, 5=GetTrustLevel, 6=GetForWindow.
    [ComImport]
    [Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        void GetIids(out uint iidCount, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int GetForWindow([In] IntPtr appWindow, [In] ref Guid riid, [Out] out IntPtr mediaControl);
    }

    // IID de ISystemMediaTransportControls (windows.media.idl, [exclusiveto(SystemMediaTransportControls)]):
    // {99FA3FF4-1742-42A6-902E-087D41F965EC}. El valor anterior (658d2101-...) no correspondía a esta
    // interfaz y provocaba que GetForWindow devolviera E_NOINTERFACE (HRESULT 0x80004002) en runtime.
    private static readonly Guid SmtcIid = new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    private SystemMediaTransportControls? _smtc;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;

    public void Inicializar(IntPtr hwndVentanaPrincipal)
    {
        if (_smtc != null || hwndVentanaPrincipal == IntPtr.Zero) return;

        try
        {
            var interop = SystemMediaTransportControls.As<ISystemMediaTransportControlsInterop>();
            Guid iid = SmtcIid;
            int hr = interop.GetForWindow(hwndVentanaPrincipal, ref iid, out IntPtr smtcPtr);
            if (hr != 0 || smtcPtr == IntPtr.Zero)
            {
                AppLogger.Warn("SystemMediaControlsService", $"GetForWindow devolvió HRESULT 0x{hr:X8}");
                return;
            }

            _smtc = WinRT.MarshalInterface<SystemMediaTransportControls>.FromAbi(smtcPtr);

            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.ButtonPressed += OnButtonPressed;
            AppLogger.Info("SystemMediaControlsService", "SMTC inicializado exitosamente para la ventana principal.");
        }
        catch (Exception ex)
        {
            // Puede fallar en Windows < 1809 o si el proceso no tiene aún una ventana top-level
            // válida; los controles multimedia del sistema simplemente no estarán disponibles.
            AppLogger.Warn("SystemMediaControlsService", $"No se pudo inicializar SMTC: {ex.Message}");
            _smtc = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                PlayRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Pause:
                PauseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void ActualizarMetadatos(string tituloAnime, int numeroEpisodio, string? rutaPortada)
    {
        if (_smtc == null) return;

        try
        {
            _smtc.IsEnabled = true;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = tituloAnime;
            updater.VideoProperties.Subtitle = $"Episodio {numeroEpisodio}";

            updater.Thumbnail = null;
            if (!string.IsNullOrWhiteSpace(rutaPortada) && Uri.TryCreate(rutaPortada, UriKind.Absolute, out var uri))
            {
                try
                {
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("SystemMediaControlsService", $"No se pudo asignar la portada al SMTC: {ex.Message}");
                }
            }

            updater.Update();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SystemMediaControlsService", $"Error actualizando metadatos SMTC: {ex.Message}");
        }
    }

    public void ActualizarEstadoReproduccion(bool reproduciendo)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = reproduciendo ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SystemMediaControlsService", $"Error actualizando estado de reproducción SMTC: {ex.Message}");
        }
    }

    public void ActualizarNavegacionDisponible(bool haySiguiente, bool hayAnterior)
    {
        if (_smtc == null) return;
        try
        {
            _smtc.IsNextEnabled = haySiguiente;
            _smtc.IsPreviousEnabled = hayAnterior;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SystemMediaControlsService", $"Error actualizando navegación SMTC: {ex.Message}");
        }
    }

    public void Deshabilitar()
    {
        if (_smtc == null) return;
        try
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _smtc.IsEnabled = false;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SystemMediaControlsService", $"Error deshabilitando SMTC: {ex.Message}");
        }
    }
}
