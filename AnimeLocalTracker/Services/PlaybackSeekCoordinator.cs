using System;
using System.Threading;
using System.Threading.Tasks;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

public class PlaybackSeekCoordinator : IPlaybackSeekCoordinator
{
    // Tras un seek, el reproductor tarda unos ms en reportar la nueva posición; durante esa
    // ventana el bucle de tracking no repinta la posición para evitar rebotes.
    private static readonly TimeSpan VentanaSettleSeek = TimeSpan.FromMilliseconds(900);

    // Enviar CurTime a Flyleaf más rápido de lo que él procesa los seeks los ENCOLA y pueden
    // aplicarse FUERA DE ORDEN: el video "no se coloca y vuelve donde estaba". Con un intervalo
    // mínimo entre seeks y aplicando siempre el objetivo MÁS RECIENTE, el orden queda garantizado.
    private static readonly TimeSpan IntervaloMinimoSeek = TimeSpan.FromMilliseconds(250);

    private DateTime _settleHastaUtc = DateTime.MinValue;
    private double _seekPendiente = -1;
    private DateTime _ultimoSeekAplicadoUtc = DateTime.MinValue;
    private CancellationTokenSource? _seekDebounceCts;
    private double _seekPendienteAlAbrir = -1;
    private bool _disposed;

    public bool EnVentanaDeSettle => DateTime.UtcNow < _settleHastaUtc;

    public void IniciarVentanaDeSettle() => _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;

    public void SolicitarSeek(Player? player, double segundos, Func<bool> haCompletadoOpen)
    {
        if (player == null || player.IsDisposed) return;

        var transcurrido = DateTime.UtcNow - _ultimoSeekAplicadoUtc;
        if (transcurrido >= IntervaloMinimoSeek)
        {
            AplicarSeekNativo(player, segundos, haCompletadoOpen);
            return;
        }

        _seekPendiente = segundos;

        _seekDebounceCts?.Cancel();
        _seekDebounceCts?.Dispose();
        _seekDebounceCts = new CancellationTokenSource();
        var ct = _seekDebounceCts.Token;
        var restante = IntervaloMinimoSeek - transcurrido;
        if (restante < TimeSpan.Zero) restante = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(restante, ct);
                double objetivo = Interlocked.Exchange(ref _seekPendiente, -1);
                if (objetivo >= 0 && !ct.IsCancellationRequested)
                {
                    AplicarSeekNativo(player, objetivo, haCompletadoOpen);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Debug("PlaybackSeekCoordinator", $"Error en seek coalescido: {ex.Message}");
            }
        }, ct);
    }

    private void AplicarSeekNativo(Player player, double segundos, Func<bool> haCompletadoOpen)
    {
        if (player.IsDisposed) return;

        // Si el reproductor todavía se está abriendo o no ha completado la inicialización inicial del decodificador,
        // no invocar Player.CurTime de inmediato (interrumpe la creación del contexto de video en FFmpeg/Flyleaf
        // dejando la pantalla negra). Guardamos la posición para aplicarla en cuanto OpenCompleted se active.
        if (!haCompletadoOpen() || player.Status == Status.Opening || player.Status == Status.Stopped)
        {
            _seekPendienteAlAbrir = segundos;
            return;
        }

        _ultimoSeekAplicadoUtc = DateTime.UtcNow;
        try
        {
            player.CurTime = TimeSpan.FromSeconds(segundos).Ticks;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackSeekCoordinator", $"Excepción al ajustar posición nativa del reproductor: {ex.Message}");
        }
    }

    public double? ConsumirSeekPendienteAlAbrir()
    {
        if (_seekPendienteAlAbrir < 0) return null;
        double valor = _seekPendienteAlAbrir;
        _seekPendienteAlAbrir = -1;
        return valor;
    }

    public void Reiniciar()
    {
        CancelarPendiente();
        _seekPendienteAlAbrir = -1;
    }

    public void CancelarPendiente()
    {
        Interlocked.Exchange(ref _seekPendiente, -1);
        try { _seekDebounceCts?.Cancel(); } catch { }
        _seekDebounceCts?.Dispose();
        _seekDebounceCts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        CancelarPendiente();
    }
}
