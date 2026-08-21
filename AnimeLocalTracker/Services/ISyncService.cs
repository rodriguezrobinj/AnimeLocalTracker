using System;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public interface ISyncService : IDisposable
{
    Task<int> SincronizarPendientesAsync();
    void IniciarSincronizacionPeriodica(TimeSpan intervalo);
    void DetenerSincronizacionPeriodica();
}
