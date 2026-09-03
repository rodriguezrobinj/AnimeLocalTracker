using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Similitud entre títulos de anime (fallback hermético cuando el daemon Python
/// con rapidfuzz no está disponible). Combina Levenshtein normalizado y
/// Jaccard por tokens — tolera palabras/signos distintos pero exige coincidencia
/// sustancial: 1.0 = idéntico, ~0.75 = "Battle of Gods" vs "Película 14: Battle of Gods".
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
