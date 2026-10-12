using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public class PlaybackSeekCoordinator : IPlaybackSeekCoordinator
{
    // Tras un seek, el reproductor tarda unos ms en reportar la nueva posición; durante esa
    // ventana el bucle de tracking no repinta la posición para evitar rebotes.
    private static readonly TimeSpan VentanaSettleSeek = TimeSpan.FromMilliseconds(900);

    // Pedir saltos al motor más rápido de lo que él los procesa los ENCOLA y pueden
    // aplicarse FUERA DE ORDEN: el video "no se coloca y vuelve donde estaba". Con un intervalo
    // mínimo entre seeks y aplicando siempre el objetivo MÁS RECIENTE, el orden queda garantizado.
    private static readonly TimeSpan IntervaloMinimoSeek = TimeSpan.FromMilliseconds(250);

    private DateTime _settleHastaUtc = DateTime.MinValue;
    private double _seekPendiente = -1;
    private bool _seekPendientePreciso;
    private DateTime _ultimoSeekAplicadoUtc = DateTime.MinValue;
    private CancellationTokenSource? _seekDebounceCts;
    private double _seekPendienteAlAbrir = -1;
    private bool _disposed;

    public bool EnVentanaDeSettle => DateTime.UtcNow < _settleHastaUtc;

    public void IniciarVentanaDeSettle() => _settleHastaUtc = DateTime.UtcNow + VentanaSettleSeek;

    public void SolicitarSeek(IMotorVideo? motor, double segundos, Func<bool> haCompletadoOpen, bool preciso = false)
    {
        if (motor == null || motor.Desechado) return;

        var transcurrido = DateTime.UtcNow - _ultimoSeekAplicadoUtc;
        if (transcurrido >= IntervaloMinimoSeek)
        {
            AplicarSeekNativo(motor, segundos, haCompletadoOpen, preciso);
            return;
        }

        _seekPendiente = segundos;
        _seekPendientePreciso = preciso;

        Core.Cancelacion.Reemplazar(ref _seekDebounceCts);
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
                    AplicarSeekNativo(motor, objetivo, haCompletadoOpen, _seekPendientePreciso);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Debug("PlaybackSeekCoordinator", $"Error en seek coalescido: {ex.Message}");
            }
        }, ct);
    }

    private void AplicarSeekNativo(IMotorVideo motor, double segundos, Func<bool> haCompletadoOpen, bool preciso)
    {
        if (motor.Desechado) return;

        // Si el reproductor todavía se está abriendo o no ha completado la inicialización inicial del decodificador,
        // no saltar de inmediato (interrumpe el arranque del decodificador de video
        // dejando la pantalla negra). Guardamos la posición para aplicarla en cuanto OpenCompleted se active.
        if (!haCompletadoOpen() || motor.Estado is EstadoMotor.Abriendo or EstadoMotor.Detenido)
        {
            _seekPendienteAlAbrir = segundos;
            return;
        }

        _ultimoSeekAplicadoUtc = DateTime.UtcNow;
        try
        {
            motor.Saltar(TimeSpan.FromSeconds(segundos), preciso);
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
