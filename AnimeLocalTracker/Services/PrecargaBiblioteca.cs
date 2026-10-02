using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>Lo que la Galería necesita para pintarse: la biblioteca y cuántos episodios vistos tiene cada anime.</summary>
public sealed record DatosBiblioteca(List<AnimeItem> Animes, Dictionary<int, int> VistosPorAnime);

/// <summary>
/// Adelanta, mientras se construye la ventana principal, el trabajo que la Galería hacía DESPUÉS de mostrarse: leer la
/// biblioteca y decodificar las portadas de la primera pantalla. Así las primeras tarjetas salen ya con su portada y la
/// decodificación no le quita el procesador a la interfaz justo cuando está dibujando las tarjetas (medido en un equipo de
/// 2 núcleos: dibujarlas costaba 1,1 s con las portadas decodificándose a la vez y 0,55 s sin ellas).
/// </summary>
public sealed class PrecargaBiblioteca
{
    /// <summary>Portadas que se decodifican por adelantado: las de la primera pantalla y la fila siguiente.</summary>
    internal const int PortadasPrimeraPantalla = 24;

    private readonly IDatabaseService _baseDatos;
    private readonly IImageCacheService _imagenes;
    private Task<DatosBiblioteca>? _tarea;

    public PrecargaBiblioteca(IDatabaseService baseDatos, IImageCacheService imagenes)
    {
        _baseDatos = baseDatos;
        _imagenes = imagenes;
    }

    /// <summary>Empieza a leer en segundo plano. La base de datos ya debe estar inicializada.</summary>
    public void Iniciar() => _tarea ??= Task.Run(CargarAsync);

    /// <summary>
    /// Entrega la lectura adelantada UNA sola vez (la primera carga de la Galería); después devuelve null y quien pregunte
    /// lee de la base de datos como siempre. Null también si nunca se inició (pruebas).
    /// </summary>
    public Task<DatosBiblioteca>? Consumir() => Interlocked.Exchange(ref _tarea, null);

    private async Task<DatosBiblioteca> CargarAsync()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var animes = await _baseDatos.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();
        long msAnimes = reloj.ElapsedMilliseconds;
        var vistos = await _baseDatos.ObtenerEpisodiosVistosPorAnimeAsync() ?? new Dictionary<int, int>();
        long msVistos = reloj.ElapsedMilliseconds - msAnimes;

        try
        {
            // La Galería abre siempre ordenada por título: esas son las tarjetas que se van a ver primero.
            var primeras = animes
                .Where(a => !string.IsNullOrWhiteSpace(a.UrlPortada))
                .OrderBy(a => a.Titulo, StringComparer.CurrentCulture)
                .Take(PortadasPrimeraPantalla);
            // Dos a la vez: el otro núcleo lo está usando la interfaz para construir la ventana.
            await Parallel.ForEachAsync(primeras, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (anime, _) =>
            {
                if (await _imagenes.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada) != null) anime.ResolverPortadaLocal();
            });
        }
        catch (Exception ex)
        {
            // Sin portadas adelantadas la Galería las carga después, como siempre.
            AppLogger.Debug("PrecargaBiblioteca", $"No se pudieron adelantar las portadas: {ex.Message}");
        }

        // Lo que cuesta de verdad la lectura (fuera del hilo de la interfaz): el tiempo que anota la Galería incluye además la
        // espera a que la ventana termine de construirse.
        AppLogger.Info("PrecargaBiblioteca", $"[Perf] Lectura adelantada: {animes.Count} animes en {msAnimes} ms, episodios vistos por anime en {msVistos} ms, " +
            $"portadas de la primera pantalla en {reloj.ElapsedMilliseconds - msAnimes - msVistos} ms.");
        return new DatosBiblioteca(animes, vistos);
    }
}
