using System;
using System.Runtime.InteropServices;

namespace AnimeLocalTracker.Views;

/// <summary>
/// Monitor en el que está una ventana. Lo usan la ventana principal (para maximizarse sin tapar la barra de tareas) y el
/// mini reproductor (para no bajar más allá de ella); antes cada una declaraba las mismas funciones de Windows.
/// </summary>
internal static partial class MonitorDeVentana
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangulo { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public Rectangulo rcMonitor; public Rectangulo rcWork; public uint dwFlags; }

    private const uint MonitorMasCercano = 2; // MONITOR_DEFAULTTONEAREST

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr ventana, uint opciones);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>Rectángulo completo del monitor y su área de trabajo (sin la barra de tareas), en píxeles. False si Windows no
    /// los da.</summary>
    internal static bool Areas(IntPtr ventana, out Rectangulo monitor, out Rectangulo trabajo)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr hMonitor = MonitorFromWindow(ventana, MonitorMasCercano);
        bool ok = hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info);
        monitor = info.rcMonitor;
        trabajo = info.rcWork;
        return ok;
    }
}
