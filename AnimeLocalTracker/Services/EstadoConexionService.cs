using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <summary>Estado para el distintivo de la barra superior: sin conexión y/o cambios esperando a subirse a AniList.</summary>
public sealed record EstadoConexionMensaje(bool SinConexion, int CambiosPendientes);

/// <summary>Volvió la conexión: las pantallas que muestran una copia guardada pueden refrescarse.</summary>
public sealed record ConexionRecuperadaMensaje;

public interface IEstadoConexionService
{
    bool SinConexion { get; }
    int CambiosPendientes { get; }
    void Iniciar();
}

/// <summary>
/// La señal única de "sin conexión" de toda la app. En cuanto se confirma, <see cref="GuardiaConexion"/> corta todas las
/// peticiones al instante y cada pantalla muestra lo guardado sin esperar a nada; al volver la red sincroniza lo pendiente y avisa
/// a Calendario/Actualizaciones.
/// Tiene que ser inmediata (pedido del usuario): se evalúa en el momento en que una petición falla sin respuesta o Windows avisa de
/// un cambio de red, y además cada <see cref="Intervalo"/>. No se fía del indicador de Windows (en el equipo del usuario parpadea y,
/// con la red caída, siguió diciendo "hay internet"): comprueba con <see cref="SondaInternet"/>. Ante una sospecha (o cada
/// <see cref="IntervaloSondeoEnLinea"/> aunque no la haya, por si cae el router sin que la app esté pidiendo nada) hace una
/// comprobación y, si falla, la confirma con otra a los <see cref="EsperaConfirmacion"/> dentro de la misma evaluación. Sin
/// conexión, comprueba en cada evaluación para notar la vuelta; una sola comprobación buena basta para volver.
/// </summary>
public sealed class EstadoConexionService : IEstadoConexionService, IDisposable
{
    internal static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan IntervaloSondeoEnLinea = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan EsperaConfirmacion = TimeSpan.FromMilliseconds(700);
    /// <summary>Cada cuántas evaluaciones se vuelve a contar lo pendiente (unos 30 s).</summary>
    private const int RecuentoCada = 10;

    private readonly GuardiaConexion _guardia;
    private readonly IDatabaseService _database;
    private readonly IAuthService _auth;
    private readonly ISyncService _sync;
    private readonly Func<CancellationToken, Task<bool>> _sonda;
    private readonly TimeSpan _esperaConfirmacion;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _ultimaEvaluacionUtc = DateTime.MinValue;
    private DateTime _ultimoSondeoUtc = DateTime.MinValue;
    private Timer? _timer;
    private bool _primera = true;
    private int _evaluaciones;

    public bool SinConexion { get; private set; }
    public int CambiosPendientes { get; private set; }

    /// <param name="sonda">Solo para pruebas: la comprobación de internet (por defecto <see cref="SondaInternet.HayInternetAsync"/>).</param>
    /// <param name="esperaConfirmacion">Solo para pruebas: pausa entre la comprobación fallida y la que la confirma.</param>
    public EstadoConexionService(GuardiaConexion guardia, IDatabaseService database, IAuthService auth, ISyncService sync,
        Func<CancellationToken, Task<bool>>? sonda = null, TimeSpan? esperaConfirmacion = null)
    {
        _sonda = sonda ?? SondaInternet.HayInternetAsync;
        _esperaConfirmacion = esperaConfirmacion ?? EsperaConfirmacion;
        _guardia = guardia;
        _database = database;
        _auth = auth;
        _sync = sync;
    }

