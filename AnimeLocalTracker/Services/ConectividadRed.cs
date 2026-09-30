using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace AnimeLocalTracker.Services;

/// <summary>
/// ¿Hay internet ahora mismo? Lo usan las descargas para no gastar sus reintentos mientras
/// la conexión está caída (wifi que se corta, router reiniciándose) y seguir solas al volver.
/// </summary>
public interface IConectividadRed
{
    bool HayInternet { get; }

    /// <summary>
    /// Espera hasta que haya internet o pase <paramref name="maximo"/> (lo que ocurra antes).
    /// Devuelve true si hay internet al terminar.
    /// </summary>
    Task<bool> EsperarInternetAsync(TimeSpan maximo, CancellationToken ct);
}

/// <summary>
/// Implementación con el mismo indicador que usa Windows para el icono de red de la barra de
/// tareas ("Sin acceso a internet"). Una simple interfaz de red activa no basta: los adaptadores
/// virtuales (Hyper-V, VPN) siguen "activos" aunque el wifi se haya caído.
/// </summary>
public sealed class ConectividadRedWindows : IConectividadRed
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Para probar la app sin internet sin desconectar el equipo: con la variable de entorno ANIMELOCALTRACKER_SIN_RED=1 la app se
    /// comporta como si no hubiera conexión (ninguna petición sale, ver <see cref="GuardiaConexion"/>).
    /// </summary>
    public static bool SinRedForzado { get; } = Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_SIN_RED") == "1";

    public bool HayInternet
    {
        get
        {
            if (SinRedForzado) return false;
            try
            {
                var perfil = NetworkInformation.GetInternetConnectionProfile();
                return perfil?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ConectividadRed", $"No se pudo consultar la conectividad de Windows: {ex.Message}");
                return NetworkInterface.GetIsNetworkAvailable();
            }
        }
    }

    public async Task<bool> EsperarInternetAsync(TimeSpan maximo, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        while (!HayInternet)
        {
            if (reloj.Elapsed >= maximo) return false;
            await Task.Delay(IntervaloSondeo, ct);
        }
        return true;
    }
}
