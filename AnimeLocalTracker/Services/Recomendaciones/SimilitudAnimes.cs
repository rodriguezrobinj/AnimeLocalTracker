using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Recomendaciones;

/// <summary>Un anime pendiente que se parece al de la ficha, con los temas que comparten y sus episodios sin ver en disco.</summary>
public sealed record Parecido(AnimeItem Anime, double Puntuacion, IReadOnlyList<string> Temas, int EnDisco)
{
    public string TextoTemas => string.Join(" · ", Temas);

    public bool TieneEnDisco => EnDisco > 0;

    public string TextoEnDisco => string.Format(LocalizationService.T("Det_ParecidosEnDiscoFormato"), EnDisco);

    /// <summary>El lector de pantalla anuncia cada fila de la lista con esto: el título, no "Parecido { Anime = … }".</summary>
    public override string ToString() => Anime.Titulo;
}

/// <summary>
/// "Parecidos en tu biblioteca": cada anime es un vector con sus tags de AniList (pesados por el porcentaje que votó la
/// comunidad), sus géneros y las palabras de su sinopsis (TF-IDF sobre la biblioteca); el parecido es el coseno entre vectores.
/// Los tags mandan (captan tema y tono: "Time Manipulation", "Tragedy"); los géneros son muy generales y la sinopsis, ruidosa,
/// así que pesan menos. Sin tags (bases anteriores a v24 hasta la siguiente actualización) quedan géneros y sinopsis.
/// </summary>
internal static class SimilitudAnimes
{
    public const double Umbral = 0.15;
    public const int Maximo = 5;
    private const int MaxTemas = 3;
    private const double PesoTags = 1.0;
    private const double PesoGeneros = 0.5;
    private const double PesoSinopsis = 0.35;

    private static readonly Regex Palabra = new(@"\p{L}+", RegexOptions.Compiled);

