using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public class SyncService : ISyncService
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _trackingService;
    private readonly IAuthService _authService;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SyncService(
        IDatabaseService databaseService,
        IAnimeTrackingService trackingService,
        IAuthService authService)
    {
        _databaseService = databaseService;
        _trackingService = trackingService;
        _authService = authService;
    }

    public async Task<int> SincronizarPendientesAsync()
    {
        if (!_authService.EstaAutenticado())
        {
            AppLogger.Debug("[SyncService] Usuario no autenticado; omitiendo sincronización.", "SyncService");
            return 0;
        }

        string? token = _authService.ObtenerToken();
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }

        if (!await _syncLock.WaitAsync(100))
        {
            return 0;
        }

        try
        {
            var noSincronizados = await _databaseService.ObtenerEpisodiosNoSincronizadosAsync();
            if (noSincronizados.Count == 0)
            {
                return 0;
            }

            int totalExitosos = 0;
            var grupos = noSincronizados.GroupBy(e => e.AniListId);

            foreach (var grupo in grupos)
            {
                int aniListId = grupo.Key;
                int maxEpisodio = grupo.Max(e => e.NumeroEpisodio);
                var idsSincronizar = grupo.Select(e => e.Id).ToList();

                try
                {
                    bool ok = await _trackingService.ActualizarProgresoAsync(aniListId, maxEpisodio, token);
                    if (ok)
                    {
                        await _databaseService.MarcarEpisodiosSincronizadosAsync(idsSincronizar);
                        totalExitosos += idsSincronizar.Count;
                        AppLogger.Info($"[SyncService] Sincronizado AniListId={aniListId} hasta episodio {maxEpisodio}.", "SyncService");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[SyncService] Error al sincronizar anime {aniListId}: {ex.Message}", "SyncService");
                }
            }

            return totalExitosos;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SyncService] Excepción general durante sincronización: {ex.Message}", "SyncService", ex);
            return 0;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void IniciarSincronizacionPeriodica(TimeSpan intervalo)
    {
        DetenerSincronizacionPeriodica();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(intervalo, token);
                        if (token.IsCancellationRequested) break;

                        await SincronizarPendientesAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"[SyncService] Error en ciclo periódico: {ex.Message}", "SyncService");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[SyncService] Excepción fatal en ciclo periódico: {ex.Message}", "SyncService", ex);
            }
            finally
            {
                // Disponer solo si todavía es el token activo
                if (ReferenceEquals(_cts, cts))
                {
                    cts.Dispose();
                    _cts = null;
                }
                else
                {
                    cts.Dispose();
                }
            }
        });
    }

    public void DetenerSincronizacionPeriodica()
    {
        if (_cts != null)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            _cts = null;
        }
    }

    public void Dispose()
    {
        DetenerSincronizacionPeriodica();
        _syncLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
