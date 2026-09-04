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

    /// <summary>DTO del veredicto de nombres del daemon (match-media).</summary>
    private sealed class MatchMediaResult
    {
        public bool Success { get; set; }
        public double Score { get; set; }
        public string? MatchedTitle { get; set; }
    }

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

    internal static void ConfigureServices(IServiceCollection services)
    {
        // 1. Ventana principal (ARC-04): las vistas de página ya NO se registran en DI —
        // se resuelven con las DataTemplates VM→Vista de App.xaml (los registros estaban
        // muertos: MainWindow las creaba con `new` y nunca se resolvían).
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IVentanaPrincipal>(sp => sp.GetRequiredService<MainWindow>());

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

        // ARC-02: la navegación resuelve ViewModels a través de un único servicio;
        // los ViewModels ya no reciben IServiceProvider.
        services.AddSingleton<INavigationService, NavigationService>();

        // 3. Aquí registraremos los Servicios
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddHttpClient();

        // SEC-03: el cliente "Downloader" (scraper + descargas) no sigue redirects a ciegas:
        // cada salto se valida con UrlSeguridad (solo https, sin credenciales embebidas).
        services.AddHttpClient("Downloader")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler(() => new RedirectSeguroHandler());
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
        services.AddSingleton<IMediaEnrichmentService, MediaEnrichmentService>();

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
            var bridge = sp.GetRequiredService<IPythonBridgeService>();
            var tracking = sp.GetRequiredService<IAnimeTrackingService>();

            return new AnimeAv1VideoSourceResolver(
                http,
                (id, ct) => aniSkip.ObtenerMalIdDesdeAniListAsync(id, ct),
                // Veredicto de nombres con rapidfuzz (daemon Python) sobre título+aka
                async (titles, candidates, ct) =>
                {
                    try
                    {
                        if (!await bridge.IsAvailableAsync()) return null;
                        var r = await bridge.ExecuteCommandAsync<object, MatchMediaResult>(
                            "match-media",
                            new { titles, candidates, threshold = 75.0 },
                            ct);
                        return r?.Success == true ? r.Score / 100.0 : null;
                    }
                    catch
                    {
                        return null;
                    }
                },
                // Títulos adicionales desde AniList: native japonés, synonyms, etc.
                // — la búsqueda no depende de lo que la biblioteca local guarde
                async (id, ct) =>
                {
                    try
                    {
                        var anime = await tracking.ObtenerAnimePorIdAsync(id);
                        if (anime?.Title == null) return (List<string>?)null;

                        var titulos = new List<string>();
                        if (!string.IsNullOrWhiteSpace(anime.Title.Romaji)) titulos.Add(anime.Title.Romaji);
                        if (!string.IsNullOrWhiteSpace(anime.Title.English)) titulos.Add(anime.Title.English!);
                        if (!string.IsNullOrWhiteSpace(anime.Title.Native)) titulos.Add(anime.Title.Native!);
                        if (!string.IsNullOrWhiteSpace(anime.Title.UserPreferred)) titulos.Add(anime.Title.UserPreferred!);
                        if (anime.Synonyms != null) titulos.AddRange(anime.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
                        return titulos.Distinct().ToList();
                    }
                    catch
                    {
                        return (List<string>?)null;
                    }
                });
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

        // Historial de reproducción
        services.AddSingleton<HistorialViewModel>();
    }

    private static Polly.IAsyncPolicy<System.Net.Http.HttpResponseMessage> GetRetryPolicy()
    {
        var circuitBreaker = Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromMinutes(2),
                onBreak: (result, timespan) => AppLogger.Warn("AniListTrackingService", $"Circuit Breaker ABIERTO por {timespan.TotalSeconds}s"),
                onReset: () => AppLogger.Info("AniListTrackingService", "Circuit Breaker RESET CERRADO"),
                onHalfOpen: () => AppLogger.Info("AniListTrackingService", "Circuit Breaker MEDIO ABIERTO")
            );

        var random = new Random();
        var retry = Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            // Permitir que el retry no falle de inmediato si el breaker está abierto (espera y reintenta)
            .Or<Polly.CircuitBreaker.BrokenCircuitException>() 
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryCount, response, context) =>
                {
                    var delay = TimeSpan.FromSeconds(60); // Por defecto AniList bloquea 1 minuto
                    if (response?.Result?.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delay = response.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromSeconds(1));
                    }
                    // Jitter para evitar thundering herd
                    return delay.Add(TimeSpan.FromMilliseconds(random.Next(500, 2000)));
                },
                onRetryAsync: (outcome, timespan, retryCount, context) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[AniList Rate Limit] Esperando {timespan.TotalSeconds} segundos. Reintento {retryCount}...");
                    return System.Threading.Tasks.Task.CompletedTask;
                });

        return Polly.Policy.WrapAsync(retry, circuitBreaker);
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
            // OPS-05: la primera línea del log identifica versión y entorno del binario
            // (antes no había forma de saber qué build produjo un app.log).
            string versionApp = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida";
            string arquitectura = System.Environment.Is64BitProcess ? "x64" : "x86";
            AppLogger.Info("App", $"AnimeLocalTracker iniciando (versión {versionApp}, {arquitectura}).");

            // Migrar datos del layout de instalación antiguo (%LocalAppData%\AnimeLocalTracker)
            // a la carpeta segura de datos, ANTES de inicializar la base de datos.
            AppDataPaths.MigrarDesdeInstalacionAntigua();

            // Aplicar el idioma guardado (ES/EN) antes de construir la UI
            ISettingsService? settingsService = null;
            try
            {
                settingsService = ServiceProvider.GetRequiredService<ISettingsService>();
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

            // Iniciar sincronización periódica en segundo plano.
            // FUN-005: el intervalo es el configurado (ya no 5 min fijos) y se reinicia al guardar.
            var syncService = ServiceProvider.GetRequiredService<ISyncService>();
            var configuracion = settingsService?.ObtenerConfiguracion();
            int intervaloMinutos = Math.Clamp(configuracion?.IntervaloSincronizacionMinutos ?? 5, 1, 1440);
            syncService.IniciarSincronizacionPeriodica(TimeSpan.FromMinutes(intervaloMinutos));
            if (settingsService != null)
            {
                settingsService.ConfiguracionModificada += cfg =>
                {
                    if (cfg?.IntervaloSincronizacionMinutos > 0)
                    {
                        syncService.IniciarSincronizacionPeriodica(TimeSpan.FromMinutes(cfg.IntervaloSincronizacionMinutos));
                        AppLogger.Info("App", $"Intervalo de sincronización actualizado a {cfg.IntervaloSincronizacionMinutos} min.");
                    }
                };
            }

            // Verificación de actualizaciones automáticas en segundo plano (4 h), salvo que
            // el usuario la desactive con "Buscar actualizaciones al iniciar".
            var updateService = ServiceProvider.GetRequiredService<IUpdateService>();
            if (configuracion?.BuscarActualizacionesAlIniciar ?? true)
            {
                updateService.IniciarVerificacionSegundoPlano(TimeSpan.FromHours(4));
            }
            else
            {
                AppLogger.Info("App", "Comprobación automática de actualizaciones desactivada por configuración.");
            }

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

            // Notificaciones de episodios nuevos: primer chequeo a los 3 s y luego cada
            // 30 min mientras la app esté abierta (FUN-008: antes solo UNA vez al arrancar).
            var notifier = ServiceProvider.GetService<NewEpisodeNotifier>();
            notifier?.IniciarMonitoreoPeriodico(TimeSpan.FromMinutes(30));
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