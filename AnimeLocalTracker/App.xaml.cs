using System;
using System.Linq;
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
            AppLogger.Flush(); // el diálogo puede acabar en cierre: que el error ya esté en disco
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
            // El proceso muere justo después (y en ese caso ProcessExit no llega): sin esto el error que
            // tumbó la app se quedaba en la cola de memoria y nunca llegaba al registro.
            AppLogger.Flush();
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
        // Fase 2d: overlay para elegir un torrent a mano entre varios candidatos.
        services.AddSingleton<ISelectorTorrentService, SelectorTorrentService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IGamepadService, GamepadService>();
        services.AddSingleton<IPluginService, PluginService>();

        // Sin internet, todas las peticiones fallan al instante (SinConexionHandler, siempre la última pieza antes de la red):
        // antes cada consulta a AniList tardaba 60 s en rendirse y las pantallas esperaban ese minuto para mostrar lo guardado.
        services.AddSingleton(sp => new GuardiaConexion(sp.GetRequiredService<IConectividadRed>(), sinRedForzado: ConectividadRedWindows.SinRedForzado));
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConCorteSinConexion();

        // SEC-03: el cliente "Downloader" (scraper + descargas) no sigue redirects a ciegas:
        // cada salto se valida con UrlSeguridad (solo https, sin credenciales embebidas).
        // ConnectTimeout (incluye la negociación TLS): medido en uso real, a3.mp4upload.com tarda
        // 14–26 s solo en negociar la conexión segura; 15 s cortaba casi todas sus conexiones. 60 s
        // deja entrar a un servidor lento pero sigue fallando antes que los 100 s generales si está
        // caído. PooledConnectionLifetime renueva las conexiones reutilizadas (IPs del CDN que cambian).
        services.AddHttpClient("Downloader")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(60),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .AddHttpMessageHandler(() => new RedirectSeguroHandler())
            .ConCorteSinConexion();

        // Igual que "Downloader" pero pidiendo las páginas comprimidas (gzip/brotli): el HTML de
        // AnimeAv1 y el RSS de Nyaa llegan varias veces más pequeños y la búsqueda del episodio
        // termina antes. Solo para páginas: en el video, comprimir rompería las cuentas de bytes
        // de las descargas por trozos.
        services.AddHttpClient("Scraper")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            .AddHttpMessageHandler(() => new RedirectSeguroHandler())
            .ConCorteSinConexion();

        // Descargas por torrent (Nyaa.si + MonoTorrent, Fase MVP): último recurso opt-in
        // cuando ninguna fuente HTTP encuentra el episodio — ver AppSettings.BusquedaTorrentHabilitada.
        services.AddSingleton<INyaaSourceService>(sp =>
        {
            var db = sp.GetRequiredService<IDatabaseService>();
            // Partes anteriores del anime: los releases con numeración continua ("2nd Season - 14").
            return new NyaaSourceService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Scraper"),
                (id, _) => PrecuelasAnime.ObtenerAsync(db, id));
        });
        services.AddSingleton<ITorrentDownloadService>(sp =>
            new TorrentDownloadService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Downloader")));

        // Las descargas esperan a que vuelva internet en vez de gastar sus reintentos sin red.
        services.AddSingleton<IConectividadRed, ConectividadRedWindows>();
        // La cola se guarda en disco y se restaura al abrir la app (en pruebas no se pasa ruta y no se escribe nada).
        services.AddSingleton<IDownloadService>(sp => ActivatorUtilities.CreateInstance<DownloadService>(sp, AppDataPaths.ColaDescargasPath,
            (IConectividadRed)new ConectividadConfirmada(sp.GetRequiredService<GuardiaConexion>())));
        
        // IHttpClientFactory nativo con Polly: límite de peticiones de AniList + reintentos cortos (PoliticasHttp.AniList)
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>()
            .AddPolicyHandler(PoliticasHttp.AniList())
            .ConCorteSinConexion();
        
        services.AddHttpClient<IAniSkipService, AniSkipService>()
            .AddPolicyHandler(PoliticasHttp.AniList())
            .ConCorteSinConexion();

        // Openings/endings vía AnimeThemes.moe: catálogo (JSON) + descarga del audio (.ogg → .mp3).
        // Política propia (reintentos cortos): la de AniList espera 60 s y este cliente corta a los 20 s.
        // Respuestas comprimidas (el JSON de un anime largo baja de ~11 KB a ~3 KB) y listas guardadas en disco.
        services.AddHttpClient<IAnimeThemesService, AnimeThemesService>(http => new AnimeThemesService(http, AppDataPaths.AnimeThemesCatalogDir))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .AddPolicyHandler(AnimeThemesService.CrearPoliticaReintentos())
            .ConCorteSinConexion();
        services.AddSingleton<IAnimeThemesDownloadService, AnimeThemesDownloadService>();
        services.AddSingleton<IAudioDurationService, AudioDurationService>();
        // "Organizar mi música" (Configuración): nombre legible, etiquetas y portada a los mp3 descargados antes.
        services.AddSingleton<IOrganizadorMusicaService, OrganizadorMusicaService>();
        // Enlaces externos de la sección de música: cada fuente es un IProveedorEnlacesMusica (AniPlaylist de serie).
        services.AddSingleton<AnimeLocalTracker.Services.EnlacesMusica.IProveedorEnlacesMusica, AnimeLocalTracker.Services.EnlacesMusica.AniPlaylistProveedor>();
        services.AddSingleton<AnimeLocalTracker.Services.EnlacesMusica.IEnlacesMusicaService, AnimeLocalTracker.Services.EnlacesMusica.EnlacesMusicaService>();

        // Lo registramos como Singleton porque queremos que haya una sola conexión a la BD en toda la app
        services.AddSingleton<IDatabaseService, DatabaseService>();
        services.AddSingleton<IProximaEmisionService, ProximaEmisionService>();
        services.AddSingleton<IDatosExtraService, DatosExtraService>();
        services.AddSingleton<IEmisionMonitorService, EmisionMonitorService>();

        // Servicio de sincronización offline-online en segundo plano
        services.AddSingleton<ISyncService, SyncService>();
        // Calendario y Actualizaciones: programación de AniList con copia local para verla sin conexión.
        services.AddSingleton<IProgramacionEmisionService, ProgramacionEmisionService>();
        // Nombre y avatar de AniList con copia local (Galería y tarjeta Wrapped sin conexión).
        services.AddSingleton<IPerfilAniListService, PerfilAniListService>();
        // Indicador de conexión + sincronización inmediata al volver internet.
        services.AddSingleton<IEstadoConexionService, EstadoConexionService>();

        // Servicio de actualizaciones automáticas con Velopack y GitHub Releases
        services.AddSingleton<IUpdateService, UpdateService>();

        // Persistencia del progreso de reproducción (reanudar, guardar, auto-tracking)
        services.AddSingleton<IPlaybackStateService, PlaybackStateService>();

        // SMT-01: Controles Multimedia del Sistema (SMTC) — teclas de medios/auriculares
        // Bluetooth y overlay nativo de Windows. Un único SMTC por ventana principal.
        services.AddSingleton<ISystemMediaControlsService, SystemMediaControlsService>();

        services.AddSingleton<ISystemTrayService, SystemTrayService>();

        // Evita que Windows apague/proteja la pantalla mientras hay un video reproduciéndose.
        services.AddSingleton<IScreenSaverPreventionService, ScreenSaverPreventionService>();

        // Arranque automático con Windows (registro HKCU\...\Run).
        services.AddSingleton<IStartupService, StartupService>();

        // Tecla de pánico / modo discreto: hotkey global (RegisterHotKey) atado al HWND de MainWindow.
        services.AddSingleton<IPanicKeyService, PanicKeyService>();

        services.AddSingleton<IVideoIntegrityService, VideoIntegrityService>();

        // Logros por niveles: evaluación, persistencia de desbloqueos y avisos
        services.AddSingleton<AnimeLocalTracker.Services.Logros.ILogrosService, AnimeLocalTracker.Services.Logros.LogrosService>();

        // Franquicias (temporadas, películas, spin-offs) para el top de Estadísticas por tiempo visto
        services.AddSingleton<AnimeLocalTracker.Services.Franquicias.IFranquiciaService, AnimeLocalTracker.Services.Franquicias.FranquiciaService>();

        // Orquestación de skip-times (resolución MAL ID + reglas de evaluación)
        // Audio oficial de los OP/ED (AnimeThemes) para ubicarlos dentro de los episodios sin pasos manuales, y su caché en disco
        services.AddSingleton<IReferenciasAudioService, ReferenciasAudioService>();
        services.AddSingleton<ISkipTimesCoordinator, SkipTimesCoordinator>();
        services.AddSingleton<IMediaEnrichmentService, MediaEnrichmentService>();

        // Extraídos de ReproductorViewModel (fase 1 del refactor): navegación de episodios es
        // estado por sesión de reproducción (Transient, igual que ReproductorViewModel); la
        // captura de frame no tiene estado propio (Singleton, igual que SkipTimesCoordinator).
        services.AddTransient<IEpisodeNavigator, EpisodeNavigator>();
        services.AddSingleton<IFrameCaptureService, FrameCaptureService>();

        // Fase 2 del refactor: modo ventana, subtítulos y volumen/mute — ninguno tiene estado
        // por sesión de reproducción (Singleton, igual que FrameCaptureService).
        services.AddSingleton<IPlaybackWindowModeCoordinator, PlaybackWindowModeCoordinator>();
        services.AddSingleton<ISubtitleCoordinator, SubtitleCoordinator>();
        services.AddSingleton<ISubtitleCuesExtractorService, SubtitleCuesExtractorService>();
        services.AddSingleton<IPlaybackVolumeCoordinator, PlaybackVolumeCoordinator>();

        // Fase 3 del refactor: coalescing de seek — sí tiene estado por sesión de reproducción
        // (Transient, igual que EpisodeNavigator).
        services.AddTransient<IPlaybackSeekCoordinator, PlaybackSeekCoordinator>();
        // Caché de fotogramas clave de los últimos episodios abiertos: compartida entre reproductores (volver a abrir es instantáneo).
        services.AddSingleton<IFotogramasClaveService, FotogramasClaveService>();

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
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Scraper");
            // Anti-confusión: AniListId → MAL ID para verificar que la página del
            // episodio es del anime correcto (nombres parecidos ya no descargan
            // episodios equivocados).
            var aniSkip = sp.GetRequiredService<IAniSkipService>();
            var bridge = sp.GetRequiredService<IPythonBridgeService>();
            var tracking = sp.GetRequiredService<IAnimeTrackingService>();
            var db = sp.GetRequiredService<IDatabaseService>();

            return new AnimeAv1VideoSourceResolver(
                http,
                // MAL ID: primero el de la biblioteca (sin red, funciona aunque AniList no responda);
                // sin él, la verificación caía a comparar nombres, mucho menos segura.
                async (id, ct) =>
                {
                    try
                    {
                        if (await db.ObtenerAnimePorIdAsync(id) is { MalId: > 0 } local) return local.MalId;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("App", $"No se pudo leer el MAL ID local de {id}: {ex.Message}");
                    }
                    return await aniSkip.ObtenerMalIdDesdeAniListAsync(id, ct);
                },
                // Veredicto de nombres con rapidfuzz (daemon Python) sobre título+aka. En minúsculas
                // (rapidfuzz distingue mayúsculas: "BLACK TORCH" frente a "Black Torch" daba 27 %) y con
                // umbral 0 para que devuelva SIEMPRE su puntuación: con umbral, un rechazo llegaba como
                // "sin respuesta" y la app decidía con otro criterio más permisivo.
                async (titles, candidates, ct) =>
                {
                    try
                    {
                        if (!await bridge.IsAvailableAsync()) return null;
                        var r = await bridge.ExecuteCommandAsync<object, MatchMediaResult>(
                            "match-media",
                            new
                            {
                                titles = titles.Select(t => t.ToLowerInvariant()).ToList(),
                                candidates = candidates.Select(c => c.ToLowerInvariant()).ToList(),
                                threshold = 0.0
                            },
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
                },
                // Página verificada de cada anime: el primer episodio tras reiniciar la app no repite la búsqueda
                db,
                // Partes anteriores (relaciones de AniList ya guardadas): el sitio junta a veces
                // "2nd Season" y "2nd Season Part 2" en una sola página y hay que desplazar el episodio.
                (id, _) => PrecuelasAnime.ObtenerAsync(db, id));
        });
        services.AddSingleton<ProveedorVideoAnimeAv1>();
        services.AddSingleton<IVideoSourceResolver>(sp =>
        {
            // Plugins C# "drop-in": cualquier IProveedorVideo hallado en la carpeta de plugins
            // se suma a los proveedores nativos — el orquestador no distingue entre ambos.
            var proveedores = new List<IProveedorVideo> { sp.GetRequiredService<ProveedorVideoAnimeAv1>() };
            // SEC-01: un .dll solo se carga si los plugins están activados Y el usuario confió en ese archivo con su
            // huella SHA-256 actual (Configuración → Plugins). Por defecto no se carga ninguno.
            var settingsPlugins = sp.GetRequiredService<ISettingsService>();
            proveedores.AddRange(CSharpPluginLoader.CargarProveedoresVideo(
                AppDataPaths.PluginsFolder,
                esConfiable: ruta => AnimeLocalTracker.Core.ConfianzaPlugins.PuedeEjecutarse(settingsPlugins.ObtenerConfiguracion(), ruta)));
            return new OrquestadorMultiProveedor(proveedores);
        });

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
        services.AddSingleton<LogrosViewModel>();
        // Visor de registros: singleton para conservar archivo y filtros elegidos entre visitas.
        services.AddSingleton<VisorRegistrosViewModel>();

        // Minijuegos (singleton: una partida en curso sobrevive al cambiar de pestaña)
        services.AddSingleton<AnimeLocalTracker.Services.Minijuegos.IClipPlayer, AnimeLocalTracker.Services.Minijuegos.ClipPlayer>();
        services.AddSingleton<AnimeLocalTracker.Services.Minijuegos.IMinijuegosRecordsService, AnimeLocalTracker.Services.Minijuegos.MinijuegosRecordsService>();
        services.AddSingleton<AdivinaAnimeViewModel>();
        services.AddSingleton<AdivinaOpEdViewModel>();
        services.AddHttpClient<IPersonajesService, PersonajesService>().ConCorteSinConexion(); // sin reintentos largos: un juego no espera minutos a AniList
        services.AddSingleton<AdivinaPersonajeViewModel>();
        services.AddSingleton<MinijuegosViewModel>();

        // Historial de reproducción
        services.AddSingleton<HistorialViewModel>();

        // Actualizaciones (episodios recién emitidos con descarga directa)
        services.AddSingleton<ActualizacionesViewModel>();
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
                // Registro detallado (entradas DEBUG en disco): ajuste del usuario o variable de entorno.
                if (settingsService.ObtenerConfiguracion()?.RegistroDetallado == true) AppLogger.RegistroDetallado = true;
            }
            catch { }

            // Pedimos la instancia del servicio de base de datos
            var dbService = ServiceProvider.GetRequiredService<IDatabaseService>();

            // PERF-01: el motor de video (Flyleaf) ya NO se inicia aquí: cargar FFmpeg cuesta 0,4-0,55 s y
            // retrasaba la aparición de la ventana. Se inicia justo después de mostrarla (ver
            // IniciarMotorFlyleafTrasMostrarVentana).

            // Obligamos a que se cree el archivo y la tabla antes de continuar
            await dbService.InicializarBaseDatosAsync();

            // Backup rotativo de la biblioteca (protección contra corrupción/pérdida).
            // BAK-02: se dispara en segundo plano para no retrasar la aparición de la
            // ventana; DatabaseService captura y registra sus propios errores.
            _ = dbService.CrearBackupRotativoAsync();

            // Las vistas previas de música (openings/endings escuchados sin guardar) son temporales: se vacían al arrancar.
            // En segundo plano y antes de que nadie pueda pedir una nueva (aún no hay ficha abierta).
            var descargasMusica = ServiceProvider.GetRequiredService<IAnimeThemesDownloadService>();
            _ = System.Threading.Tasks.Task.Run(descargasMusica.LimpiarVistasPrevias);

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

            // Avisos y descarga automática de episodios nuevos (animes con la opción activada en su ficha).
            ServiceProvider.GetRequiredService<IEmisionMonitorService>().Iniciar();

            // Indicador de conexión: al volver internet sincroniza al momento lo hecho sin conexión.
            ServiceProvider.GetRequiredService<IEstadoConexionService>().Iniciar();
            // Descargas que quedaron pendientes al cerrar la app: vuelven a la cola (las pausadas, en pausa).
            _ = Task.Run(() =>
            {
                var descargas = ServiceProvider.GetRequiredService<IDownloadService>();
                try { descargas.RestaurarColaPendiente(); }
                catch (Exception ex) { AppLogger.Warn("App", $"No se pudo restaurar la cola de descargas: {ex.Message}"); }
                // Después de restaurar (la cola dice qué carpetas siguen en uso): restos de torrents abandonados.
                try { descargas.LimpiarTemporalesTorrentHuerfanos(); }
                catch (Exception ex) { AppLogger.Warn("App", $"No se pudieron limpiar los torrents abandonados: {ex.Message}"); }
            });

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
            IniciarMotorFlyleafTrasMostrarVentana();

            // Arranque con Windows (StartupService añade "--bandeja" al comando del registro):
            // ocultar directo a la bandeja para no interrumpir el inicio de sesión con una ventana.
            if (e.Args.Any(a => string.Equals(a, "--bandeja", StringComparison.OrdinalIgnoreCase)))
            {
                ServiceProvider.GetRequiredService<ISystemTrayService>().IniciarEnBandeja();
            }

            // Iniciar servicio de gamepad
            var gamepad = ServiceProvider.GetService<IGamepadService>();
            gamepad?.Iniciar();

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

    /// <summary>
    /// Inicializa el motor de video nativo (Flyleaf) cuando la interfaz ya está pintada y en reposo.
    /// Engine.Start exige el hilo de UI y no tiene versión asíncrona útil (StartAsync también bloquea ~0,5 s
    /// antes de volver y deja IsLoaded=false), así que en vez de sacarlo del hilo se retrasa hasta que la
    /// ventana ya es visible. Es idempotente: si el usuario abre un video antes, ReproductorViewModel lo
    /// inicia por su cuenta y esta llamada posterior cuesta 0 ms. NO es fatal: si falla, el reproductor
    /// degrada (CreateOptimizedPlayer lo maneja).
    /// </summary>
    private void IniciarMotorFlyleafTrasMostrarVentana()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
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
        });
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