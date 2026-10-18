using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

public enum EstadoParteSaga { Vista, AMedias, Descargada, EnBiblioteca, NoLaTienes }

/// <summary>
/// Una fila del orden de visionado. <paramref name="Numero"/>: 1, 2… en la línea principal; 0 en extras y "aparte".
/// <paramref name="Titulo"/> null = de esa parte aún no hay datos (sin conexión la primera vez).
/// </summary>
public sealed record ParteOrdenVisionado(int AniListId, int Numero, bool EsExtra, bool EsAparte, AnimeItem? Anime, string? Titulo,
    string Formato, int Anio, string? UrlPortada, EstadoParteSaga Estado);

public sealed record OrdenVisionadoLeido(IReadOnlyList<ParteOrdenVisionado> Partes, bool SinConexion);

public interface IOrdenVisionadoService
{
    /// <summary>Si el anime tiene alguna relación de saga guardada, o si sus relaciones nunca se consultaron. Sin red.</summary>
    Task<bool> TieneFranquiciaOSinConsultarAsync(int aniListId);

    /// <summary>El orden con lo ya guardado. Sin red. Nunca lanza.</summary>
    Task<OrdenVisionadoLeido> LeerGuardadoAsync(int aniListId);

    /// <summary>Consulta a AniList lo que falte o haya caducado (relaciones y datos de las partes) y devuelve el orden. Nunca lanza.</summary>
    Task<OrdenVisionadoLeido> ActualizarAsync(int aniListId, CancellationToken ct);

    /// <summary>Da de alta la parte como en la pestaña Agregar. Null si no se pudo (sin red). Si ya estaba, devuelve la de la biblioteca.</summary>
    Task<AnimeItem?> AnadirABibliotecaAsync(int aniListId);
}

/// <summary>Orden de visionado de la ficha (ver docs/plan-orden-visionado-franquicia.md).</summary>
public sealed class OrdenVisionadoService : IOrdenVisionadoService
{
    /// <summary>Los datos de una parte se refrescan cada cierto tiempo (cambian portadas, fechas y nº de episodios).</summary>
    private static readonly TimeSpan Caducidad = TimeSpan.FromDays(30);

    private readonly IDatabaseService _db;
    private readonly IFranquiciaService _franquicias;
    private readonly IAnimeTrackingService _anilist;
    private readonly AnimeLibraryService _biblioteca;

    public OrdenVisionadoService(IDatabaseService db, IFranquiciaService franquicias, IAnimeTrackingService anilist, AnimeLibraryService biblioteca)
    {
        _db = db;
        _franquicias = franquicias;
        _anilist = anilist;
        _biblioteca = biblioteca;
    }

    public static EstadoParteSaga EstadoDe(AnimeItem? anime, int vistos, bool enDisco)
    {
        if (anime == null) return EstadoParteSaga.NoLaTienes;
        if (string.Equals(anime.EstadoUsuario, "COMPLETED", StringComparison.OrdinalIgnoreCase)
            || (anime.TotalEpisodios > 0 && vistos >= anime.TotalEpisodios)) return EstadoParteSaga.Vista;
        if (vistos > 0) return EstadoParteSaga.AMedias;
        return enDisco ? EstadoParteSaga.Descargada : EstadoParteSaga.EnBiblioteca;
    }

    public async Task<bool> TieneFranquiciaOSinConsultarAsync(int aniListId)
    {
        try
        {
            var sync = await _db.ObtenerRelacionesSincronizadasAsync();
            if (!sync.Any(s => s.AnimeId == aniListId)) return true;
            var aristas = await _db.ObtenerRelacionesAnimeAsync();
            return aristas.Any(a => (a.AnimeId == aniListId || a.RelacionadoId == aniListId) && OrdenVisionado.CuentaParaElOrden(a.Tipo));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo saber si el anime {aniListId} tiene saga: {ex.Message}");
            return false;
        }
    }

