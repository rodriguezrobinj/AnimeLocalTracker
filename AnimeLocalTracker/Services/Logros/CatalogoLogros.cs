using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Services.Logros;

/// <summary>
/// Catálogo de familias de logros. Añadir una nueva = una entrada aquí + su métrica en
/// <see cref="MotorLogros.CalcularMetricas"/> + las claves <c>Logro_{Id}_Titulo</c>/<c>_Desc</c>
/// en ambos diccionarios de LocalizationService. La estructura de la pantalla no cambia.
/// Colores: solo los ya presentes en la paleta de la app (nada de literales nuevos).
/// </summary>
public static class CatalogoLogros
{
    public static IReadOnlyList<LogroDefinicion> Todos { get; } = new List<LogroDefinicion>
    {
        // ── Maratón: volumen y tiempo ─────────────────────────────────────────
        new("maraton_dia", CategoriaLogro.Maraton, "Run", "#FB923C", new double[] { 3, 6, 10, 15, 24 }),
        new("horas", CategoriaLogro.Maraton, "BookOpenPageVariant", "#34D399", new double[] { 10, 50, 100, 300, 750 }),
        new("episodios", CategoriaLogro.Maraton, "PlayBoxMultiple", "#60A5FA", new double[] { 25, 100, 250, 600, 1500 }),

        // ── Constancia ────────────────────────────────────────────────────────
        new("racha", CategoriaLogro.Constancia, "Fire", "#F87171", new double[] { 3, 7, 14, 30, 60 }),
        new("dias_activos", CategoriaLogro.Constancia, "CalendarCheck", "#60A5FA", new double[] { 7, 30, 90, 200, 365 }),

        // ── Colección ─────────────────────────────────────────────────────────
        new("series_completadas", CategoriaLogro.Coleccion, "TrophyAward", "#FBBF24", new double[] { 1, 5, 15, 30, 60 }),
        new("series_largas", CategoriaLogro.Coleccion, "Infinity", "#A78BFA", new double[] { 1, 2, 4, 8, 15 }),
        new("biblioteca", CategoriaLogro.Coleccion, "LibraryShelves", "#818CF8", new double[] { 25, 75, 150, 300, 600 }),
        new("favoritos", CategoriaLogro.Coleccion, "Heart", "#F472B6", new double[] { 1, 5, 15, 30, 60 }),

        // ── Géneros ───────────────────────────────────────────────────────────
        new("generos_variedad", CategoriaLogro.Generos, "Palette", "#A78BFA", new double[] { 3, 6, 10, 15, 20 }),
        new("genero_especialista", CategoriaLogro.Generos, "SwordCross", "#F87171", new double[] { 5, 15, 30, 50, 80 }),

        // ── Horarios ──────────────────────────────────────────────────────────
        new("buho", CategoriaLogro.Horarios, "Owl", "#818CF8", new double[] { 1, 5, 15, 40, 100 }),
        new("madrugador", CategoriaLogro.Horarios, "WeatherSunny", "#FBBF24", new double[] { 1, 5, 15, 40, 100 }),

        // ── Épocas ────────────────────────────────────────────────────────────
        new("clasicos", CategoriaLogro.Epocas, "TelevisionClassic", "#94A3B8", new double[] { 1, 3, 6, 12, 25 }),
        new("decadas", CategoriaLogro.Epocas, "Hourglass", "#38BDF8", new double[] { 2, 3, 4, 5, 6 }),

        // ── Secretos: nombre y descripción ocultos hasta conseguir el primer nivel ──
        new("insomnio", CategoriaLogro.Secretos, "BedClock", "#818CF8", new double[] { 3, 6 }, Secreto: true),
        new("de_una_sentada", CategoriaLogro.Secretos, "FlagCheckered", "#34D399", new double[] { 12, 24 }, Secreto: true),
    };

    public static int NivelesTotales { get; } = Todos.Sum(l => l.NivelesTotales);
}
