using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Mal;

/// <summary>Cómo fue una pasada: cuántos animes se revisaron en MAL, cuántos se escribieron y si llegó al final.</summary>
public sealed record ResumenSincronizacionMal(DateTime FinUtc, int Revisados, int Enviados, bool Completa);

/// <summary>Mantiene la lista de MyAnimeList al día con la biblioteca (app → MAL, sin bajar nunca nada).</summary>
public interface IMalSyncService
{
    /// <summary>Primera pasada a los 30 s del arranque y después cada 5 minutos.</summary>
    void Iniciar();

    /// <summary>Una pasada ya; null si MAL no está conectado o ya hay una en marcha.</summary>
    Task<ResumenSincronizacionMal?> SincronizarAhoraAsync(CancellationToken ct);

    ResumenSincronizacionMal? UltimaPasada { get; }

    event Action<ResumenSincronizacionMal>? PasadaTerminada;
}

/// <summary>
/// Espejo por diferencias: para cada anime con MalId calcula lo que la app querría en MAL (<see cref="MapeoMal"/>) y, si es
/// distinto de lo último enviado (<see cref="SincronizacionMal"/>), lee lo que MAL tiene, decide con <see cref="ReglaMal"/> y
/// escribe. Lo que no se pudo enviar (sin red, MAL caído) sigue distinto y sale en la pasada siguiente: no hace falta cola.
/// Una petición por segundo como mucho; ante un límite de MAL o un permiso perdido, la pasada se detiene.
/// </summary>
public sealed class MalSyncService : IMalSyncService, IDisposable
{
    private const string Origen = "Mal";
    private static readonly TimeSpan PrimeraPasada = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);

    private readonly IDatabaseService _db;
    private readonly IMalAuthService _auth;
    private readonly IMalApiClient _api;
    private readonly TimeSpan _esperaEntrePeticiones;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _unaPasada = new(1, 1);
    private Timer? _temporizador;

    public MalSyncService(IDatabaseService db, IMalAuthService auth, IMalApiClient api)
        : this(db, auth, api, TimeSpan.FromSeconds(1), TimeProvider.System) { }

    internal MalSyncService(IDatabaseService db, IMalAuthService auth, IMalApiClient api, TimeSpan esperaEntrePeticiones, TimeProvider reloj)
    {
        _db = db;
        _auth = auth;
        _api = api;
        _esperaEntrePeticiones = esperaEntrePeticiones;
        _reloj = reloj;
    }

    public ResumenSincronizacionMal? UltimaPasada { get; private set; }

    public event Action<ResumenSincronizacionMal>? PasadaTerminada;

    public void Iniciar()
        => _temporizador ??= new Timer(_ => _ = SincronizarAhoraAsync(CancellationToken.None), null, PrimeraPasada, Intervalo);

    public async Task<ResumenSincronizacionMal?> SincronizarAhoraAsync(CancellationToken ct)
    {
        if (!_auth.Configurado || !_auth.Conectado) return null;
        if (!await _unaPasada.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try
        {
            var resumen = await PasadaAsync(ct).ConfigureAwait(false);
            UltimaPasada = resumen;
            if (resumen.Revisados > 0) AppLogger.Info(Origen, $"Sincronización con MAL: {resumen.Revisados} revisados, {resumen.Enviados} actualizados{(resumen.Completa ? "" : " (detenida; sigue en la próxima)")}.");
            PasadaTerminada?.Invoke(resumen);
            return resumen;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn(Origen, $"La sincronización con MAL falló: {ex.Message}");
            return null;
        }
        finally
        {
            _unaPasada.Release();
        }
    }

    private async Task<ResumenSincronizacionMal> PasadaAsync(CancellationToken ct)
    {
        var animes = await _db.ObtenerTodosLosAnimesAsync().ConfigureAwait(false) ?? new();
        var vistos = await _db.ObtenerEpisodiosVistosPorAnimeAsync().ConfigureAwait(false) ?? new();
        var seguimientos = (await _db.ObtenerSeguimientosLocalesAsync().ConfigureAwait(false) ?? new()).ToDictionary(s => s.AniListId);
        var enviados = (await _db.ObtenerSincronizacionesMalAsync().ConfigureAwait(false) ?? new()).ToDictionary(s => s.AniListId);

        int revisados = 0, actualizados = 0;
        foreach (var anime in animes)
        {
            ct.ThrowIfCancellationRequested();
            var deseado = MapeoMal.Deseado(anime, seguimientos.GetValueOrDefault(anime.AniListId), vistos.GetValueOrDefault(anime.AniListId));
            if (deseado == null || YaEnviado(enviados.GetValueOrDefault(anime.AniListId), anime.MalId!.Value, deseado)) continue;

            revisados++;
            await Task.Delay(_esperaEntrePeticiones, ct).ConfigureAwait(false);
            var (lectura, enMal) = await _api.LeerAsync(anime.MalId.Value, ct).ConfigureAwait(false);
            if (lectura is ResultadoMal.LimiteAlcanzado or ResultadoMal.SinSesion) return Resumen(revisados, actualizados, completa: false);
            if (lectura == ResultadoMal.Error) continue; // se reintenta en la próxima pasada

            if (ReglaMal.QueEnviar(deseado, enMal) is { } envio)
            {
                await Task.Delay(_esperaEntrePeticiones, ct).ConfigureAwait(false);
                var escritura = await _api.ActualizarAsync(anime.MalId.Value, envio, ct).ConfigureAwait(false);
                if (escritura is ResultadoMal.LimiteAlcanzado or ResultadoMal.SinSesion) return Resumen(revisados, actualizados, completa: false);
                if (escritura == ResultadoMal.Error) continue;
                actualizados++;
            }

            // Se apunta lo DESEADO (también si MAL iba por delante y no se tocó): mientras no cambie en la app, no se vuelve a preguntar.
            await _db.GuardarSincronizacionMalAsync(new SincronizacionMal
            {
                AniListId = anime.AniListId, MalId = anime.MalId.Value, Estado = deseado.Estado, Progreso = deseado.Progreso,
                Nota = deseado.Nota, EnviadoUtc = _reloj.GetUtcNow().UtcDateTime,
            }).ConfigureAwait(false);
        }
        return Resumen(revisados, actualizados, completa: true);
    }

    private static bool YaEnviado(SincronizacionMal? enviado, int malId, EstadoMal deseado)
        => enviado != null && enviado.MalId == malId && enviado.Estado == deseado.Estado
           && enviado.Progreso == deseado.Progreso && enviado.Nota == deseado.Nota;

    private ResumenSincronizacionMal Resumen(int revisados, int enviados, bool completa)
        => new(_reloj.GetUtcNow().UtcDateTime, revisados, enviados, completa);

    public void Dispose()
    {
        _temporizador?.Dispose();
        _unaPasada.Dispose();
    }
}
