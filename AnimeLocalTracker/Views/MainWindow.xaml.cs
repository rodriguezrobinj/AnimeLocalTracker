using System.Windows;
using AnimeLocalTracker.ViewModels; // Asegúrate de importar el namespace

namespace AnimeLocalTracker.Views;

public partial class MainWindow : Window
{
    private bool _isFullScreen = false;

    private MainViewModel _viewModel;

    // Recibimos el ViewModel mágicamente gracias a la inyección de dependencias
    public MainWindow(MainViewModel viewModel) 
    {
        InitializeComponent();
        _viewModel = viewModel;
        
        // FIX: Evitar que al maximizar la ventana oculte la barra de tareas de Windows
        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
        
        // ¡Esta es la línea más importante de MVVM!
        DataContext = _viewModel; 
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(WindowProc);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GetMonitorInfo(System.IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern System.IntPtr MonitorFromWindow(System.IntPtr hwnd, uint dwFlags);

    private System.IntPtr WindowProc(System.IntPtr hwnd, int msg, System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024 && !_isFullScreen) // WM_GETMINMAXINFO
        {
            MINMAXINFO mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
            System.IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != System.IntPtr.Zero)
            {
                MONITORINFO monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                GetMonitorInfo(monitor, ref monitorInfo);

                mmi.ptMaxPosition.x = System.Math.Abs(monitorInfo.rcWork.left - monitorInfo.rcMonitor.left);
                mmi.ptMaxPosition.y = System.Math.Abs(monitorInfo.rcWork.top - monitorInfo.rcMonitor.top);
                mmi.ptMaxSize.x = System.Math.Abs(monitorInfo.rcWork.right - monitorInfo.rcWork.left);
                mmi.ptMaxSize.y = System.Math.Abs(monitorInfo.rcWork.bottom - monitorInfo.rcWork.top);
            }
            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return System.IntPtr.Zero;
    }

    private void BtnMinimizar_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximizar_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen) 
        {
            // Si estábamos en pantalla completa real y le damos al botón de restaurar/maximizar,
            // primero debemos desactivar la pantalla completa.
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
        if (_isFullScreen)
        {
            SalirPantallaCompleta();
        }
        else
        {
            EntrarPantallaCompleta();
        }
    }

    private System.Windows.Shell.WindowChrome? _chromeCache;

    private void EntrarPantallaCompleta()
    {
        _isFullScreen = true; // IMPORTANTÍSIMO: Setear antes de maximizar para que WM_GETMINMAXINFO lo ignore.
        
        // 1. Guardar y remover el WindowChrome (es el culpable de que no cubra la barra de tareas)
        if (_chromeCache == null)
        {
            _chromeCache = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        }
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

        // 2. Ocultar nuestra barra de botones
        BarraTitulo.Visibility = Visibility.Collapsed;

        // 3. Configurar la ventana para pantalla completa real
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; 
        Topmost = true; 
        
        // 4. Forzar el refresco y maximizar quitando límites
        WindowState = WindowState.Normal;
        MaxHeight = double.PositiveInfinity;
        MaxWidth = double.PositiveInfinity;
        WindowState = WindowState.Maximized;
    }

    private void SalirPantallaCompleta()
    {
        _isFullScreen = false; // IMPORTANTÍSIMO: Setear antes de restablecer los estados.
        
        // 1. Volver a mostrar nuestra barra de título
        BarraTitulo.Visibility = Visibility.Visible;
        
        // 2. Deshacer la pantalla completa
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = false;
        
        // 3. Restaurar los límites de la pantalla para maximizado normal
        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
        
        WindowState = WindowState.Maximized;
        
        // 4. Restaurar el WindowChrome
        if (_chromeCache != null)
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, _chromeCache);
        }
    }
    
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            BtnPantallaCompleta_Click(this, new RoutedEventArgs());
        }
    }
}