using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

public interface IFranquiciaService
{
    /// <summary>
    /// Id de franquicia de cada anime de la biblioteca, a partir de las relaciones YA guardadas (sin red).
    /// Un anime sin relaciones conocidas forma su propia franquicia.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> ObtenerMapaAsync(IEnumerable<int> idsBiblioteca);

    /// <summary>
    /// Consulta a AniList las relaciones que falten (o hayan caducado) y las guarda. Devuelve true si se
    /// obtuvieron datos nuevos. Nunca lanza: sin conexión simplemente no hay cambios.
    /// </summary>
    Task<bool> SincronizarAsync(IEnumerable<int> idsBiblioteca);
}

public sealed class FranquiciaService : IFranquiciaService, IDisposable
{
    /// <summary>Rondas de exploración: biblioteca → sus relacionados → los relacionados de estos…</summary>
    private const int MaximoSaltos = 4;

    /// <summary>Tope de animes ajenos a la biblioteca que se exploran (evita recorrer franquicias enormes).</summary>
    private const int MaximoNodosExternos = 400;

    /// <summary>Las relaciones de un anime se refrescan cada cierto tiempo (salen secuelas y películas nuevas).</summary>
    private static readonly TimeSpan Caducidad = TimeSpan.FromDays(30);

    /// <summary>Tras un intento fallido (sin red) no se insiste de inmediato.</summary>
    private static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromMinutes(5);

    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly SemaphoreSlim _candado = new(1, 1);
    private DateTime _noReintentarAntesDeUtc = DateTime.MinValue;

    public FranquiciaService(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
    }

    public async Task<IReadOnlyDictionary<int, int>> ObtenerMapaAsync(IEnumerable<int> idsBiblioteca)
    {
        var aristas = await _databaseService.ObtenerRelacionesAnimeAsync() ?? new List<RelacionAnime>();
        return AgrupadorFranquicias.Agrupar(idsBiblioteca, aristas);
    }

    public async Task<bool> SincronizarAsync(IEnumerable<int> idsBiblioteca)
    {
        if (DateTime.UtcNow < _noReintentarAntesDeUtc) return false;

        await _candado.WaitAsync();
        try
        {
            var biblioteca = idsBiblioteca.Where(id => id > 0).ToHashSet();
            var sincronizados = (await _databaseService.ObtenerRelacionesSincronizadasAsync() ?? new List<RelacionAnimeSync>())
                .ToDictionary(s => s.AnimeId, s => s.FechaUtc);
            var ahora = DateTime.UtcNow;

            var pendientes = biblioteca
                .Where(id => !sincronizados.TryGetValue(id, out var fecha) || ahora - fecha > Caducidad)
                .ToList();
            if (pendientes.Count == 0) return false;

            bool hubieronDatos = false;
            int externosConsultados = 0;

            for (int salto = 0; salto < MaximoSaltos && pendientes.Count > 0; salto++)
            {
                var respuesta = await _animeTrackingService.ObtenerRelacionesLoteAsync(pendientes);
                if (respuesta == null || respuesta.Count == 0)
                {
                    // Sin datos (sin conexión o AniList caído): no volver a intentarlo enseguida.
                    if (!hubieronDatos) _noReintentarAntesDeUtc = DateTime.UtcNow + EsperaTrasFallo;
                    break;
                }

                await _databaseService.GuardarRelacionesAnimeAsync(respuesta);
                hubieronDatos = true;

                foreach (var id in respuesta.Keys) sincronizados[id] = ahora;
                externosConsultados += respuesta.Keys.Count(id => !biblioteca.Contains(id));

                // Siguiente ronda: vecinos aún no consultados que puedan unir franquicias.
                pendientes = respuesta.Values
                    .SelectMany(aristas => aristas)
                    .Where(a => AgrupadorFranquicias.EsRelacionDeFranquicia(a.Tipo))
                    .Select(a => a.RelacionadoId)
                    .Distinct()
                    .Where(id => !sincronizados.ContainsKey(id))
                    .Take(Math.Max(0, MaximoNodosExternos - externosConsultados))
                    .ToList();
            }

            return hubieronDatos;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("FranquiciaService", $"No se pudieron sincronizar las franquicias: {ex.Message}");
            return false;
        }
        finally
        {
            _candado.Release();
        }
    }

    public void Dispose() => _candado.Dispose();
}
