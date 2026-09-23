using System;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Atajo de teclado GLOBAL ("boss key"/"tecla de pánico"): funciona aunque AnimeLocalTracker no
/// tenga el foco, a diferencia del resto de atajos del reproductor. Ver <see cref="PanicKeyService"/>.
/// </summary>
public interface IPanicKeyService
{
    /// <summary>Se dispara cuando el usuario presiona la tecla de pánico registrada.</summary>
    event EventHandler? Activado;

    /// <summary>HWND de la ventana principal, necesario para registrar el hotkey a nivel de SO.</summary>
    void Inicializar(IntPtr hwnd);

    /// <summary>Registra o retira el hotkey global según <paramref name="activa"/> y <paramref name="tecla"/> ("F12"/"Escape").</summary>
    void Aplicar(bool activa, string tecla);

    /// <summary>Debe llamarse desde el WndProc de la ventana principal con cada mensaje recibido;
    /// devuelve true si el mensaje era el hotkey de pánico (para marcarlo como manejado).</summary>
    bool ProcesarMensaje(int msg, IntPtr wParam);
}
