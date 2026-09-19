using System;
using System.Runtime.InteropServices;
using System.Windows;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.Views;

public partial class MainWindow : Window, IVentanaPrincipal
{
    public bool IsFullScreen { get; private set; }
    public bool EsModoPiP { get; private set; }

    private System.Windows.Shell.WindowChrome? _chromeCache;

    // PIP-01: estado a restaurar al salir del modo Picture-in-Picture.
    private WindowState _estadoPrevioPiP;
    private Rect _restoreBoundsPiP;
    private ResizeMode _resizeModePrevioPiP;
    private bool _fullscreenPrevioPiP;
    private double _minWidthPrevioPiP;
    private double _minHeightPrevioPiP;

    private const double AnchoPiP = 420;
    private const double AltoPiP = 236;
    private const double MargenPiP = 16;
    private const double MinAnchoPiP = 240;
    private const double MinAltoPiP = 135;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // UX-05: al abrir un diálogo modal, el foco se mueve al botón Aceptar para que
        // el teclado (Enter/Esc) funcione de inmediato y no quede en la página subyacente.
        viewModel.DialogService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IDialogService.DialogoVisible) && viewModel.DialogService.DialogoVisible)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    () => BtnDialogoAceptar?.Focus());
            }
        };
    }

    /// <summary>
    /// UX-05: Esc cierra el diálogo abierto (antes el foco podía quedar bajo el overlay
    /// y Esc llegaba a la página de detrás, p. ej. cerrando el reproductor).
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && DataContext is MainViewModel vm && vm.DialogService.DialogoVisible)
        {
            if (vm.DialogService.CancelarDialogoCommand.CanExecute(null))
            {
                vm.DialogService.CancelarDialogoCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  WM_GETMINMAXINFO: Fuerza que WindowState.Maximized respete
    //  el área de trabajo del monitor (sin cubrir la barra de tareas).
    //  Se desactiva cuando IsFullScreen=true para cubrir TODO.
    // ═══════════════════════════════════════════════════════════════

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WindowProc);

        // SMT-01: SMTC se ata al HWND de la ventana principal una única vez, tan pronto
        // como existe (no bloquea el arranque: falla en silencio en Windows < 1809).
        try
        {
            App.ServiceProvider.GetService<ISystemMediaControlsService>()?.Inicializar(hwnd);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"No se pudo inicializar los controles multimedia del sistema: {ex.Message}");
        }

        try
        {
            App.ServiceProvider.GetService<ISystemTrayService>()?.Habilitar(this);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"No se pudo inicializar el icono de la bandeja del sistema: {ex.Message}");
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO = 0x0024
        if (msg == 0x0024 && !IsFullScreen && !EsModoPiP)
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
        // Pantalla completa y PiP son excluyentes.
        if (EsModoPiP) SalirModoPiP();

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

    // ═══════════════════════════════════════════════════════════════
    //  PIP-01: Picture-in-Picture — recuadro compacto, siempre encima,
    //  anclado a la esquina inferior derecha del área de trabajo.
    // ═══════════════════════════════════════════════════════════════

    public void EntrarModoPiP()
    {
        if (EsModoPiP) return;
        EsModoPiP = true;

        // PiP y pantalla completa son excluyentes.
        _fullscreenPrevioPiP = IsFullScreen;
        if (IsFullScreen) SalirPantallaCompleta();

        // 1. Guardar estado para restaurar al salir (RestoreBounds sobrevive aunque la
        //    ventana esté maximizada, a diferencia de Left/Top/Width/Height directos).
        _estadoPrevioPiP = WindowState;
        _restoreBoundsPiP = RestoreBounds;
        _resizeModePrevioPiP = ResizeMode;
        _minWidthPrevioPiP = MinWidth;
        _minHeightPrevioPiP = MinHeight;

        // 2. Ocultar la barra de título custom (no cabe y no aplica en un recuadro tan chico)
        BarraTitulo.Visibility = Visibility.Collapsed;

        // MinWidth/MinHeight de la ventana normal son 800x600 (ver MainWindow.xaml): sin bajarlos
        // primero, WPF recorta el Width/Height del PiP de vuelta a 800x600 silenciosamente.
        MinWidth = MinAnchoPiP;
        MinHeight = MinAltoPiP;

        // 3. Dar un borde de agarre real para poder redimensionar (en modo normal
        //    ResizeBorderThickness=0 porque el chrome custom no lo necesita).
        _chromeCache ??= System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        // 4. Encoger, fijar encima de las demás ventanas y anclar a la esquina inferior derecha.
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;

        AplicarTamanioYPosicionPiP();

        // La transición Maximized→Normal no siempre ha asentado RestoreBounds/el layout nativo
        // en este mismo tick (se observó Left/Top mal calculados justo tras un arranque en frío);
        // reaplicar una vez que WPF termina el ciclo de layout actual garantiza la posición final.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, AplicarTamanioYPosicionPiP);
    }

    private void AplicarTamanioYPosicionPiP()
    {
        if (!EsModoPiP) return;

        var area = SystemParameters.WorkArea;
        Width = AnchoPiP;
        Height = AltoPiP;
        Left = area.Right - AnchoPiP - MargenPiP;
        Top = area.Bottom - AltoPiP - MargenPiP;
    }

    public void SalirModoPiP()
    {
        if (!EsModoPiP) return;
        EsModoPiP = false;

        Topmost = false;
        ResizeMode = _resizeModePrevioPiP;
        BarraTitulo.Visibility = Visibility.Visible;
        MinWidth = _minWidthPrevioPiP;
        MinHeight = _minHeightPrevioPiP;

        if (_chromeCache != null)
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, _chromeCache);
        }

        WindowState = _estadoPrevioPiP;
        if (_estadoPrevioPiP == WindowState.Normal)
        {
            Left = _restoreBoundsPiP.Left;
            Top = _restoreBoundsPiP.Top;
            Width = _restoreBoundsPiP.Width;
            Height = _restoreBoundsPiP.Height;
        }
        else if (_estadoPrevioPiP == WindowState.Maximized)
        {
            // Igual que SalirPantallaCompleta: forzar el ciclo Normal→Maximized para que el
            // hook WM_GETMINMAXINFO vuelva a limitar al área de trabajo (sin tapar la taskbar).
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }

        if (_fullscreenPrevioPiP)
        {
            _fullscreenPrevioPiP = false;
            EntrarPantallaCompleta();
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            TogglePantallaCompleta();
        }
    }

    /// <summary>
    /// ARC-04: devuelve el foco del teclado a la ventana principal (lo usa el
    /// reproductor al alternar pantalla completa, sin castear a la ventana).
    /// </summary>
    public void Enfocar()
    {
        Focus();
        System.Windows.Input.Keyboard.Focus(this);
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