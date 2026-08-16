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
        
        _isFullScreen = true;
    }

    private void SalirPantallaCompleta()
    {
        // 1. Volver a mostrar nuestra barra de título
        BarraTitulo.Visibility = Visibility.Visible;
        
        // 2. Deshacer la pantalla completa
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        
        // 3. Restaurar los límites de la pantalla para maximizado normal
        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
        
        WindowState = WindowState.Normal;
        
        // 4. Restaurar el WindowChrome
        if (_chromeCache != null)
        {
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, _chromeCache);
        }
        
        _isFullScreen = false;
    }
    
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11)
        {
            BtnPantallaCompleta_Click(this, new RoutedEventArgs());
        }
    }
}