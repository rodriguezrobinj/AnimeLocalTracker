using System;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using AnimeLocalTracker.Views; // Asegúrate de que este namespace exista
using AnimeLocalTracker.Services;
using Polly;

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
        
        // === MANEJADORES GLOBALES DE EXCEPCIONES ===
        
        // 1. Excepciones no manejadas en el hilo de UI (Dispatcher)
        this.DispatcherUnhandledException += (s, args) =>
        {
            try {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                System.IO.File.WriteAllText(logPath, $"[{DateTime.Now}] UI Thread Exception:\n{args.Exception}");
            } catch {}
            
            MessageBox.Show($"Error en la aplicación:\n{args.Exception.Message}", 
                            "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // Prevenir cierre de la app
        };
        
        // 2. Excepciones no manejadas en hilos secundarios (fatal, no se puede prevenir el cierre)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log_domain.txt");
                System.IO.File.WriteAllText(logPath, $"[{DateTime.Now}] Domain Exception:\n{args.ExceptionObject}");
            } catch {}
        };
        
        // 3. Excepciones de Tasks async no observadas (esta es la causa más probable del crash silencioso)
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log_task.txt");
                System.IO.File.WriteAllText(logPath, $"[{DateTime.Now}] Unobserved Task Exception:\n{args.Exception}");
            } catch {}
            args.SetObserved(); // Marcar como observada para prevenir cierre
        };
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. Registramos las Vistas (Ventanas)
        services.AddTransient<MainWindow>();
        services.AddTransient<ReproductorView>();
        services.AddTransient<DescargasView>();

        // 2. Aquí registraremos los ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<GaleriaViewModel>();
        services.AddTransient<DetalleViewModel>();
        services.AddTransient<CalendarioViewModel>();
        services.AddTransient<ReproductorViewModel>();
        services.AddTransient<DescargasViewModel>();

        // 3. Aquí registraremos los Servicios
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<IFileScannerService, FileScannerService>();
        services.AddHttpClient();
        services.AddSingleton<IDownloadService, DownloadService>();
        
        // IHttpClientFactory nativo con Polly para Rate Limiting
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>()
            .AddPolicyHandler(GetRetryPolicy());
        
        // Lo registramos como Singleton porque queremos que haya una sola conexión a la BD en toda la app
        services.AddSingleton<IDatabaseService, DatabaseService>();
    }

    private static Polly.IAsyncPolicy<System.Net.Http.HttpResponseMessage> GetRetryPolicy()
    {
        return Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryCount, response, context) =>
                {
                    var delay = TimeSpan.FromSeconds(60); // Por defecto AniList bloquea 1 minuto
                    if (response.Result?.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delay = response.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromSeconds(1));
                    }
                    return delay;
                },
                onRetryAsync: (outcome, timespan, retryCount, context) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[AniList Rate Limit] Esperando {timespan.TotalSeconds} segundos. Reintento {retryCount}...");
                    return System.Threading.Tasks.Task.CompletedTask;
                });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pedimos la instancia del servicio de base de datos
        var dbService = ServiceProvider.GetRequiredService<IDatabaseService>();
        
        // Inicializar reproductor de video nativo (Flyleaf)
        FlyleafLib.Engine.Start(new FlyleafLib.EngineConfig()
        {
            FFmpegPath = ":FFmpeg", // Usa las DLLs del paquete NuGet Flyleaf.FFmpeg
            UIRefresh = true
        });
        
        // Obligamos a que se cree el archivo y la tabla antes de continuar
        await dbService.InicializarBaseDatosAsync();

        // En lugar de que WPF abra la ventana automáticamente (StartupUri),
        // nosotros le pedimos al contenedor DI que nos construya la ventana
        // con todas sus dependencias ya inyectadas.
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}