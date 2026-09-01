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
            // SEC-12: no exponer mensajes internos (rutas, versiones, drivers) en la UI;
            // el detalle completo queda en el log de la aplicación.
            MessageBox.Show("Ocurrió un error inesperado.\nEl detalle técnico se ha guardado en el registro de la aplicación.",
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

        // 4. DATA-01: en cierre brusco (Task Manager, crash, update forzado de Velopack)
        // OnExit NO se ejecuta → el daemon Python quedaría huérfano y bloquearía el
        // directorio de instalación (update falla con "Failed to remove existing
        // application directory"). ProcessExit se dispara en casi todos los cierres:
        // matar el daemon aquí garantiza que nunca quede bloqueando la app.
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try
            {
                ServiceProvider?.GetService<IPythonBridgeService>()?.Dispose();
            }
            catch { }
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
        // ── PROVEEDORES DE VIDEO (Fase A multi-fuente) ──
        // La app ya no depende de una sola fuente: cada proveedor es un
        // IProveedorVideo intercambiable y el orquestador los prueba por
        // prioridad con degradación por salud (fallos → cooldown → reintento).
        services.AddSingleton<AnimeAv1VideoSourceResolver>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Downloader");
            // Anti-confusión: AniListId → MAL ID para verificar que la página del
            // episodio es del anime correcto (nombres parecidos ya no descargan
            // episodios equivocados).
            var aniSkip = sp.GetRequiredService<IAniSkipService>();
            return new AnimeAv1VideoSourceResolver(http, (id, ct) => aniSkip.ObtenerMalIdDesdeAniListAsync(id, ct));
        });
        services.AddSingleton<ProveedorVideoAnimeAv1>();
        services.AddSingleton<IVideoSourceResolver>(sp => new OrquestadorMultiProveedor(
            new IProveedorVideo[]
            {
                sp.GetRequiredService<ProveedorVideoAnimeAv1>()
            }));

        // Persistencia del estado de descargas segmentadas (.state)
        services.AddSingleton<IDownloadStateStore, DownloadStateStore>();

        // Servicio de caché y precarga de imágenes optimizadas para 60fps
        services.AddSingleton<IImageCacheService, ImageCacheService>();

        // ARQ-02: alta de animes unificada (MainViewModel + AgregarAnimeViewModel)
        services.AddSingleton<AnimeLibraryService>();

        // Mantenimiento de caché (miniaturas/portadas huérfanas)
        services.AddSingleton<CacheMaintenanceService>();

        // Notificaciones de episodios nuevos
        services.AddSingleton<NewEpisodeNotifier>();

        // Estadísticas personales
        services.AddSingleton<EstadisticasViewModel>();
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

    private static System.Threading.Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 0. Instancia única: Evitar colisiones de puertos (OAuth 5050), locks de base de datos y settings
        const string mutexName = "Global\\AnimeLocalTracker_SingleInstance_Mutex";
        try
        {
            _singleInstanceMutex = new System.Threading.Mutex(true, mutexName, out bool esPrimeraInstancia);
            if (!esPrimeraInstancia)
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }
        }
        catch
        {
            // Si la creación del Mutex global falla por permisos, continuar sin bloquear el arranque
        }

        base.OnStartup(e);

        try
        {
            // Migrar datos del layout de instalación antiguo (%LocalAppData%\AnimeLocalTracker)
            // a la carpeta segura de datos, ANTES de inicializar la base de datos.
            AppDataPaths.MigrarDesdeInstalacionAntigua();

            // Aplicar el idioma guardado (ES/EN) antes de construir la UI
            try
            {
                var settingsService = ServiceProvider.GetRequiredService<ISettingsService>();
                LocalizationService.Instance.Idioma = settingsService.ObtenerConfiguracion()?.Idioma ?? "es";
            }
            catch { }

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

            // Backup rotativo de la biblioteca (protección contra corrupción/pérdida).
            // BAK-02: se dispara en segundo plano para no retrasar la aparición de la
            // ventana; DatabaseService captura y registra sus propios errores.
            _ = dbService.CrearBackupRotativoAsync();

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

            // Notificaciones de episodios nuevos (diferido para no competir con la carga)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000);
                    var notifier = ServiceProvider.GetService<NewEpisodeNotifier>();
                    if (notifier != null)
                    {
                        await notifier.BuscarYNotificarNuevosAsync();
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "Error fatal durante el arranque", ex);
            MessageBox.Show($"No se pudo iniciar la aplicación:\n{ex.Message}",
                            "Error de inicio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Graceful shutdown: limpiar y terminar procesos secundarios daemon
            var pythonBridge = ServiceProvider?.GetService<IPythonBridgeService>();
            pythonBridge?.Dispose();
        }
        catch { }

        try
        {
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch { }

        base.OnExit(e);
    }
}