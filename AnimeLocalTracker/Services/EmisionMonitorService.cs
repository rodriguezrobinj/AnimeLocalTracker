using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

public interface IEmisionMonitorService
{
    /// <summary>Empieza a vigilar (un ciclo por minuto) los animes con avisos o descarga automática activados.</summary>
    void Iniciar();
    void Detener();

    /// <summary>
    /// Último episodio YA EMITIDO de un anime según los datos locales (la referencia con la que se fija el punto de
    /// partida al activar las opciones). 0 si no se sabe.
    /// </summary>
    int UltimoEmitido(ProximaEmision? proxima, AnimeItem anime, DateTime ahoraUtc);
}

/// <summary>
/// Vigila los animes con "avisar" y/o "descargar automáticamente": cuando sale un episodio nuevo muestra un aviso y lo
/// descarga. Funciona mientras la app está abierta; si estuvo cerrada, al abrirla avisa (y descarga) los que salieron
/// mientras tanto. Solo cuentan los episodios posteriores al momento de activar la opción.
/// La descarga se reintenta con espera creciente porque el episodio suele tardar en aparecer en el servidor.
/// </summary>
public sealed class EmisionMonitorService : IEmisionMonitorService, IRecipient<DescargaProgresoMensaje>, IDisposable
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetrasoInicial = TimeSpan.FromSeconds(20);

    /// <summary>Espera antes de reintentar la descarga automática: 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 16 h (7 intentos).</summary>
    internal static readonly TimeSpan[] EsperasEntreIntentos =
    [
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
        TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(16)
    ];

    /// <summary>Tras la hora de emisión se espera un poco antes del primer intento (el servidor tarda en subirlo).</summary>
    internal static readonly TimeSpan MargenTrasEmision = TimeSpan.FromMinutes(10);

    /// <summary>Máximo de episodios atrasados que se descargan de golpe al reabrir la app tras una ausencia larga.</summary>
    internal const int MaxEpisodiosAtrasados = 5;

    private readonly IDatabaseService _database;
    private readonly IProximaEmisionService _proxima;
    private readonly IDownloadService _descargas;
    private readonly IFileScannerService _escaner;
    private readonly IDialogService _dialogos;
    private readonly SemaphoreSlim _ciclo = new(1, 1);
    private readonly ConcurrentDictionary<string, (int Intentos, DateTime ProximoIntentoUtc)> _intentos = new();
    private readonly ConcurrentDictionary<string, byte> _automaticasEnCurso = new();
    private Timer? _timer;

    public EmisionMonitorService(IDatabaseService database, IProximaEmisionService proxima, IDownloadService descargas, IFileScannerService escaner, IDialogService dialogos)
    {
        _database = database;
        _proxima = proxima;
        _descargas = descargas;
        _escaner = escaner;
        _dialogos = dialogos;
    }

    public void Iniciar()
    {
        if (_timer != null) return;
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        _timer = new Timer(_ => _ = EjecutarCicloAsync(), null, RetrasoInicial, Intervalo);
    }

    public void Detener()
    {
        _timer?.Dispose();
        _timer = null;
        WeakReferenceMessenger.Default.Unregister<DescargaProgresoMensaje>(this);
    }

    public void Dispose() => Detener();

    /// <summary>Aviso cuando termina una descarga automática (las manuales ya se ven en la pestaña Descargas).</summary>
    public void Receive(DescargaProgresoMensaje message)
    {
        if (!message.IsCompleted) return;
        if (!_automaticasEnCurso.TryRemove(Clave(message.AniListId, message.NumeroEpisodio), out _)) return;

        _dialogos.MostrarToast(
            LocalizationService.T("Emision_DescargadoTitulo"),
            string.Format(LocalizationService.T("Emision_DescargadoMsj"), message.AnimeTitulo, message.NumeroEpisodio),
            "CloudDownloadOutline", "#10B981");
    }

    public int UltimoEmitido(ProximaEmision? proxima, AnimeItem anime, DateTime ahoraUtc)
    {
        if (proxima != null)
        {
            // Hora ya pasada: ese episodio ya salió (la copia aún no conoce el siguiente); si no, sale el siguiente.
            return proxima.EmisionUtc <= ahoraUtc ? proxima.Episodio : Math.Max(0, proxima.Episodio - 1);
        }
        return anime.Estado == "FINISHED" ? Math.Max(0, anime.TotalEpisodios) : 0;
    }

    /// <summary>Un ciclo de vigilancia. Público para poder probarlo; el temporizador lo llama cada minuto.</summary>
    public async Task EjecutarCicloAsync()
    {
        if (!await _ciclo.WaitAsync(0)) return; // el ciclo anterior sigue en marcha
        try
        {
            var preferencias = await _database.ObtenerPreferenciasEmisionActivasAsync();
            foreach (var pref in preferencias)
            {
                try { await ProcesarAsync(pref); }
                catch (Exception ex) { AppLogger.Warn("EmisionMonitorService", $"Error vigilando el anime {pref.AniListId}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("EmisionMonitorService", $"Ciclo de vigilancia fallido: {ex.Message}");
        }
        finally
        {
            _ciclo.Release();
        }
    }

    private async Task ProcesarAsync(PreferenciaEmision pref)
    {
        var anime = await _database.ObtenerAnimePorIdAsync(pref.AniListId);
        if (anime == null) return;

        // El servicio decide si toca preguntar a AniList (revisión escalonada); casi siempre responde desde la copia local.
        var proxima = await _proxima.ObtenerAsync(pref.AniListId, anime.Estado);
        var ahora = DateTime.UtcNow;
        int ultimo = UltimoEmitido(proxima, anime, ahora);
        if (ultimo <= 0) return;

        bool cambio = false;

        if (pref.Avisar && ultimo > pref.UltimoAvisado)
        {
            AvisarEpisodios(anime, pref.UltimoAvisado + 1, ultimo);
            pref.UltimoAvisado = ultimo;
            cambio = true;
        }

        if (pref.AutoDescargar && ultimo > pref.UltimoDescargado)
        {
            cambio |= await DescargarPendientesAsync(pref, anime, proxima, ultimo, ahora);
        }

        if (cambio) await _database.GuardarPreferenciaEmisionAsync(pref);
    }

    private void AvisarEpisodios(AnimeItem anime, int desde, int hasta)
    {
        string mensaje = desde == hasta
            ? string.Format(LocalizationService.T("Emision_AvisoUno"), anime.Titulo, hasta)
            : string.Format(LocalizationService.T("Emision_AvisoVarios"), anime.Titulo, hasta - desde + 1, desde, hasta);
        _dialogos.MostrarToast(LocalizationService.T("Emision_AvisoTitulo"), mensaje, "BellRing", "#F59E0B");
    }

    /// <summary>
    /// Descarga en orden los episodios pendientes (uno por anime a la vez). Devuelve true si avanzó el contador guardado.
    /// </summary>
    private async Task<bool> DescargarPendientesAsync(PreferenciaEmision pref, AnimeItem anime, ProximaEmision? proxima, int ultimo, DateTime ahora)
    {
        if (string.IsNullOrWhiteSpace(anime.RutaCarpeta)) return false; // sin carpeta no hay dónde descargar

        bool avanzo = false;
        int tope = Math.Min(ultimo, pref.UltimoDescargado + MaxEpisodiosAtrasados);

        // Episodios que ya están en la carpeta (bajados a mano, o por un intento anterior): se dan por resueltos.
        HashSet<int> enDisco;
        try
        {
            var locales = await _escaner.EscanearEpisodiosAsync(anime.RutaCarpeta);
            enDisco = locales.Where(e => e.Descargado).Select(e => e.NumeroEpisodio).ToHashSet();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EmisionMonitorService", $"No se pudo escanear la carpeta de {anime.Titulo}: {ex.Message}");
            return false;
        }

        // Si el usuario avisó de un episodio ya emitido hace tiempo (app cerrada) no hay que esperar el margen.
        for (int ep = pref.UltimoDescargado + 1; ep <= tope; ep++)
        {
            string clave = Clave(pref.AniListId, ep);

            if (enDisco.Contains(ep))
            {
                pref.UltimoDescargado = ep;
                avanzo = true;
                _intentos.TryRemove(clave, out _);
                continue;
            }

            if (_descargas.EstaDescargando(pref.AniListId, ep, out _)) break; // en curso: se espera a que termine

            var estado = _intentos.GetOrAdd(clave, _ => (0, ProximoIntentoInicial(proxima, ep, ahora)));
            if (ahora < estado.ProximoIntentoUtc) break;

            if (estado.Intentos >= EsperasEntreIntentos.Length)
            {
                // Agotados los intentos: se avisa una vez y se sigue con el siguiente para no bloquear la cola.
                _dialogos.MostrarToast(
                    LocalizationService.T("Emision_FalloTitulo"),
                    string.Format(LocalizationService.T("Emision_FalloMsj"), anime.Titulo, ep),
                    "AlertCircleOutline", "#EF4444");
                pref.UltimoDescargado = ep;
                avanzo = true;
                _intentos.TryRemove(clave, out _);
                continue;
            }

            _intentos[clave] = (estado.Intentos + 1, ahora + EsperasEntreIntentos[estado.Intentos]);
            _automaticasEnCurso[clave] = 0;
            await _descargas.IniciarDescargaAutomaticaAsync(pref.AniListId, anime.Titulo, anime.RutaCarpeta, ep, TitulosAlternativos(anime));
            break; // un episodio a la vez por anime, en orden
        }

        return avanzo;
    }

    private static DateTime ProximoIntentoInicial(ProximaEmision? proxima, int episodio, DateTime ahora)
    {
        if (proxima != null && proxima.Episodio == episodio && proxima.EmisionUtc <= ahora)
        {
            var conMargen = proxima.EmisionUtc + MargenTrasEmision;
            return conMargen > ahora ? conMargen : ahora;
        }
        return ahora;
    }

    private static IEnumerable<string> TitulosAlternativos(AnimeItem anime) =>
        string.IsNullOrWhiteSpace(anime.NombresAlternativos)
            ? []
            : anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries);

    private static string Clave(int aniListId, int episodio) => $"{aniListId}_{episodio}";
}
