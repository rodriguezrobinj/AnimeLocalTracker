using System;
using System.Runtime.InteropServices;

namespace AnimeLocalTracker.Services;

/// <summary>
/// RegisterHotKey (user32) registra el atajo a nivel de sistema operativo: llega a la app aunque
/// otra ventana tenga el foco. ADVERTENCIA: mientras esté activo con "Escape", esa tecla queda
/// interceptada en TODAS las aplicaciones abiertas (no solo AnimeLocalTracker) — es una limitación
/// inherente de un hotkey global, no un bug. MOD_NOREPEAT evita que mantener la tecla presionada
/// dispare el evento en bucle.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class PanicKeyService : IPanicKeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x0B00F;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF12 = 0x7B;
    private const uint VkEscape = 0x1B;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _registrada;

    public event EventHandler? Activado;

    public void Inicializar(IntPtr hwnd) => _hwnd = hwnd;

    public void Aplicar(bool activa, string tecla)
    {
        if (_registrada)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registrada = false;
        }

        if (!activa || _hwnd == IntPtr.Zero) return;

        uint vk = string.Equals(tecla, "Escape", StringComparison.OrdinalIgnoreCase) ? VkEscape : VkF12;
        _registrada = RegisterHotKey(_hwnd, HotkeyId, ModNoRepeat, vk);
        if (!_registrada)
        {
            AppLogger.Warn("PanicKeyService", $"No se pudo registrar la tecla de pánico '{tecla}' (puede estar en uso por otra aplicación).");
        }
    }

    public bool ProcesarMensaje(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY || wParam.ToInt32() != HotkeyId) return false;

        Activado?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
