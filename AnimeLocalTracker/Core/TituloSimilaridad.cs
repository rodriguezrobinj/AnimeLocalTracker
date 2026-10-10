using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Similitud entre títulos de anime. <see cref="Similitud"/> combina Levenshtein normalizado y
/// Jaccard por tokens — tolera palabras/signos distintos pero exige coincidencia
/// sustancial: 1.0 = idéntico, ~0.75 = "Battle of Gods" vs "Película 14: Battle of Gods".
/// <see cref="SimilitudPorLetras"/> compara letra a letra (erratas, romanizaciones).
/// </summary>
public static class TituloSimilaridad
{
    /// <summary>Mejor similitud (0..1) entre el título consultado y cualquier candidato.</summary>
    public static double MejorSimilitud(string? consulta, IEnumerable<string> candidatos)
    {
        if (string.IsNullOrWhiteSpace(consulta)) return 0;
        double mejor = 0;
        foreach (var candidato in candidatos)
        {
            mejor = Math.Max(mejor, Similitud(consulta, candidato));
        }
        return mejor;
    }

    public static double Similitud(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

        string ta = Normalizar(a);
        string tb = Normalizar(b);
        if (ta == tb) return 1.0;

        var tokensA = ta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = tb.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Levenshtein a nivel de TOKENS (los espacios por caracteres diluían la
        // métrica en títulos cortos) — 1 - dist/max
        double lev = 1.0 - (double)DistanciaTokens(tokensA, tokensB) / Math.Max(tokensA.Length, tokensB.Length);

        int inter = tokensA.Intersect(tokensB).Count();
        int union = tokensA.Union(tokensB).Count();
        double jac = union == 0 ? 0 : (double)inter / union;

        return Math.Max(lev, jac);
    }

    /// <summary>
    /// Parecido letra a letra (0..1), sin importar mayúsculas ni el orden de las palabras. Tolera lo que <see cref="Similitud"/>,
    /// que compara palabras enteras, no perdona: una errata o una romanización distinta ("Shippuuden" / "Shippuden").
    /// Es el mismo cálculo que token_sort_ratio de rapidfuzz, que antes se pedía al daemon Python (y sin daemon la app decidía
    /// con menos criterios): palabras ordenadas y 2 × subsecuencia común / suma de largos.
    /// No distingue entregas de una franquicia ("Dragon Ball Z" / "Dragon Ball Super" da 0,80): quien decida con él comprueba
    /// antes <see cref="SoloCambiaLaEscritura"/>.
    /// </summary>
    public static double SimilitudPorLetras(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

        string ta = PalabrasOrdenadas(a);
        string tb = PalabrasOrdenadas(b);
        return 2.0 * SubsecuenciaComun(ta, tb) / (ta.Length + tb.Length);
    }

    /// <summary>Parecido mínimo entre las palabras que dos títulos no comparten para tomarlas por la misma, escrita distinto.</summary>
    private const double UmbralMismaPalabra = 0.6;

    /// <summary>
    /// True si los dos títulos solo se diferencian en cómo están escritos (errata, romanización, palabras pegadas) y no en una
    /// palabra entera. Es lo que <see cref="SimilitudPorLetras"/> no ve: "Dragon Ball Z" y "Dragon Ball Super" comparten casi
    /// todas las letras y son animes distintos; lo que los separa es justo la palabra que no comparten. Se quitan las palabras
    /// iguales y se compara el resto: si a uno le sobra una palabra ("Dragon Ball" / "Dragon Ball Z") o las que quedan no se
    /// parecen ("z" / "super"), no es un asunto de escritura.
    /// </summary>
    public static bool SoloCambiaLaEscritura(string? a, string? b)
    {
        var restoA = FirmaTitulo.Normalizar(a).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var restoB = FirmaTitulo.Normalizar(b).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (restoA.Count == 0 || restoB.Count == 0) return false;

        restoA.RemoveAll(restoB.Remove);
        if (restoA.Count == 0 || restoB.Count == 0) return restoA.Count == restoB.Count;

        string letrasA = string.Concat(restoA);
        string letrasB = string.Concat(restoB);
        return 2.0 * SubsecuenciaComun(letrasA, letrasB) / (letrasA.Length + letrasB.Length) >= UmbralMismaPalabra;
    }

    private static string PalabrasOrdenadas(string s)
    {
        var palabras = s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(palabras, StringComparer.Ordinal);
        return string.Join(' ', palabras);
    }

    /// <summary>Largo de la subsecuencia común más larga (letras en el mismo orden, no necesariamente seguidas).</summary>
    private static int SubsecuenciaComun(string a, string b)
    {
        var anterior = new int[b.Length + 1];
        var actual = new int[b.Length + 1];
        foreach (char letra in a)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                actual[j] = letra == b[j - 1] ? anterior[j - 1] + 1 : Math.Max(anterior[j], actual[j - 1]);
            }
            (anterior, actual) = (actual, anterior);
        }
        return anterior[b.Length];
    }

    private static string Normalizar(string s)
    {
        // Minúsculas, solo letras/números/espacios, espacios compactados
        var chars = s.ToLowerInvariant()
                     .Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ')
                     .ToArray();
        string limpio = new string(chars);
        return string.Join(" ", limpio.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int DistanciaTokens(string[] a, string[] b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var anterior = new int[b.Length + 1];
        var actual = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) anterior[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            actual[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int coste = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
                actual[j] = Math.Min(Math.Min(actual[j - 1] + 1, anterior[j] + 1), anterior[j - 1] + coste);
            }
            (anterior, actual) = (actual, anterior);
        }
        return anterior[b.Length];
    }
}
