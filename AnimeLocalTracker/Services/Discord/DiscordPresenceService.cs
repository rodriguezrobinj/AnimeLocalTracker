using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Discord;

/// <summary>
/// Publica en Discord lo que se está viendo. El reproductor deja el estado deseado (<see cref="Actualizar"/>) y un único
/// trabajador en segundo plano decide si hay que mandarlo (<see cref="ReglaEnvioPresencia"/>), se conecta a Discord (cada 30 s
/// como mucho si no está abierto) y lo envía. Nada de esto toca el hilo de interfaz ni lanza hacia el reproductor.
/// </summary>
public sealed class DiscordPresenceService : IDiscordPresenceService, IDisposable
{
    /// <summary>ID de la aplicación "AnimeLocalTracker" en el Discord Developer Portal (público). Vacío = servicio inerte.</summary>
    internal const string ClientIdDiscord = "1561156845668798476";

    private const string Origen = "Discord";
    private static readonly TimeSpan EsperaReconexion = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _ajustes;
    private readonly IDatabaseService _db;
    private readonly string _clientId;
    private readonly TimeProvider _reloj;
    private readonly bool _conTrabajador;
    private readonly object _candado = new();
    private readonly SemaphoreSlim _aviso = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ClienteIpcDiscord _cliente = new();

    private EstadoPresencia? _deseado;
    private (int AnimeId, int Episodio)? _suprimido;
    private Task? _trabajador;

    // Solo los toca el trabajador.
    private EstadoPresencia? _enviado;
    private DateTimeOffset _enviadoEn;
    private DateTimeOffset _ultimoEnvio = DateTimeOffset.MinValue;
    private DateTimeOffset _proximoIntento = DateTimeOffset.MinValue;
    private bool _publicado;
    private int _portadaAnimeId = -1;
    private string? _portadaUrl;

    public DiscordPresenceService(ISettingsService ajustes, IDatabaseService db)
        : this(ajustes, db, ClientIdDiscord, TimeProvider.System, conTrabajador: true) { }

    /// <param name="conTrabajador">false en las pruebas: el estado se guarda pero nunca se abre la tubería de Discord.</param>
    internal DiscordPresenceService(ISettingsService ajustes, IDatabaseService db, string clientId, TimeProvider reloj, bool conTrabajador)
    {
        _ajustes = ajustes;
        _db = db;
        _clientId = clientId;
        _reloj = reloj;
        _conTrabajador = conTrabajador;
        _ajustes.ConfiguracionModificada += AlCambiarAjustes;
    }

    internal EstadoPresencia? Deseado { get { lock (_candado) return _deseado; } }

    internal bool TrabajadorIniciado { get { lock (_candado) return _trabajador != null; } }

    private bool Habilitado => _clientId.Length > 0 && _ajustes.ObtenerConfiguracion()?.DiscordRichPresence == true;

    public void Actualizar(EstadoPresencia estado)
    {
        if (!Habilitado) return;

        bool urgente;
        lock (_candado)
        {
            if (_suprimido == (estado.AnimeId, estado.Episodio)) return;
            _suprimido = null;
            // Pausa, reanudación u otro episodio despiertan al trabajador ya; un salto lo verá en su repaso de cada segundo.
            urgente = _deseado is null || _deseado.Reproduciendo != estado.Reproduciendo
                      || _deseado.AnimeId != estado.AnimeId || _deseado.Episodio != estado.Episodio;
            _deseado = estado;
        }
        if (urgente) Avisar();
    }

    public void Limpiar()
    {
        lock (_candado) _deseado = null;
        Avisar();
    }

    public void Suprimir()
    {
        lock (_candado)
        {
            if (_deseado is { } d) _suprimido = (d.AnimeId, d.Episodio);
            _deseado = null;
        }
        Avisar();
    }

    private void AlCambiarAjustes(AppSettings config)
    {
        if (config?.DiscordRichPresence != true) Limpiar();
    }

    private void Avisar()
    {
        if (!_conTrabajador || _clientId.Length == 0) return;
        lock (_candado)
        {
            // Sin trabajador y con Discord apagado no hay nada publicado que borrar: no se arranca un hilo para nada.
            if (_trabajador == null && !Habilitado) return;
            _trabajador ??= Task.Run(() => BucleAsync(_cts.Token));
        }
        try { if (_aviso.CurrentCount == 0) _aviso.Release(); }
        catch (SemaphoreFullException) { } // otro hilo ya lo había despertado
    }

    private async Task BucleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _aviso.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                await ProcesarAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(Origen, $"Error publicando el estado en Discord: {ex.Message}");
            }
        }
    }

    private async Task ProcesarAsync(CancellationToken ct)
    {
        EstadoPresencia? deseado;
        lock (_candado) deseado = _deseado;
        var ahora = _reloj.GetUtcNow();

        if (deseado is null)
        {
            // Si la conexión se perdió, Discord ya borró el estado al cerrarse la tubería.
            if (_publicado && _cliente.Conectado)
            {
                _ultimoEnvio = ahora;
                await _cliente.EnviarActividadAsync(ActividadDiscord.ComandoSetActivity(null, Environment.ProcessId), ct).ConfigureAwait(false);
            }
            _publicado = false;
            _enviado = null;
            if (!Habilitado) _cliente.Cerrar();
            return;
        }

        if (!ReglaEnvioPresencia.HayCambio(_enviado, _enviadoEn, deseado, ahora)) return;
        if (ahora - _ultimoEnvio < ReglaEnvioPresencia.IntervaloMinimo) return; // se vuelve a mirar en el próximo repaso

        if (!_cliente.Conectado)
        {
            if (ahora < _proximoIntento) return;
            _proximoIntento = ahora + EsperaReconexion;
            if (!await _cliente.ConectarAsync(_clientId, ct).ConfigureAwait(false)) return;
            ahora = _reloj.GetUtcNow();
        }

        string? portada = await PortadaAsync(deseado.AnimeId).ConfigureAwait(false);
        string comando = ActividadDiscord.ComandoSetActivity(ActividadDiscord.Construir(deseado, portada, ahora), Environment.ProcessId);
        _ultimoEnvio = ahora;
        if (await _cliente.EnviarActividadAsync(comando, ct).ConfigureAwait(false))
        {
            _enviado = deseado;
            _enviadoEn = ahora;
            _publicado = true;
        }
    }

    /// <summary>La ruta que recibe el reproductor puede ser la copia local de la caché; Discord necesita la URL de AniList.</summary>
    private async Task<string?> PortadaAsync(int animeId)
    {
        if (animeId <= 0) return null;
        if (animeId != _portadaAnimeId)
        {
            _portadaUrl = (await _db.ObtenerAnimePorIdAsync(animeId).ConfigureAwait(false))?.UrlPortada;
            _portadaAnimeId = animeId;
        }
        return _portadaUrl;
    }

    public void Dispose()
    {
        _ajustes.ConfiguracionModificada -= AlCambiarAjustes;
        _cts.Cancel();
        _cliente.Dispose();
    }
}