    /// <summary>Palabras vacías del inglés (AniList da la sinopsis en inglés) y las que salen en casi cualquier sinopsis de anime.</summary>
    private static readonly HashSet<string> Vacias = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "also", "among", "another", "around", "because", "been", "before", "being", "between", "both",
        "came", "come", "comes", "could", "does", "down", "during", "each", "even", "every", "from", "have", "having", "here",
        "himself", "herself", "into", "itself", "just", "know", "like", "made", "make", "many", "more", "most", "much", "must",
        "never", "only", "other", "over", "same", "should", "since", "some", "soon", "such", "than", "that", "their", "them",
        "then", "there", "these", "they", "this", "those", "through", "under", "until", "upon", "very", "want", "were", "what",
        "when", "where", "which", "while", "will", "with", "without", "would", "your", "yours", "anime", "series", "season",
        "episode", "episodes", "source", "note", "based", "manga", "novel", "adaptation", "written", "illustrated",
    };

    public static IReadOnlyList<Parecido> Calcular(AnimeItem baseAnime, IReadOnlyList<AnimeItem> biblioteca,
        IReadOnlyDictionary<int, int> vistosPorAnime, IReadOnlyDictionary<int, int> enDiscoPorAnime)
    {
        var palabrasPorAnime = biblioteca.Concat(new[] { baseAnime }).DistinctBy(a => a.AniListId)
            .ToDictionary(a => a.AniListId, a => Palabras(a.SinopsisLimpia));
        var idf = Idf(palabrasPorAnime.Values);
        var vectorBase = Vector(baseAnime, palabrasPorAnime[baseAnime.AniListId], idf);

        return biblioteca
            .Where(a => a.AniListId != baseAnime.AniListId && EsPendiente(a, vistosPorAnime.GetValueOrDefault(a.AniListId)))
            .DistinctBy(a => a.AniListId)
            .Select(a => (Anime: a, Puntuacion: Coseno(vectorBase, Vector(a, palabrasPorAnime[a.AniListId], idf))))
            .Where(x => x.Puntuacion >= Umbral)
            .OrderByDescending(x => x.Puntuacion)
            .Take(Maximo)
            .Select(x => new Parecido(x.Anime, x.Puntuacion, TemasCompartidos(baseAnime, x.Anime), enDiscoPorAnime.GetValueOrDefault(x.Anime.AniListId)))
            .ToList();
    }

    /// <summary>Ni terminado ni abandonado, y sin todos sus episodios vistos.</summary>
    internal static bool EsPendiente(AnimeItem anime, int vistos)
        => anime.EstadoUsuario is not ("COMPLETED" or "DROPPED") && !(anime.TotalEpisodios > 0 && vistos >= anime.TotalEpisodios);

    /// <summary>Primero los tags comunes (por el menor de los dos porcentajes), después los géneros comunes, traducidos. Sin repetir.</summary>
    private static IReadOnlyList<string> TemasCompartidos(AnimeItem a, AnimeItem b)
    {
        var tagsB = EtiquetasAnime.Leer(b.Etiquetas).ToDictionary(t => t.Nombre, t => t.Rank, StringComparer.OrdinalIgnoreCase);
        var temas = EtiquetasAnime.Leer(a.Etiquetas)
            .Where(t => tagsB.ContainsKey(t.Nombre))
            .OrderByDescending(t => Math.Min(t.Rank, tagsB[t.Nombre]))
            .Select(t => t.Nombre)
            .ToList();

        var generosB = new HashSet<string>(b.GenerosLista, StringComparer.OrdinalIgnoreCase);
        foreach (var genero in a.GenerosLista.Where(generosB.Contains))
        {
            if (!temas.Contains(genero, StringComparer.OrdinalIgnoreCase)) temas.Add(genero);
        }

        return temas.Take(MaxTemas).Select(t => tagsB.ContainsKey(t) ? t : LocalizationService.TraducirGenero(t)).ToList();
    }

    private static Dictionary<string, double> Vector(AnimeItem anime, IReadOnlyDictionary<string, int> palabras, IReadOnlyDictionary<string, double> idf)
    {
        var vector = new Dictionary<string, double>(StringComparer.Ordinal);
        Sumar(vector, EtiquetasAnime.Leer(anime.Etiquetas).Select(t => ("t:" + t.Nombre.ToLowerInvariant(), t.Rank / 100.0)), PesoTags);
        Sumar(vector, anime.GenerosLista.Select(g => ("g:" + g.ToLowerInvariant(), 1.0)), PesoGeneros);
        Sumar(vector, palabras.Select(p => ("w:" + p.Key, p.Value * idf.GetValueOrDefault(p.Key))), PesoSinopsis);
        return vector;
    }

    /// <summary>Cada parte se normaliza antes de pesarla: así un anime con 15 tags no aplasta a los géneros y la sinopsis.</summary>
    private static void Sumar(Dictionary<string, double> vector, IEnumerable<(string Clave, double Valor)> parte, double peso)
    {
        var lista = parte.Where(p => p.Valor > 0).ToList();
        double norma = Math.Sqrt(lista.Sum(p => p.Valor * p.Valor));
        if (norma == 0) return;
        foreach (var (clave, valor) in lista) vector[clave] = vector.GetValueOrDefault(clave) + valor / norma * peso;
    }

    private static double Coseno(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        double producto = a.Sum(p => p.Value * b.GetValueOrDefault(p.Key));
        double normas = Math.Sqrt(a.Values.Sum(v => v * v)) * Math.Sqrt(b.Values.Sum(v => v * v));
        return normas == 0 ? 0 : producto / normas;
    }

    private static Dictionary<string, int> Palabras(string texto)
        => Palabra.Matches(texto.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(p => p.Length >= 4 && !Vacias.Contains(p))
            .GroupBy(p => p)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Una palabra que sale en casi todas las sinopsis de la biblioteca no dice nada del parecido.</summary>
    private static Dictionary<string, double> Idf(IEnumerable<Dictionary<string, int>> documentos)
    {
        var lista = documentos.ToList();
        return lista.SelectMany(d => d.Keys).GroupBy(p => p)
            .ToDictionary(g => g.Key, g => Math.Log((lista.Count + 1.0) / (g.Count() + 1.0)) + 1.0);
    }
}
