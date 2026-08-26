using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using AnimeLocalTracker.Core.ViewModels;
using AnimeLocalTracker.Core.Services;
using Polly;

using AnimeLocalTracker.Avalonia.Services;

namespace AnimeLocalTracker.Avalonia;

public static class Bootstrapper
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddSingleton<GaleriaViewModel>();
        services.AddTransient<ReproductorViewModel>();
        services.AddSingleton<AgregarAnimeViewModel>();
        services.AddTransient<DetalleViewModel>();
        services.AddSingleton<CalendarioViewModel>();
        services.AddSingleton<DescargasViewModel>();
        services.AddSingleton<ConfiguracionViewModel>();
        services.AddSingleton<AcercaDeViewModel>();

        // Services
        services.AddSingleton<IDispatcherService, AvaloniaDispatcherService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDialogService, Services.DialogService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddTransient<IFileScannerService, FileScannerService>();
        services.AddHttpClient();
        services.AddSingleton<IDownloadService, DownloadService>();
        
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>()
            .AddPolicyHandler(GetRetryPolicy());
        
        services.AddHttpClient<IAniSkipService, AniSkipService>()
            .AddPolicyHandler(GetRetryPolicy());
        
        services.AddSingleton<IDatabaseService, DatabaseService>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IUpdateService, Services.UpdateService>();
        services.AddSingleton<IPlaybackStateService, PlaybackStateService>();
        services.AddSingleton<ISkipTimesCoordinator, SkipTimesCoordinator>();
        services.AddSingleton<IDownloadStateStore, DownloadStateStore>();
        services.AddSingleton<IVideoSourceResolver>(sp =>
            new AnimeAv1VideoSourceResolver(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Downloader")));
        services.AddSingleton<IImageCacheService, ImageCacheService>();

        return services.BuildServiceProvider();
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
                    var delay = TimeSpan.FromSeconds(60);
                    if (response.Result?.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        delay = response.Result.Headers.RetryAfter.Delta.Value.Add(TimeSpan.FromSeconds(1));
                    }
                    return delay;
                },
                onRetryAsync: (outcome, timespan, retryCount, context) =>
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                });
    }
}
