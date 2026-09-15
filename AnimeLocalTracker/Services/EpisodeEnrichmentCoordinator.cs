using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Native;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Extraído de DetalleViewModel: enriquecimiento en segundo plano de metadata técnica
/// (ffprobe vía daemon Python) y miniaturas (Rust FFI, con fallback a Python) de los episodios
/// locales de un anime, con persistencia por lotes en SQLite. Instancia por ViewModel (no
/// registrado en DI): mantiene el gate/lote de la sesión de detalle actual, sin estado
/// compartido entre distintos animes/vistas.
/// </summary>
public sealed class EpisodeEnrichmentCoordinator : IDisposable
{
    // Coalescing: si ya hay una pasada en curso, no abrir otra en paralelo.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // PERF-05: la persistencia del enriquecimiento se acumula y se escribe por lotes
    // (antes 1 SELECT + 1 write por episodio; ahora 1 transacción por lote de 20).
    private readonly object _persistenciaLock = new();
    private readonly List<RegistroEpisodio> _persistenciaPendiente = new();
    private const int PersistenciaLoteMax = 20;

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Enriquecer los episodios locales que aún no tienen metadata técnica o miniatura vía
    /// el bridge Python y guardar el resultado en SQLite para visitas instantáneas futuras.
    /// TODO el trabajo pesado (ffprobe, ffmpeg, SQLite) corre en el thread pool; al llamador
    /// solo se le pide repintar vía <paramref name="solicitarRefrescoEpisodios"/> (que ya
    /// gestiona su propio salto al Dispatcher de UI y es seguro de invocar desde cualquier hilo).
    /// </summary>
    public async Task EnriquecerEnSegundoPlanoAsync(
        int aniListId,
        IReadOnlyList<EpisodioItem> todosLosEpisodios,
        PythonEpisodeEnricher? enricher,
        IDatabaseService databaseService,
        Action solicitarRefrescoEpisodios)
    {
        if (!_gate.Wait(0)) return;

        try
        {
            await Task.Run(async () =>
            {
                var pendientes = todosLosEpisodios
                    .Where(e => e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta) && File.Exists(e.RutaCompleta) &&
                                (string.IsNullOrEmpty(e.RutaMiniatura) || string.IsNullOrEmpty(e.Resolucion)))
                    .ToList();

                if (pendientes.Count == 0) return;

                // Importante: NO esperar aquí el ping al daemon Python (si está frío puede
                // tardar 2-8 s). La extracción Rust de miniaturas no depende de Python:
                // se resuelve pythonDisponible de forma diferida, solo si se necesita.
                bool pythonDisponible = false;

                // ── FASE 1: Miniaturas 1×1 con Rust FFI. Cada episodio se extrae, persiste y
                //    se refleja en la UI ANTES de pasar al siguiente: la primera miniatura
                //    aparece en ~1s y el resto van apareciendo de forma secuencial apenas
                //    cada una termina (sin esperar a que se generen todas). ──
                var sinMiniatura = pendientes.Where(e => string.IsNullOrEmpty(e.RutaMiniatura)).ToList();
                if (sinMiniatura.Count > 0)
                {
                    bool huboCambiosMiniatura = false;

                    if (NativeMethods.IsAvailable)
                    {
                        foreach (var ep in sinMiniatura)
                        {
                            string outPath = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(ep.RutaCompleta);
                            // Timestamp corto (2s): el keyframe previo cae en los primeros
                            // segundos del video, así el decode intermedio (frames entre el
                            // keyframe y el punto exacto) es mínimo → miniatura casi instantánea.
                            bool ok = NativeMethods.ExtractFrame(ep.RutaCompleta, outPath, 2.0, 320);

                            if (ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                            {
                                ep.RutaMiniatura = outPath;
                                huboCambiosMiniatura = true;

                                // Mostrar esta miniatura YA y persistirla antes de seguir
                                await PersistirRegistrosAsync(databaseService, aniListId, new[] { ep }).ConfigureAwait(false);
                                solicitarRefrescoEpisodios();
                            }
                        }
                    }

                    // Fallback individual SOLO si Rust no estuvo disponible (resolver el ping
                    // de Python aquí, de forma diferida, sin bloquear la extracción Rust)
                    if (!huboCambiosMiniatura)
                    {
                        pythonDisponible = enricher != null && await enricher.EstáDisponibleAsync().ConfigureAwait(false);
                        if (pythonDisponible)
                        {
                            foreach (var ep in sinMiniatura)
                            {
                                await enricher!.GenerarMiniaturaAsync(ep).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(ep.RutaMiniatura)) huboCambiosMiniatura = true;
                            }

                            if (huboCambiosMiniatura)
                            {
                                await PersistirRegistrosAsync(databaseService, aniListId, sinMiniatura).ConfigureAwait(false);
                                solicitarRefrescoEpisodios();
                            }
                        }
                    }
                }

                // ── FASE 2: Metadata técnica (ffprobe vía daemon Python) en segundo plano diferido ──
                var sinMetadata = pendientes.Where(e => string.IsNullOrEmpty(e.Resolucion)).ToList();
                if (sinMetadata.Count > 0)
                {
                    if (!pythonDisponible)
                        pythonDisponible = enricher != null && await enricher.EstáDisponibleAsync().ConfigureAwait(false);
                    if (pythonDisponible)
                    {
                        foreach (var ep in sinMetadata)
                        {
                            await enricher!.EnriquecerEpisodioAsync(ep).ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(ep.Resolucion))
                            {
                                await PersistirRegistrosAsync(databaseService, aniListId, new[] { ep }).ConfigureAwait(false);
                                solicitarRefrescoEpisodios();
                            }

                            // Pausa de cortesía para no saturar CPU en segundo plano
                            await Task.Delay(20).ConfigureAwait(false);
                        }
                    }
                }

                // PERF-05: vaciar el lote de persistencia acumulado del enriquecimiento
                await VaciarPersistenciaPendienteAsync(databaseService).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EpisodeEnrichmentCoordinator", $"Error en enriquecimiento de fondo: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persiste los episodios con miniatura/metadata recién generada en SQLite para
    /// que las siguientes visitas al anime sean instantáneas (sin regenerar).
    /// </summary>
    private async Task PersistirRegistrosAsync(IDatabaseService databaseService, int aniListId, IEnumerable<EpisodioItem> episodios)
    {
        List<RegistroEpisodio>? loteAEnviar = null;

        lock (_persistenciaLock)
        {
            foreach (var ep in episodios)
            {
                _persistenciaPendiente.Add(new RegistroEpisodio
                {
                    AniListId = aniListId,
                    NumeroEpisodio = ep.NumeroEpisodio,
                    RutaArchivo = ep.RutaCompleta,
                    Resolucion = ep.Resolucion,
                    CodecVideo = ep.CodecVideo,
                    Fps = ep.Fps,
                    Es10Bit = ep.Es10Bit,
                    RutaMiniatura = ep.RutaMiniatura,
                    VistoLocal = ep.Visto,
                    FavoritoLocal = ep.Favorito,
                    ProgresoSegundos = ep.ProgresoSegundos,
                    TotalSegundos = ep.TotalSegundos
                });
            }

            if (_persistenciaPendiente.Count >= PersistenciaLoteMax)
            {
                loteAEnviar = new List<RegistroEpisodio>(_persistenciaPendiente);
                _persistenciaPendiente.Clear();
            }
        }

        if (loteAEnviar != null)
        {
            try
            {
                await databaseService.GuardarRegistrosEpisodioBulkAsync(loteAEnviar).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("EpisodeEnrichmentCoordinator", $"Error persistiendo lote de enriquecimiento: {ex.Message}");
            }
        }
    }

    private async Task VaciarPersistenciaPendienteAsync(IDatabaseService databaseService)
    {
        List<RegistroEpisodio>? pendiente = null;
        lock (_persistenciaLock)
        {
            if (_persistenciaPendiente.Count == 0) return;
            pendiente = new List<RegistroEpisodio>(_persistenciaPendiente);
            _persistenciaPendiente.Clear();
        }

        try
        {
            await databaseService.GuardarRegistrosEpisodioBulkAsync(pendiente).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EpisodeEnrichmentCoordinator", $"Error vaciando persistencia de enriquecimiento: {ex.Message}");
        }
    }
}
