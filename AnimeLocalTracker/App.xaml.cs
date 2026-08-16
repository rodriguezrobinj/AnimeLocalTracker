using System;
using System.Windows;
using AnimeLocalTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using AnimeLocalTracker.Views; // Asegúrate de que este namespace exista
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker;

public partial class App : Application
{
    // Este es nuestro contenedor global de dependencias
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. Registramos las Vistas (Ventanas)
        services.AddTransient<MainWindow>();

        // 2. Aquí registraremos los ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<GaleriaViewModel>();
        services.AddTransient<DetalleViewModel>();

        // 3. Aquí registraremos los Servicios
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<IFileScannerService, FileScannerService>();
        
        // IHttpClientFactory nativo
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>();
        
        // Lo registramos como Singleton porque queremos que haya una sola conexión a la BD en toda la app
        services.AddSingleton<IDatabaseService, DatabaseService>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Manejo Global de Errores
        this.DispatcherUnhandledException += (s, args) => 
        {
            args.Handled = true; // Evita que se cierre la app
            MessageBox.Show("Ha ocurrido un error inesperado.\n\nDetalles técnicos:\n" + args.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        
        // Pedimos la instancia del servicio de base de datos
        var dbService = ServiceProvider.GetRequiredService<IDatabaseService>();
        
        // Inicializar reproductor de video nativo (Removido)
        
        // Obligamos a que se cree el archivo y la tabla antes de continuar
        await dbService.InicializarBaseDatosAsync();

        // En lugar de que WPF abra la ventana automáticamente (StartupUri),
        // nosotros le pedimos al contenedor DI que nos construya la ventana
        // con todas sus dependencias ya inyectadas.
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}