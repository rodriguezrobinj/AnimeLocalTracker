using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Fechas de seguimiento automáticas para AniList: la de inicio (primer visionado real) y la de fin (último episodio
/// oficial de un anime ya finalizado). Solo cuenta el VISIONADO REAL (episodios con fecha de reproducción): el
/// marcado manual como visto no inventa fechas. Nunca pisa una fecha que AniList ya tenga.
/// </summary>
public static class SeguimientoFechas
{
    /// <summary>
    /// Calcula qué fechas faltan en AniList y con qué valor (día local de la reproducción).
    /// </summary>
    /// <param name="anime">Anime local (estado de emisión y total oficial de episodios). Sin él no se calcula la fecha de fin.</param>
    /// <param name="registros">Registros locales de episodios del anime.</param>
    /// <param name="remotoPrevio">Entrada de AniList ANTES de actualizar el progreso (null = aún no está en la lista).</param>
    /// <param name="episodioMaximo">Episodio más alto visto en esta tanda.</param>
    public static (DateTime? Inicio, DateTime? Fin) Calcular(AnimeItem? anime, IEnumerable<RegistroEpisodio> registros, AniListMediaList? remotoPrevio, int episodioMaximo)
    {
        var reproducciones = registros
            .Where(r => r.VistoLocal && r.UltimaReproduccion.HasValue)
            .ToList();
        if (reproducciones.Count == 0) return (null, null);

        // Un anime ya completado (o en re-visionado) en AniList es un re-visionado: no se tocan sus fechas.
        string estadoRemoto = remotoPrevio?.Status ?? string.Empty;
        if (estadoRemoto is "COMPLETED" or "REPEATING") return (null, null);

        DateTime? inicio = null;
        DateTime? fin = null;

        // Inicio: primer visionado real, solo si en AniList aún no había nada visto ni fecha de inicio.
        if (!TieneFecha(remotoPrevio?.StartedAt) && (remotoPrevio?.Progress ?? 0) == 0)
        {
            inicio = ADiaLocal(reproducciones.Min(r => r.UltimaReproduccion!.Value));
        }

        // Fin: el ÚLTIMO EPISODIO OFICIAL (total de AniList) de un anime FINALIZADO. En emisión, o con total
        // desconocido, "el último de la lista" no es el final de la serie, así que no se guarda nada.
        if (anime is { Estado: "FINISHED", TotalEpisodios: > 0 }
            && !TieneFecha(remotoPrevio?.CompletedAt)
            && episodioMaximo >= anime.TotalEpisodios)
        {
            var ultimo = reproducciones
                .Where(r => r.NumeroEpisodio >= anime.TotalEpisodios)
                .OrderByDescending(r => r.UltimaReproduccion)
                .FirstOrDefault();
            if (ultimo != null) fin = ADiaLocal(ultimo.UltimaReproduccion!.Value);
        }

        // Coherencia: la fecha de fin nunca puede ser anterior a la de inicio.
        if (inicio.HasValue && fin.HasValue && fin < inicio) fin = inicio;

        return (inicio, fin);
    }

    /// <summary>
    /// Calcula y envía a AniList las fechas que falten. Los fallos no se propagan: es un extra que nunca debe
    /// impedir marcar el episodio como visto. Devuelve true si se guardó alguna fecha.
    /// </summary>
    public static async Task<bool> AplicarAsync(
        IDatabaseService database,
        IAnimeTrackingService tracking,
        int aniListId,
        AniListMediaList? remotoPrevio,
        int episodioMaximo,
        string token)
    {
        try
        {
            var registros = await database.ObtenerRegistrosPorAnimeAsync(aniListId);
            if (registros == null || registros.Count == 0) return false;

            var anime = await database.ObtenerAnimePorIdAsync(aniListId);
            var (inicio, fin) = Calcular(anime, registros, remotoPrevio, episodioMaximo);
            if (!inicio.HasValue && !fin.HasValue) return false;

            bool ok = await tracking.GuardarFechasSeguimientoAsync(aniListId, inicio, fin, token);
            if (ok)
            {
                AppLogger.Info("SeguimientoFechas", $"Fechas guardadas en AniList para {aniListId}: inicio={inicio:yyyy-MM-dd}, fin={fin:yyyy-MM-dd}.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SeguimientoFechas", $"No se pudieron guardar las fechas automáticas de {aniListId}: {ex.Message}");
            return false;
        }
    }

    private static bool TieneFecha(AniListFuzzyDate? fecha) => fecha?.Year.HasValue == true;

    /// <summary>UltimaReproduccion es UTC (sqlite la devuelve como Unspecified): el día que importa es el del usuario.</summary>
    private static DateTime ADiaLocal(DateTime fecha) =>
        (fecha.Kind == DateTimeKind.Local ? fecha : DateTime.SpecifyKind(fecha, DateTimeKind.Utc).ToLocalTime()).Date;
}
