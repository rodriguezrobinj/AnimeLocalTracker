using System;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Punto único de creación de ViewModels para la navegación (ARC-02).
/// Centraliza el acceso al contenedor DI que antes vivía en MainViewModel:
/// los ViewModels ya no reciben IServiceProvider.
/// </summary>
public interface INavigationService
{
    GaleriaViewModel ObtenerGaleria();
    AgregarAnimeViewModel ObtenerAgregarAnime();
    CalendarioViewModel ObtenerCalendario();
    DescargasViewModel ObtenerDescargas();
    ConfiguracionViewModel ObtenerConfiguracion();
    AcercaDeViewModel ObtenerAcercaDe();
    EstadisticasViewModel ObtenerEstadisticas();
    HistorialViewModel ObtenerHistorial();
    DetalleViewModel CrearDetalle();
    ReproductorViewModel CrearReproductor();
}

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public GaleriaViewModel ObtenerGaleria() => _serviceProvider.GetRequiredService<GaleriaViewModel>();
    public AgregarAnimeViewModel ObtenerAgregarAnime() => _serviceProvider.GetRequiredService<AgregarAnimeViewModel>();
    public CalendarioViewModel ObtenerCalendario() => _serviceProvider.GetRequiredService<CalendarioViewModel>();
    public DescargasViewModel ObtenerDescargas() => _serviceProvider.GetRequiredService<DescargasViewModel>();
    public ConfiguracionViewModel ObtenerConfiguracion() => _serviceProvider.GetRequiredService<ConfiguracionViewModel>();
    public AcercaDeViewModel ObtenerAcercaDe() => _serviceProvider.GetRequiredService<AcercaDeViewModel>();
    public EstadisticasViewModel ObtenerEstadisticas() => _serviceProvider.GetRequiredService<EstadisticasViewModel>();
    public HistorialViewModel ObtenerHistorial() => _serviceProvider.GetRequiredService<HistorialViewModel>();
    public DetalleViewModel CrearDetalle() => _serviceProvider.GetRequiredService<DetalleViewModel>();
    public ReproductorViewModel CrearReproductor() => _serviceProvider.GetRequiredService<ReproductorViewModel>();
}
