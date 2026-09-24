using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDemuxer;

namespace AnimeLocalTracker.Services;

public class EpisodeNavigator : IEpisodeNavigator
{
    // CA1001: dueño de un CancellationTokenSource descartable.
    private bool _disposed;


    private List<EpisodioItem> _episodios = new();
    private string? _rutaPrecargada;

    public IReadOnlyList<EpisodioItem> EpisodiosDisponibles => _episodios;

    public void EstablecerEpisodios(IEnumerable<EpisodioItem>? episodios)
    {
        if (episodios == null) return;

        _episodios = episodios
            .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderBy(e => e.NumeroEpisodio)
            .ToList();
    }

    public EpisodioItem? ObtenerSiguiente(int episodioActual)
    {
        return _episodios
            .Where(e => e.NumeroEpisodio > episodioActual && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderBy(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    public EpisodioItem? ObtenerAnterior(int episodioActual)
    {
        return _episodios
            .Where(e => e.NumeroEpisodio < episodioActual && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .OrderByDescending(e => e.NumeroEpisodio)
            .FirstOrDefault();
    }

    /// <summary>
    /// ARQ-01: decisión pura (sin Player) de si toca disparar la pre-carga del siguiente episodio en
    /// este instante del sondeo de progreso — al menos 95% visto, hay un siguiente episodio con
    /// archivo, y no es el mismo que ya se precargó (como máximo una vez por episodio).
    /// </summary>
    public static bool DebePrecargarSiguienteEpisodio(double porcentaje, string? rutaSiguiente, string? rutaYaPrecargada) =>
        porcentaje >= 0.95 && !string.IsNullOrWhiteSpace(rutaSiguiente) && rutaSiguiente != rutaYaPrecargada;

    // Vida propia (no ligado al ct del bucle de tracking): se reinicia una vez por episodio
    // (CancelarPrecargaYReiniciar, llamado desde CargarVideoAsync) y se cancela en Dispose —
    // igual ciclo de vida que tenía _precargaCts en ReproductorViewModel antes de esta extracción.
    private CancellationTokenSource _precargaCts = new();

    public void ConsiderarPrecarga(double porcentaje, string? rutaSiguiente)
    {
        if (!DebePrecargarSiguienteEpisodio(porcentaje, rutaSiguiente, _rutaPrecargada)) return;

        _rutaPrecargada = rutaSiguiente;
        _ = PrecargarSiguienteAsync(rutaSiguiente!, _precargaCts.Token);
    }

    public void CancelarPrecargaYReiniciar()
    {
        _precargaCts.Cancel();
        _precargaCts.Dispose();
        _precargaCts = new CancellationTokenSource();
        _rutaPrecargada = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        _precargaCts.Cancel();
        _precargaCts.Dispose();
    }

    /// <summary>
    /// Pre-buffering del siguiente episodio: abre un <see cref="Demuxer"/> de FlyleafLib APARTE del
    /// Player en reproducción contra el archivo del siguiente episodio, y lo descarta enseguida. No
    /// sustituye la apertura real del Player al cambiar de episodio — la API pública de FlyleafLib no
    /// permite entregarle a un Player un demuxer ya abierto — pero adelanta el sondeo de
    /// contenedor/streams (avformat_find_stream_info) y calienta la caché de E/S de Windows para ese
    /// archivo, así que cuando SiguienteEpisodio() abra el Player real, esa parte del trabajo ya no
    /// parte de cero. Estrictamente best-effort: nunca toca el Player en reproducción, y cualquier
    /// fallo (archivo movido, formato no soportado) se descarta sin avisar al usuario.
    /// </summary>
    private static async Task PrecargarSiguienteAsync(string rutaSiguiente, CancellationToken ct)
    {
        try
        {
            await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;

                var config = new Config();
                var demuxer = new Demuxer(config.Demuxer, MediaType.Video);
                try
                {
                    string error = demuxer.Open(rutaSiguiente);
                    if (!string.IsNullOrEmpty(error))
                    {
                        AppLogger.Debug("EpisodeNavigator", $"Precarga del siguiente episodio no pudo abrir '{rutaSiguiente}': {error}");
                    }
                }
                finally
                {
                    demuxer.Dispose();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Se cambió de episodio/se cerró el reproductor antes de que terminara: no hay nada que hacer.
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EpisodeNavigator", $"Precarga del siguiente episodio falló (no afecta la reproducción actual): {ex.Message}");
        }
    }
}
