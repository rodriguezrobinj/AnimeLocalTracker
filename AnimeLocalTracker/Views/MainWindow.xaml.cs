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

    // Mini reproductor: ventana flotante propia (ver EntrarModoPiP). Se recuerda dónde la dejó el usuario durante la sesión.
    private MiniReproductorWindow? _ventanaMini;
    private static Rect? _ultimoRectMini;

    private const double AnchoMini = 420;
    private const double AltoMini = 236; // 16:9; al abrirse se ajusta a la proporción real del episodio
    private const double MargenMini = 16;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        IsVisibleChanged += MainWindow_IsVisibleChanged;

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
        else if (e.Key == System.Windows.Input.Key.Escape && DataContext is MainViewModel vmSel && vmSel.SelectorTorrentService.SelectorVisible)
        {
            // Esc en el selector de torrents = Cancelar (igual que en las confirmaciones)
            vmSel.SelectorTorrentService.CancelarSelectorCommand.Execute(null);
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

        try
        {
            _panicKeyService = App.ServiceProvider.GetService<IPanicKeyService>();
            _panicKeyService?.Inicializar(hwnd);
            var cfg = App.ServiceProvider.GetService<ISettingsService>()?.ObtenerConfiguracion();
            _panicKeyService?.Aplicar(cfg?.TeclaPanicoActiva ?? false, cfg?.TeclaPanico ?? "F12");
            if (_panicKeyService != null) _panicKeyService.Activado += PanicKeyService_Activado;

            var settingsService = App.ServiceProvider.GetService<ISettingsService>();
            if (settingsService != null)
            {
                settingsService.ConfiguracionModificada += nuevaCfg =>
                    _panicKeyService?.Aplicar(nuevaCfg?.TeclaPanicoActiva ?? false, nuevaCfg?.TeclaPanico ?? "F12");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"No se pudo inicializar la tecla de pánico: {ex.Message}");
        }
    }

    private IPanicKeyService? _panicKeyService;
    private bool _integracionesIniciadas;

    /// <summary>
    /// Controles multimedia de Windows (SMTC) e icono de la bandeja. No hacen falta para pintar la ventana y el primero
    /// carga una biblioteca de 25 MB, así que App los inicia cuando la ventana ya se ve (antes iban en OnSourceInitialized,
    /// es decir, antes del primer pintado). Una sola vez.
    /// </summary>
    public void InicializarIntegracionesDelSistema()
    {
        if (_integracionesIniciadas) return;
        _integracionesIniciadas = true;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // SMT-01: SMTC se ata al HWND de la ventana principal una única vez (falla en silencio en Windows < 1809).
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

    // ═══════════════════════════════════════════════════════════════
    //  Pestañas preparadas por adelantado: la primera visita a una
    //  pestaña construía su vista entera (0,1-1 s medidos). Con la app
    //  en reposo se van construyendo de una en una, sin mostrarlas.
    // ═══════════════════════════════════════════════════════════════

    private static readonly TimeSpan EsperaAntesDePrecalentar = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PausaEntrePestanas = TimeSpan.FromMilliseconds(350);
    /// <summary>Sin tocar ratón ni teclado durante este tiempo se considera que el usuario no está en medio de algo.</summary>
    private const int MsSinEntradaParaPrecalentar = 1000;

    public void PrecalentarPestanasEnReposo()
    {
        if (DataContext is not MainViewModel vm) return;
        _ = PrecalentarPestanasAsync(vm.Navigation);
    }

    private async System.Threading.Tasks.Task PrecalentarPestanasAsync(INavigationService navegacion)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(EsperaAntesDePrecalentar);

            // De la más usada a la menos; cada ViewModel se pide al llegar su turno (crearlo también cuesta). "Acerca de" se
            // queda fuera: es la más cara de construir (dibuja el registro de cambios) y la que menos se visita.
            Func<object>[] pestanas =
            [
                () => navegacion.ObtenerVista(Pestanas.Historial), () => navegacion.ObtenerVista(Pestanas.Actualizaciones), () => navegacion.ObtenerVista(Pestanas.Calendario),
                () => navegacion.ObtenerVista(Pestanas.Descargas), () => navegacion.ObtenerVista(Pestanas.Estadisticas), () => navegacion.ObtenerVista(Pestanas.Logros),
                () => navegacion.ObtenerVista(Pestanas.AgregarAnime), () => navegacion.ObtenerVista(Pestanas.Configuracion), navegacion.ObtenerVisorRegistros
            ];

            var reloj = System.Diagnostics.Stopwatch.StartNew();
            long msTrabajo = 0;
            int hechas = 0;

            async System.Threading.Tasks.Task EnReposoAsync(Action trabajo)
            {
                // Nunca mientras el usuario está haciendo algo (desplazándose, escribiendo, viendo un episodio): se espera.
                while (!EnReposoParaPrecalentar()) await System.Threading.Tasks.Task.Delay(1000);

                await Dispatcher.InvokeAsync(() =>
                {
                    var paso = System.Diagnostics.Stopwatch.StartNew();
                    trabajo();
                    msTrabajo += paso.ElapsedMilliseconds;
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                await System.Threading.Tasks.Task.Delay(PausaEntrePestanas);
            }

            foreach (var obtener in pestanas)
            {
                await EnReposoAsync(() =>
                {
                    if (ContenedorVistaPrincipal.Precalentar(obtener())) hechas++;
                });
            }

            // La ficha: su vista es una sola y pasa de un anime al siguiente. Se deja construida con una ficha vacía,
            // que se descarta enseguida; la primera que abras ya solo le cambia los datos.
            await EnReposoAsync(() =>
            {
                var fichaVacia = navegacion.CrearDetalle();
                if (ContenedorVistaPrincipal.Precalentar(fichaVacia)) hechas++;
                fichaVacia.Dispose();
            });

            // Las secciones internas de la Galería: el menú de minijuegos y, después, cada juego (uno por paso).
            if (BuscarDescendiente<GaleriaView>(ContenedorVistaPrincipal) is { } galeria)
            {
                bool quedan = true;
                while (quedan)
                {
                    await EnReposoAsync(() =>
                    {
                        quedan = galeria.PrecalentarMinijuegos();
                        if (quedan) hechas++;
                    });
                }
            }

            AppLogger.Info("MainWindow", $"[Perf] Pestañas preparadas por adelantado: {hechas} en {msTrabajo} ms de trabajo, repartidos en {reloj.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"No se pudieron preparar las pestañas por adelantado: {ex.Message}");
        }
    }

    private static T? BuscarDescendiente<T>(DependencyObject raiz) where T : DependencyObject
    {
        int hijos = System.Windows.Media.VisualTreeHelper.GetChildrenCount(raiz);
        for (int i = 0; i < hijos; i++)
        {
            var hijo = System.Windows.Media.VisualTreeHelper.GetChild(raiz, i);
            if (hijo is T encontrado) return encontrado;
            if (BuscarDescendiente<T>(hijo) is { } descendiente) return descendiente;
        }
        return null;
    }

    private bool EnReposoParaPrecalentar()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return false;
        if (DataContext is MainViewModel { Navigation.ReproductorActivo: not null }) return false;

        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return true;
        return unchecked((uint)Environment.TickCount - info.dwTime) >= MsSinEntradaParaPrecalentar;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// "Modo discreto": silencia el video en curso (si hay uno) y oculta la app a la bandeja al
    /// instante. Se ejecuta en el hilo de UI porque llega desde WndProc, fuera del Dispatcher normal.
    /// </summary>
    private void PanicKeyService_Activado(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (DataContext is MainViewModel mvm && mvm.Navigation.ReproductorActivo is { } reproductor && !reproductor.IsMuted)
                {
                    reproductor.ToggleMuteCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MainWindow", $"No se pudo silenciar el reproductor en modo pánico: {ex.Message}");
            }

            _ventanaMini?.Hide(); // el mini reproductor también desaparece: es un modo discreto
            App.ServiceProvider.GetService<ISystemTrayService>()?.IniciarEnBandeja();
            AppLogger.Info("MainWindow", "Modo discreto activado (tecla de pánico).");
        });
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_panicKeyService != null && _panicKeyService.ProcesarMensaje(msg, wParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        // WM_GETMINMAXINFO = 0x0024
        if (msg == 0x0024 && !IsFullScreen)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            if (MonitorDeVentana.Areas(hwnd, out var monitor, out var trabajo))
            {
                // trabajo = área útil sin la barra de tareas
                mmi.ptMaxPosition.x = Math.Abs(trabajo.Left - monitor.Left);
                mmi.ptMaxPosition.y = Math.Abs(trabajo.Top  - monitor.Top);
                mmi.ptMaxSize.x     = Math.Abs(trabajo.Right  - trabajo.Left);
                mmi.ptMaxSize.y     = Math.Abs(trabajo.Bottom - trabajo.Top);
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

    // ═══════════════════════════════════════════════════════════════
    //  Mini reproductor: el video pasa a una ventana flotante propia,
    //  siempre encima y SIN dueño. Antes se encogía la ventana principal
    //  entera: no se podía navegar por la app y, al minimizarla, el
    //  video desaparecía con ella.
    // ═══════════════════════════════════════════════════════════════

    public void EntrarModoPiP()
    {
        if (_ventanaMini != null) return;
        if (DataContext is not MainViewModel { Navigation.ReproductorActivo: { } reproductor }) return;

        // La app vuelve a su tamaño normal para poder navegar mientras el episodio sigue en la esquina.
        if (IsFullScreen) SalirPantallaCompleta();

        var mini = new MiniReproductorWindow(reproductor);
        var area = SystemParameters.WorkArea;
        var rect = _ultimoRectMini ?? new Rect(area.Right - AnchoMini - MargenMini, area.Bottom - AltoMini - MargenMini, AnchoMini, AltoMini);
        mini.Width = Math.Clamp(rect.Width, mini.MinWidth, area.Width);
        mini.Height = Math.Clamp(rect.Height, mini.MinHeight, area.Height);
        mini.Left = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - mini.Width));
        mini.Top = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - mini.Height));
        mini.Closed += (_, _) =>
        {
            _ultimoRectMini = new Rect(mini.Left, mini.Top, mini.Width, mini.Height);
            if (ReferenceEquals(_ventanaMini, mini))
            {
                _ventanaMini = null;
                EsModoPiP = false;
            }
        };

        _ventanaMini = mini;
        EsModoPiP = true;
        mini.Show();
    }

    public void SalirModoPiP()
    {
        var mini = _ventanaMini;
        if (mini == null) return;
        _ventanaMini = null;
        EsModoPiP = false;
        mini.CerrarDesdeCodigo();
    }

    /// <summary>Al volver del mini reproductor al formato habitual: la ventana principal se restaura (si estaba minimizada
    /// o en la bandeja) y pasa al frente con el episodio.</summary>
    public void MostrarYActivar()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
        {
            // SW_RESTORE vuelve al estado de antes de minimizar (maximizada si lo estaba), cosa que WindowState=Normal no hace.
            ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, 9);
        }
        Activate();
        Enfocar();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Sin dueño, la ventana del mini mantendría viva la app al cerrar la principal.
        SalirModoPiP();
        base.OnClosed(e);
    }

    /// <summary>Si la ventana principal se oculta a la bandeja por la tecla de pánico, el mini se oculta con ella; vuelve al restaurarla.</summary>
    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_ventanaMini != null && IsVisible && !_ventanaMini.IsVisible) _ventanaMini.Show();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

}