    public Task<OrdenVisionadoLeido> LeerGuardadoAsync(int aniListId) => Task.Run(async () =>
    {
        try
        {
            return new OrdenVisionadoLeido(await LeerPartesAsync(aniListId), false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo leer el orden de visionado de {aniListId}: {ex.Message}");
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), false);
        }
    });

    public Task<OrdenVisionadoLeido> ActualizarAsync(int aniListId, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            // FranquiciaService decide si hace falta (caducidad de 30 días, espera tras un fallo) y explora la saga con sus topes.
            await _franquicias.SincronizarAsync(new[] { aniListId });
            bool sinRelaciones = !(await _db.ObtenerRelacionesSincronizadasAsync()).Any(s => s.AnimeId == aniListId);
            ct.ThrowIfCancellationRequested();

            var partes = await LeerPartesAsync(aniListId);
            var info = (await _db.ObtenerRelacionadosInfoAsync()).ToDictionary(i => i.AniListId);
            var ahora = DateTime.UtcNow;
            var pendientes = partes.Select(p => p.AniListId)
                .Where(id => !info.TryGetValue(id, out var d) || ahora - d.FechaUtc > Caducidad)
                .ToList();

            bool sinDatos = false;
            if (pendientes.Count > 0)
            {
                var recibidos = await _anilist.ObtenerAnimesPorIdsLoteAsync(pendientes);
                ct.ThrowIfCancellationRequested();
                if (recibidos.Count == 0) sinDatos = true;
                else
                {
                    await _db.GuardarRelacionadosInfoAsync(recibidos.Values.Select(m => Convertir(m, ahora)).ToList());
                    partes = await LeerPartesAsync(aniListId);
                }
            }
            return new OrdenVisionadoLeido(partes, sinRelaciones || sinDatos);
        }
        catch (OperationCanceledException)
        {
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo actualizar el orden de visionado de {aniListId}: {ex.Message}");
            return new OrdenVisionadoLeido(Array.Empty<ParteOrdenVisionado>(), true);
        }
    });

    public async Task<AnimeItem?> AnadirABibliotecaAsync(int aniListId)
    {
        try
        {
            var media = await _anilist.ObtenerAnimePorIdAsync(aniListId);
            if (media == null) return null;
            return await _biblioteca.CrearYGuardarAnimeAsync(media, TituloDe(media))
                ?? await _db.ObtenerAnimePorIdAsync(aniListId); // ya estaba (se añadió desde otra pantalla)
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoService", $"No se pudo añadir el anime {aniListId}: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<ParteOrdenVisionado>> LeerPartesAsync(int aniListId)
    {
        var aristas = await _db.ObtenerRelacionesAnimeAsync();
        var animes = (await _db.ObtenerTodosLosAnimesAsync()).GroupBy(a => a.AniListId).ToDictionary(g => g.Key, g => g.First());
        var info = (await _db.ObtenerRelacionadosInfoAsync()).ToDictionary(i => i.AniListId);
        var vistos = await _db.ObtenerEpisodiosVistosPorAnimeAsync();
        var enDisco = (await _db.ObtenerTodosLosRegistrosAsync())
            .Where(r => !string.IsNullOrEmpty(r.RutaArchivo)).Select(r => r.AniListId).ToHashSet();

        int Fecha(int id) => info.TryGetValue(id, out var d) && d.Anio is > 0
            ? OrdenVisionado.FechaComparable(d.Anio, d.Mes)
            : animes.TryGetValue(id, out var a) ? a.OrdenEstreno : 0;

        ParteOrdenVisionado Parte(int id, int numero, bool esExtra, bool esAparte)
        {
            animes.TryGetValue(id, out var anime);
            info.TryGetValue(id, out var datos);
            int anio = datos?.Anio ?? anime?.AnioLanzamiento ?? 0;
            string? titulo = anime?.Titulo ?? (string.IsNullOrWhiteSpace(datos?.Titulo) ? null : datos!.Titulo);
            string? portada = anime != null ? anime.PortadaVisible : datos?.UrlPortada;
            var estado = EstadoDe(anime, vistos.GetValueOrDefault(id), enDisco.Contains(id));
            return new ParteOrdenVisionado(id, numero, esExtra, esAparte, anime, titulo, datos?.Formato ?? string.Empty, anio,
                string.IsNullOrWhiteSpace(portada) ? null : portada, estado);
        }

        var orden = OrdenVisionado.Construir(aniListId, aristas, Fecha);
        var partes = new List<ParteOrdenVisionado>();
        int numero = 0;
        foreach (var parte in orden.LineaPrincipal)
        {
            partes.Add(Parte(parte.AniListId, ++numero, esExtra: false, esAparte: false));
            partes.AddRange(parte.Extras.Select(id => Parte(id, 0, esExtra: true, esAparte: false)));
        }
        partes.AddRange(orden.Aparte.Select(id => Parte(id, 0, esExtra: false, esAparte: true)));
        return partes;
    }

    private static AnimeRelacionadoInfo Convertir(AniListMedia media, DateTime ahora) => new()
    {
        AniListId = media.Id,
        Titulo = TituloDe(media),
        Formato = media.Format ?? string.Empty,
        Anio = media.StartDate?.Year,
        Mes = media.StartDate?.Month,
        Dia = media.StartDate?.Day,
        Episodios = media.Episodes,
        UrlPortada = media.CoverImage?.ExtraLarge ?? media.CoverImage?.Large ?? string.Empty,
        FechaUtc = ahora,
    };

    /// <summary>Mismo orden que la pestaña Agregar (<c>AnimeBusquedaItem.TituloPrincipal</c>): romaji, el preferido y el inglés.</summary>
    private static string TituloDe(AniListMedia media) =>
        !string.IsNullOrWhiteSpace(media.Title?.Romaji) ? media.Title!.Romaji! :
        !string.IsNullOrWhiteSpace(media.Title?.UserPreferred) ? media.Title!.UserPreferred! :
        !string.IsNullOrWhiteSpace(media.Title?.English) ? media.Title!.English! :
        LocalizationService.T("Media_TituloDesconocido");
}
