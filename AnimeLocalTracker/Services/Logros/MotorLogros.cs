using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Logros;

/// <summary>
/// Cálculo puro de logros: sin base de datos ni UI, así que se prueba con datos en memoria.
/// Las horas y los días se miden en HORA LOCAL: sqlite-net devuelve las fechas con Kind=Unspecified
/// pero son UTC (persistence.md #4), por eso se usa <see cref="ALocal"/> y no se compara Kind == Utc.
/// </summary>
public static class MotorLogros
{
    /// <summary>Mide todas las métricas que consumen las familias del catálogo (clave = Id de la familia).</summary>
    public static Dictionary<string, double> CalcularMetricas(
        IReadOnlyList<AnimeItem> animes,
        IReadOnlyList<RegistroEpisodio> registros)
    {
        var animesPorId = new Dictionary<int, AnimeItem>();
        foreach (var anime in animes) animesPorId.TryAdd(anime.AniListId, anime);

        var vistos = registros.Where(r => r.VistoLocal).ToList();

        // Solo el reproductor estampa UltimaReproduccion: los marcados a mano no tienen fecha y
        // cuentan para los totales pero no para las métricas de día/hora.
        var conFecha = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .Select(r => (Registro: r, Local: ALocal(r.UltimaReproduccion!.Value)))
            .ToList();

        var episodiosVistosPorAnime = vistos
            .GroupBy(r => r.AniListId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.NumeroEpisodio).Distinct().Count());

        var animesVistos = animesPorId.Values
            .Where(a => episodiosVistosPorAnime.GetValueOrDefault(a.AniListId) > 0)
            .ToList();

        var dias = conFecha.Select(x => x.Local.Date).Distinct().OrderBy(d => d).ToList();

        var generos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var anime in animesVistos)
        {
            if (string.IsNullOrWhiteSpace(anime.Generos)) continue;
            foreach (var genero in anime.Generos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                generos[genero] = generos.GetValueOrDefault(genero) + 1;
            }
        }

        bool Completada(AnimeItem a, int minimoEpisodios) =>
            a.TotalEpisodios > minimoEpisodios && episodiosVistosPorAnime.GetValueOrDefault(a.AniListId) >= a.TotalEpisodios;

        return new Dictionary<string, double>
        {
            ["maraton_dia"] = MaximoPorGrupo(conFecha.GroupBy(x => x.Local.Date)),
            ["horas"] = EstimadorDuracion.SegundosVistos(vistos) / 3600.0,
            ["episodios"] = vistos.Count,

            ["racha"] = RachaMaxima(dias),
            ["dias_activos"] = dias.Count,

            ["series_completadas"] = animesPorId.Values.Count(a => Completada(a, 0)),
            ["series_largas"] = animesPorId.Values.Count(a => Completada(a, 50)),
            ["biblioteca"] = animesPorId.Count,
            ["favoritos"] = animesPorId.Values.Count(a => a.EsFavorito),

            ["generos_variedad"] = generos.Count,
            ["genero_especialista"] = generos.Count > 0 ? generos.Values.Max() : 0,

            ["buho"] = conFecha.Count(x => x.Local.Hour is >= 2 and < 5),
            ["madrugador"] = conFecha.Count(x => x.Local.Hour is >= 5 and < 8),

            ["clasicos"] = animesVistos.Count(a => a.AnioLanzamiento is > 0 and < 2000),
            ["decadas"] = animesVistos.Where(a => a.AnioLanzamiento > 0).Select(a => a.AnioLanzamiento / 10).Distinct().Count(),

            ["insomnio"] = MaximoPorGrupo(conFecha.Where(x => x.Local.Hour < 6).GroupBy(x => x.Local.Date)),
            ["de_una_sentada"] = MaximoPorGrupo(conFecha.GroupBy(x => (x.Registro.AniListId, x.Local.Date))),
        };
    }

    /// <summary>
    /// Combina las métricas con lo ya desbloqueado antes: un nivel conseguido nunca se pierde aunque
    /// las métricas actuales bajen (p. ej. si borras una serie de la biblioteca).
    /// </summary>
    public static ResumenLogros Evaluar(
        IReadOnlyDictionary<string, double> metricas,
        IReadOnlyDictionary<string, (int Nivel, DateTime? FechaUtc)> desbloqueados)
    {
        var estados = new List<LogroEstado>(CatalogoLogros.Todos.Count);

        foreach (var definicion in CatalogoLogros.Todos)
        {
            double valor = metricas.GetValueOrDefault(definicion.Id);
            int nivelPorDatos = definicion.Umbrales.Count(umbral => valor >= umbral);

            desbloqueados.TryGetValue(definicion.Id, out var guardado);
            int nivel = Math.Max(nivelPorDatos, guardado.Nivel);

            estados.Add(new LogroEstado(definicion, valor, nivel, guardado.FechaUtc));
        }

        return new ResumenLogros(estados);
    }

    /// <summary>Máxima cantidad de episodios en un mismo grupo (día, o serie+día).</summary>
    private static double MaximoPorGrupo<TKey, TItem>(IEnumerable<IGrouping<TKey, TItem>> grupos)
    {
        int maximo = 0;
        foreach (var grupo in grupos) maximo = Math.Max(maximo, grupo.Count());
        return maximo;
    }

    private static int RachaMaxima(List<DateTime> diasOrdenados)
    {
        if (diasOrdenados.Count == 0) return 0;

        int maxima = 1, actual = 1;
        for (int i = 1; i < diasOrdenados.Count; i++)
        {
            actual = (diasOrdenados[i] - diasOrdenados[i - 1]).Days == 1 ? actual + 1 : 1;
            maxima = Math.Max(maxima, actual);
        }
        return maxima;
    }

    /// <summary>Regla persistence.md #4: Unspecified = UTC; no comparar Kind == Utc.</summary>
    internal static DateTime ALocal(DateTime fecha) =>
        fecha.Kind == DateTimeKind.Local ? fecha : fecha.ToLocalTime();
}
