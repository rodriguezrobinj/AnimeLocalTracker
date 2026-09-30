using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// "¿Hay internet?" según la señal única de la app (<see cref="GuardiaConexion"/>, alimentada por <see cref="EstadoConexionService"/>)
/// en vez del indicador de Windows: lo usan las descargas para esperar a que vuelva la red sin gastar reintentos.
/// </summary>
public sealed class ConectividadConfirmada : IConectividadRed
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromSeconds(1);
    private readonly GuardiaConexion _guardia;

    public ConectividadConfirmada(GuardiaConexion guardia) => _guardia = guardia;

    public bool HayInternet => !_guardia.PareceSinConexion;

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
