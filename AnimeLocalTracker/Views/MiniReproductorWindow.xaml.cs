using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

/// <summary>
/// Ventana flotante del mini reproductor (ver MainWindow.EntrarModoPiP). La abre y la cierra la ventana principal; si el usuario la
/// cierra por su cuenta (Alt+F4), se trata igual que su botón ✕: se cierra el episodio guardando el progreso.
/// </summary>
public partial class MiniReproductorWindow : Window
{
    /// <summary>Grosor (en DIP) de la franja de los bordes que sirve para cambiar el tamaño (igual que ResizeBorderThickness).</summary>
    public const double GrosorBorde = 6;

    private const double ProporcionPorDefecto = 16d / 9d;

    private readonly ReproductorViewModel _reproductor;
    private INotifyPropertyChanged? _videoObservado;
    private bool _cierrePorCodigo;

    public MiniReproductorWindow(ReproductorViewModel reproductor)
    {
        InitializeComponent();
        _reproductor = reproductor;
        DataContext = reproductor;
        Title = LocalizationService.T("Player_MiniTitulo");

        Loaded += (_, _) => AjustarAProporcion();
        _videoObservado = reproductor.Player?.Video;
        if (_videoObservado != null) _videoObservado.PropertyChanged += Video_PropertyChanged;
    }

    /// <summary>Cierre pedido por la app (restaurar el formato habitual o cerrar el episodio).</summary>
    public void CerrarDesdeCodigo()
    {
        _cierrePorCodigo = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_cierrePorCodigo && DataContext is ReproductorViewModel reproductor)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => reproductor.CerrarCommand.Execute(null));
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_videoObservado != null) _videoObservado.PropertyChanged -= Video_PropertyChanged;
        _videoObservado = null;
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
    }

    /// <summary>
    /// True si el punto (en píxeles de pantalla) cae en la franja de los bordes de esta ventana. Lo usa la vista del reproductor para
    /// dejar pasar esos puntos a esta ventana: el video y los controles de Flyleaf son ventanas nativas que la cubren entera.
    /// </summary>
    public bool EnBordeDeRedimension(int x, int y)
    {
        var manejador = new WindowInteropHelper(this).Handle;
        if (manejador == IntPtr.Zero || !GetWindowRect(manejador, out var r)) return false;

        int borde = (int)Math.Ceiling(GrosorBorde * VisualTreeHelper.GetDpi(this).DpiScaleX);
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom
            && (x - r.Left < borde || r.Right - x <= borde || y - r.Top < borde || r.Bottom - y <= borde);
    }

    /// <summary>Proporción ancho/alto del episodio que se está viendo (16:9 si aún no se sabe).</summary>
    private double Proporcion()
    {
        try
        {
            double valor = _reproductor.Player?.Video.AspectRatio.Value ?? 0;
            return valor is > 0.5 and < 4 ? valor : ProporcionPorDefecto;
        }
        catch
        {
            return ProporcionPorDefecto;
        }
    }

    private void Video_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "AspectRatio") Dispatcher.BeginInvoke(AjustarAProporcion);
    }

    /// <summary>Ajusta el alto a la proporción del video (al abrir, o si el siguiente episodio tiene otra), sin mover el borde de abajo:
    /// el mini se queda pegado a la esquina donde estaba.</summary>
    private void AjustarAProporcion()
    {
        if (!IsLoaded) return;

        // Recién cargada, ActualWidth/ActualHeight aún pueden valer 0: entonces manda el tamaño asignado al abrirla.
        double anchoActual = ActualWidth > 0 ? ActualWidth : Width;
        double altoActual = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(anchoActual) || double.IsNaN(altoActual) || anchoActual <= 0) return;

        double alto = Math.Max(MinHeight, Math.Round(anchoActual / Proporcion()));
        if (Math.Abs(alto - altoActual) < 1) return;

        double abajo = Top + altoActual;
        Height = alto;
        Top = Math.Max(SystemParameters.WorkArea.Top, abajo - alto);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        if (msg == WM_SIZING)
        {
            // Al cambiar el tamaño por un borde o una esquina, el otro lado acompaña para conservar la proporción del video.
            var r = Marshal.PtrToStructure<RECT>(lParam);
            double proporcion = Proporcion();
            int ancho = r.Right - r.Left, alto = r.Bottom - r.Top;

            switch (wParam.ToInt32())
            {
                case 3: // arriba
                case 6: // abajo
                    r.Right = r.Left + (int)Math.Round(alto * proporcion);
                    break;
                case 4: // arriba-izquierda
                case 5: // arriba-derecha
                    r.Top = r.Bottom - (int)Math.Round(ancho / proporcion);
                    break;
                case 1: // izquierda
                case 2: // derecha
                    r.Bottom = r.Top + (int)Math.Round(ancho / proporcion);
                    // Pegado a la esquina de abajo, crecería por debajo de la barra de tareas: entonces crece hacia arriba.
                    int limite = LimiteInferiorDelMonitor(hwnd);
                    if (r.Bottom > limite)
                    {
                        r.Top -= r.Bottom - limite;
                        r.Bottom = limite;
                    }
                    break;
                default: // esquinas de abajo
                    r.Bottom = r.Top + (int)Math.Round(ancho / proporcion);
                    break;
            }

            Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    /// <summary>Borde inferior (en píxeles) del área de trabajo del monitor de la ventana: sin la barra de tareas.</summary>
    private static int LimiteInferiorDelMonitor(IntPtr ventana)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(ventana, 2 /* MONITOR_DEFAULTTONEAREST */), ref info) ? info.rcWork.Bottom : int.MaxValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr ventana, uint opciones);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr ventana, out RECT rect);
}
