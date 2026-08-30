using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using AnimeLocalTracker.Views; // Asegúrate de que este namespace exista
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using Polly;

namespace AnimeLocalTracker;

public partial class App : Application
{
    // Este es nuestro contenedor global de dependencias
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public App()
    {
        try
        {
            Velopack.VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Velopack", $"Aviso en inicialización de Velopack: {ex.Message}");
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
        
        // === MANEJADORES GLOBALES DE EXCEPCIONES ===
        
        // 1. Excepciones no manejadas en el hilo de UI (Dispatcher)
        this.DispatcherUnhandledException += (s, args) =>
        {
            AppLogger.Error("App", "UI Thread Exception", args.Exception);
            string detalleError = args.Exception.InnerException != null 
                ? $"{args.Exception.Message}\n\nDetalle: {args.Exception.InnerException.Message}"
                : args.Exception.Message;
            MessageBox.Show($"Error en la aplicación:\n{detalleError}", 
                            "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // Prevenir cierre inesperado
        };
        
        // 2. Excepciones no manejadas en hilos secundarios (fatal)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("App", "Domain Unhandled Exception (Fatal)", ex);
            }
            else
            {
                AppLogger.Error("App", $"Domain Unhandled Exception: {args.ExceptionObject}");
            }
        };
        
        // 3. Excepciones de Tasks async no observadas
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLogger.Error("App", "Unobserved Task Exception", args.Exception);
            args.SetObserved(); // Marcar como observada para prevenir cierre
        };
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. Registramos las Vistas (Ventanas)
        services.AddTransient<MainWindow>();
        services.AddTransient<AgregarAnimeView>();
        services.AddTransient<ReproductorView>();
        services.AddTransient<DescargasView>();
        services.AddTransient<ConfiguracionView>();
        services.AddTransient<AcercaDeView>();

        // 2. Registramos los ViewModels (Vistas principales como Singleton para preservar estado y no repetir queries al cambiar de pestaña)
        services.AddTransient<MainViewModel>();
        services.AddSingleton<GaleriaViewModel>();
        services.AddSingleton<AgregarAnimeViewModel>();
        services.AddTransient<DetalleViewModel>();
        services.AddSingleton<CalendarioViewModel>();
        services.AddTransient<ReproductorViewModel>();
        services.AddSingleton<DescargasViewModel>();
        services.AddSingleton<ConfiguracionViewModel>();
        services.AddSingleton<AcercaDeViewModel>();

        // 3. Aquí registraremos los Servicios
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<IFileScannerService, FileScannerService>();
        services.AddHttpClient();
        services.AddSingleton<IDownloadService, DownloadService>();
        
        // IHttpClientFactory nativo con Polly para Rate Limiting
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>()
            .AddPolicyHandler(GetRetryPolicy());
        
        services.AddHttpClient<IAniSkipService, AniSkipService>()
            .AddPolicyHandler(GetRetryPolicy());
        
        // Lo registramos como Singleton porque queremos que haya una sola conexión a la BD en toda la app
        services.AddSingleton<IDatabaseService, DatabaseService>();

        // Servicio de sincronización offline-online en segundo plano
        services.AddSingleton<ISyncService, SyncService>();

        // Servicio de actualizaciones automáticas con Velopack y GitHub Releases
        services.AddSingleton<IUpdateService, UpdateService>();

        // Persistencia del progreso de reproducción (reanudar, guardar, auto-tracking)
        services.AddSingleton<IPlaybackStateService, PlaybackStateService>();

        // Orquestación de skip-times (resolución MAL ID + reglas de evaluación)
        services.AddSingleton<ISkipTimesCoordinator, SkipTimesCoordinator>();

        // 4. Integración del Ecosistema de Automatización Python (Zero-Setup & Clean Architecture)
        services.AddSingleton<IPythonBridgeService, PythonBridgeService>();
        services.AddSingleton<PythonEpisodeEnricher>();
        services.AddTransient<IFileScannerService, PythonFileScannerService>();
        services.AddSingleton<IVideoSourceResolver, PythonVideoSourceResolver>();

        // Persistencia del estado de descargas segmentadas (.state)
        services.AddSingleton<IDownloadStateStore, DownloadStateStore>();

        // Servicio de caché y precarga de imágenes optimizadas para 60fps
        services.AddSingleton<IImageCacheService, ImageCacheService>();

        // Servicio de miniaturas de previsualización en hover para la línea de tiempo
        services.AddSingleton<IHoverThumbnailService, HoverThumbnailService>();
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

        try
        {
            // Pedimos la instancia del servicio de base de datos
            var dbService = ServiceProvider.GetRequiredService<IDatabaseService>();

            // Inicializar reproductor de video nativo (Flyleaf).
            // NO es fatal: si falla, el reproductor degrada gracefully (CreateOptimizedPlayer lo maneja).
            try
            {
                FlyleafLib.Engine.Start(new FlyleafLib.EngineConfig()
                {
                    FFmpegPath = ":FFmpeg", // Usa las DLLs del paquete NuGet Flyleaf.FFmpeg
                    UIRefresh = true
                });
            }
            catch (Exception flyleafEx)
            {
                AppLogger.Error("App", "No se pudo iniciar el motor Flyleaf (el reproductor quedará degradado)", flyleafEx);
            }

            // Obligamos a que se cree el archivo y la tabla antes de continuar
            await dbService.InicializarBaseDatosAsync();

            // Iniciar sincronización periódica en segundo plano
            var syncService = ServiceProvider.GetRequiredService<ISyncService>();
            syncService.IniciarSincronizacionPeriodica(TimeSpan.FromMinutes(5));

            // Iniciar verificación de actualizaciones automáticas en segundo plano
            var updateService = ServiceProvider.GetRequiredService<IUpdateService>();
            updateService.IniciarVerificacionSegundoPlano(TimeSpan.FromHours(4));

            // Pre-calentar el demonio de Python en segundo plano (Zero-Lag en primera entrada a un anime)
            _ = Task.Run(async () =>
            {
                try
                {
                    var pythonBridge = ServiceProvider.GetService<IPythonBridgeService>();
                    if (pythonBridge != null)
                    {
                        await pythonBridge.IsAvailableAsync();
                    }
                }
                catch { }
            });

            // En lugar de que WPF abra la ventana automáticamente (StartupUri),
            // nosotros le pedimos al contenedor DI que nos construya la ventana
            // con todas sus dependencias ya inyectadas.
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "Error fatal durante el arranque", ex);
            MessageBox.Show($"No se pudo iniciar la aplicación:\n{ex.Message}",
                            "Error de inicio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}