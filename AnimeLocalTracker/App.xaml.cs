using System;
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
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. Registramos las Vistas (Ventanas)
        services.AddTransient<MainWindow>();

        // 2. Aquí registraremos los ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<GaleriaViewModel>();
        services.AddTransient<DetalleViewModel>();
        services.AddTransient<CalendarioViewModel>();

        // 3. Aquí registraremos los Servicios
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<IFileScannerService, FileScannerService>();
        
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

    private System.Diagnostics.Process? _goDaemonProcess;
    private System.Diagnostics.Process? _pythonDaemonProcess;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Manejo Global de Errores
        this.DispatcherUnhandledException += (s, args) => 
        {
            args.Handled = true; // Evita que se cierre la app
            MessageBox.Show("Ha ocurrido un error inesperado.\n\nDetalles técnicos:\n" + args.Exception.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        
        // === FASE 2: ORQUESTACIÓN DEL DAEMON GO ===
        try
        {
            string baseDir = AppContext.BaseDirectory;
            // Calcular ruta al ejecutable (sube 4 niveles desde bin/Debug/net8.0-windows/ y entra a BackendGo)
            string goDaemonPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, @"..\..\..\..\BackendGo\tracker_daemon.exe"));
            
            if (System.IO.File.Exists(goDaemonPath))
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = goDaemonPath,
                    UseShellExecute = false,
                    CreateNoWindow = true // Ejecutar de manera invisible
                };
                _goDaemonProcess = System.Diagnostics.Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error al iniciar Go Daemon: {ex.Message}");
        }

        // === FASE 3: ORQUESTACIÓN DEL DAEMON PYTHON ===
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string pythonExe = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, @"..\..\..\..\BackendPython\venv\Scripts\python.exe"));
            string pythonScript = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, @"..\..\..\..\BackendPython\main.py"));
            
            if (System.IO.File.Exists(pythonExe) && System.IO.File.Exists(pythonScript))
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{pythonScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true, // Ejecutar de manera invisible
                    WorkingDirectory = System.IO.Path.GetDirectoryName(pythonScript)
                };
                _pythonDaemonProcess = System.Diagnostics.Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error al iniciar Python Daemon: {ex.Message}");
        }
        
        // Pedimos la instancia del servicio de base de datos
        var dbService = ServiceProvider.GetRequiredService<IDatabaseService>();
        
        // Obligamos a que se cree el archivo y la tabla antes de continuar
        await dbService.InicializarBaseDatosAsync();

        // En lugar de que WPF abra la ventana automáticamente (StartupUri),
        // nosotros le pedimos al contenedor DI que nos construya la ventana
        // con todas sus dependencias ya inyectadas.
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // === MATAR DAEMON GO ===
        if (_goDaemonProcess != null && !_goDaemonProcess.HasExited)
        {
            try
            {
                _goDaemonProcess.Kill();
            }
            catch { /* Ignorar errores al matar proceso */ }
        }
        
        // === MATAR DAEMON PYTHON ===
        if (_pythonDaemonProcess != null && !_pythonDaemonProcess.HasExited)
        {
            try
            {
                _pythonDaemonProcess.Kill();
            }
            catch { /* Ignorar errores al matar proceso */ }
        }
        
        base.OnExit(e);
    }
}