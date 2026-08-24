using System.Windows;
using System.Runtime.InteropServices;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class MainWindow : Window
{
    public bool IsFullScreen { get; private set; }

    private MainViewModel _viewModel;
    private System.Windows.Shell.WindowChrome? _chromeCache;

    public MainWindow(MainViewModel viewModel) 
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel; 

        ActualizarVista(_viewModel.VistaActual);
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.VistaActual))
            {
                ActualizarVista(_viewModel.VistaActual);
            }
        };
    }

    private void ActualizarVista(object? viewModel)
    {
        ContenedorVistaPrincipal.Content = viewModel switch
        {
            GaleriaViewModel vm => new GaleriaView { DataContext = vm },
            DetalleViewModel vm => new DetalleView { DataContext = vm },
            AgregarAnimeViewModel vm => new AgregarAnimeView { DataContext = vm },
            CalendarioViewModel vm => new CalendarioView { DataContext = vm },
            ReproductorViewModel vm => new ReproductorView { DataContext = vm },
            DescargasViewModel vm => new DescargasView { DataContext = vm },
            ConfiguracionViewModel vm => new ConfiguracionView { DataContext = vm },
            AcercaDeViewModel vm => new AcercaDeView { DataContext = vm },
            _ => null
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  WM_GETMINMAXINFO: Fuerza que WindowState.Maximized respete
    //  el área de trabajo del monitor (sin cubrir la barra de tareas).
    //  Se desactiva cuando IsFullScreen=true para cubrir TODO.
    // ═══════════════════════════════════════════════════════════════

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO = 0x0024
        if (msg == 0x0024 && !IsFullScreen)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref mi);

                // rcWork = área útil sin la barra de tareas
                mmi.ptMaxPosition.x = Math.Abs(mi.rcWork.left - mi.rcMonitor.left);
                mmi.ptMaxPosition.y = Math.Abs(mi.rcWork.top  - mi.rcMonitor.top);
                mmi.ptMaxSize.x     = Math.Abs(mi.rcWork.right  - mi.rcWork.left);
                mmi.ptMaxSize.y     = Math.Abs(mi.rcWork.bottom - mi.rcWork.top);
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Botones de la barra de título
    // ═══════════════════════════════════════════════════════════════

    private void BtnMinimizar_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximizar_Click(object sender, RoutedEventArgs e)
    {
        if (IsFullScreen) 
        {
            SalirPantallaCompleta();
        }
        else 
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void BtnCerrar_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void BtnPantallaCompleta_Click(object sender, RoutedEventArgs e)
    {
        TogglePantallaCompleta();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pantalla completa (cubre barra de tareas)
    // ═══════════════════════════════════════════════════════════════

    public void TogglePantallaCompleta()
    {
        if (IsFullScreen)
            SalirPantallaCompleta();
        else
            EntrarPantallaCompleta();
    }

    private void EntrarPantallaCompleta()
    {
        IsFullScreen = true; // Desactiva WM_GETMINMAXINFO → permite cubrir toda la pantalla

        // 1. Guardar y quitar WindowChrome (es el que impide cubrir la barra de tareas)
        _chromeCache ??= System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

        // 2. Ocultar barra de título
        BarraTitulo.Visibility = Visibility.Collapsed;

        // 3. Configurar ventana para fullscreen real
        WindowStyle = WindowStyle.None;
        ResizeMode  = ResizeMode.NoResize; 
        Topmost     = true; 

        // 4. Forzar refresco (Normal → Maximized) para que WPF recalcule sin límites
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
    }

    private void SalirPantallaCompleta()
    {
        IsFullScreen = false; // Reactiva WM_GETMINMAXINFO → respeta barra de tareas

        // 1. Restaurar barra de título
        BarraTitulo.Visibility = Visibility.Visible;
        
        // 2. Restaurar propiedades de ventana
        WindowStyle = WindowStyle.None;
        ResizeMode  = ResizeMode.CanResize;
        Topmost     = false;
        
        // 3. Re-maximizar (ahora WM_GETMINMAXINFO limitará al área de trabajo)
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
        
        // 4. Restaurar WindowChrome
        if (_chromeCache != null)
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, _chromeCache);
        }
    }
    
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            TogglePantallaCompleta();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Win32 interop structs
    // ═══════════════════════════════════════════════════════════════

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
}