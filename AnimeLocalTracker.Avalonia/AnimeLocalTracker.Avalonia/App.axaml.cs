using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AnimeLocalTracker.Avalonia.Services;
using AnimeLocalTracker.Core.Services;
using AnimeLocalTracker.Core.ViewModels;
using AnimeLocalTracker.Avalonia.Views;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── Inicialización del BACKEND (equivalente a App.xaml.cs OnStartup del WPF) ──

        // 1. Motor de video LibVLC
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "No se pudo inicializar LibVLC", ex);
        }

        // 2. Contenedor DI completo
        var services = Bootstrapper.ConfigureServices();

        // 3. Puente Core↔UI: todo lo que venga de hilos de background llega al hilo de UI
        CoreDispatcher.Current = new AvaloniaDispatcherService();

        // 4. Inicializar el thread de la UI y lanzar la inicialización asíncrona del backend
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = services.GetRequiredService<MainViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            // 5. Inicialización asíncrona (no bloquea el hilo de UI): BD → sync → updates
            _ = InicializarBackendAsync(services);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var mainViewModel = services.GetRequiredService<MainViewModel>();

            singleViewPlatform.MainView = new MainView
            {
                DataContext = mainViewModel
            };

            _ = InicializarBackendAsync(services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Inicializa el backend en segundo plano sin bloquear el arranque de la UI:
    /// base de datos, sincronización periódica y verificación de actualizaciones.
    /// </summary>
    private async Task InicializarBackendAsync(IServiceProvider services)
    {
        try
        {
            // 1. Base de datos (necesita estar lista antes de cualquier consulta de los ViewModels)
            var dbService = services.GetRequiredService<IDatabaseService>();
            await dbService.InicializarBaseDatosAsync();

            // 2. Sincronización periódica (equivalente: cada 5 min)
            var syncService = services.GetRequiredService<ISyncService>();
            syncService.IniciarSincronizacionPeriodica(TimeSpan.FromMinutes(5));

            // 3. Verificación de actualizaciones automáticas (equivalente: cada 4 horas)
            var updateService = services.GetRequiredService<IUpdateService>();
            updateService.IniciarVerificacionSegundoPlano(TimeSpan.FromHours(4));

            AppLogger.Info("App", "Backend inicializado correctamente (BD, sync y updates).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "Error al inicializar el backend", ex);
        }
    }
}
