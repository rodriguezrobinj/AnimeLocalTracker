using System;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public interface ISyncService : IDisposable
{
    Task<(int Exitosos, int Pendientes)> SincronizarPendientesAsync();
    void IniciarSincronizacionPeriodica(TimeSpan intervalo);
    void DetenerSincronizacionPeriodica();
}