    public void Iniciar()
    {
        if (_timer != null) return;
        _guardia.FalloDeRed += AlFallarUnaPeticion;
        _timer = new Timer(_ => _ = EvaluarAsync(), null, TimeSpan.Zero, Intervalo);
        try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += AlCambiarLaRed; }
        catch (Exception ex) { AppLogger.Debug("EstadoConexionService", $"Sin aviso de cambios de red de Windows: {ex.Message}"); }
    }

    private void AlCambiarLaRed(object? sender) => _ = EvaluarAsync();

    /// <summary>Una petición acaba de fallar sin respuesta: se comprueba ya, no en el siguiente ciclo.</summary>
    private void AlFallarUnaPeticion()
    {
        if (!SinConexion) _ = EvaluarAsync();
    }

    internal async Task EvaluarAsync()
    {
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            bool primera = _primera;
            bool antes = SinConexion;
            int pendientesAntes = CambiosPendientes;
            var inicio = DateTime.UtcNow;

            bool sinConexion;
            if (_guardia.Forzado) sinConexion = true;
            else if (SinConexion) sinConexion = !await ComprobarAsync(inicio); // para notar la vuelta cuanto antes
            else if (primera || HaySospecha() || inicio - _ultimoSondeoUtc >= IntervaloSondeoEnLinea)
            {
                bool hayRed = await ComprobarAsync(inicio);
                if (!hayRed)
                {
                    // Un fallo suelto no basta; la confirmación va en esta misma evaluación para que el cambio sea inmediato.
                    await Task.Delay(_esperaConfirmacion);
                    hayRed = await ComprobarAsync(DateTime.UtcNow);
                }
                sinConexion = !hayRed;
            }
            else sinConexion = false;

            _ultimaEvaluacionUtc = inicio;
            SinConexion = sinConexion;
            _guardia.EstablecerSinConexionConfirmada(SinConexion && !_guardia.Forzado);

            bool recuperada = antes && !SinConexion;
            if (primera || recuperada || antes != SinConexion || ++_evaluaciones % RecuentoCada == 0)
                await ContarPendientesAsync();
            _primera = false;

            if (antes != SinConexion)
                AppLogger.Info("EstadoConexionService", SinConexion ? "Sin conexión: la app sigue con lo guardado." : "Volvió la conexión.");

            // El distintivo se avisa antes de sincronizar: el cambio de estado se ve al instante.
            if (primera || antes != SinConexion || pendientesAntes != CambiosPendientes)
                WeakReferenceMessenger.Default.Send(new EstadoConexionMensaje(SinConexion, CambiosPendientes));

            if (recuperada)
            {
                if (CambiosPendientes > 0)
                {
                    var (enviados, _) = await _sync.SincronizarPendientesAsync();
                    AppLogger.Info("EstadoConexionService", $"Sincronizado al volver la conexión: {enviados} cambio(s).");
                    int antesDeSincronizar = CambiosPendientes;
                    await ContarPendientesAsync();
                    if (CambiosPendientes != antesDeSincronizar)
                        WeakReferenceMessenger.Default.Send(new EstadoConexionMensaje(SinConexion, CambiosPendientes));
                }
                WeakReferenceMessenger.Default.Send(new ConexionRecuperadaMensaje());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EstadoConexionService", $"No se pudo evaluar la conexión: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> ComprobarAsync(DateTime ahoraUtc)
    {
        _ultimoSondeoUtc = ahoraUtc;
        bool hayRed;
        try { hayRed = await _sonda(CancellationToken.None); }
        catch (Exception) { hayRed = false; }
        if (hayRed) _guardia.RegistrarRespuesta(); // un servidor respondió: con Windows diciendo lo contrario, no se repite cada 3 s
        return hayRed;
    }

    /// <summary>Algo indica que la red pudo caerse: una petición falló sin respuesta desde la última evaluación, o Windows dice que no hay internet.</summary>
    private bool HaySospecha() => _guardia.UltimoFalloDeRedUtc >= _ultimaEvaluacionUtc || _guardia.PareceSinConexionSegunWindows;

    /// <summary>Episodios vistos y cambios del editor que aún no llegaron a AniList (sin cuenta no hay nada que enviar).</summary>
    private async Task ContarPendientesAsync()
    {
        if (!_auth.EstaAutenticado())
        {
            CambiosPendientes = 0;
            return;
        }
        var episodios = await _database.ObtenerEpisodiosNoSincronizadosAsync();
        var seguimientos = await _database.ObtenerSeguimientosPendientesAsync();
        CambiosPendientes = (episodios?.Count ?? 0) + (seguimientos?.Count ?? 0);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _guardia.FalloDeRed -= AlFallarUnaPeticion;
        try { Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= AlCambiarLaRed; } catch { }
        _lock.Dispose();
    }
}
