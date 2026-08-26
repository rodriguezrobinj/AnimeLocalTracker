using AnimeLocalTracker.Core.Services;
using System;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core.Services;

public interface ISyncService : IDisposable
{
    Task<int> SincronizarPendientesAsync();
    void IniciarSincronizacionPeriodica(TimeSpan intervalo);
    void DetenerSincronizacionPeriodica();
}
