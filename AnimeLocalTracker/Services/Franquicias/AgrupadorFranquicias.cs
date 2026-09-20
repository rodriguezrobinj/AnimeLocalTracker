using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

/// <summary>
/// Agrupa animes en franquicias siguiendo las relaciones de AniList: temporadas (precuela/secuela),
/// películas y especiales (historia paralela), spin-offs, resúmenes y versiones alternativas.
/// Cálculo puro, sin red ni base de datos.
/// </summary>
public static class AgrupadorFranquicias
{
    /// <summary>Relaciones que enlazan una secuencia de temporadas: siempre unen.</summary>
    private static readonly HashSet<string> TiposDeSecuencia = new(StringComparer.OrdinalIgnoreCase) { "PREQUEL", "SEQUEL" };

    /// <summary>Relaciones "de obra derivada": unen, salvo que pasen por un nodo de cruce (ver <see cref="Agrupar"/>).</summary>
    private static readonly HashSet<string> TiposDerivados = new(StringComparer.OrdinalIgnoreCase)
    {
        "PARENT", "SIDE_STORY", "SPIN_OFF", "SUMMARY", "ALTERNATIVE"
    };

    /// <summary>Tipos que se siguen al explorar la red de relaciones (cualquiera que pueda unir franquicias).</summary>
    public static bool EsRelacionDeFranquicia(string? tipo) =>
        tipo != null && (TiposDeSecuencia.Contains(tipo) || TiposDerivados.Contains(tipo));

    /// <summary>
    /// Devuelve, para cada id de <paramref name="ids"/>, el id que identifica su franquicia (el menor de su grupo).
    /// Los nodos externos (animes relacionados que no están en la biblioteca) también sirven de puente: si tienes
    /// la temporada 1 y la 3, la 2 (que no tienes) las une.
    ///
    /// Protección contra series "cruce" (p. ej. Isekai Quartet, hijo de cuatro franquicias distintas): un nodo con
    /// dos o más padres distintos NO une franquicias por sus relaciones derivadas (padre/spin-off/historia paralela);
    /// si no, Re:Zero, Overlord y KonoSuba acabarían contados como una sola.
    /// </summary>
    public static Dictionary<int, int> Agrupar(IEnumerable<int> ids, IEnumerable<RelacionAnime> aristas)
    {
        var lista = aristas.Where(a => EsRelacionDeFranquicia(a.Tipo)).ToList();

        var nodosDeCruce = lista
            .Where(a => string.Equals(a.Tipo, "PARENT", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.AnimeId)
            .Where(g => g.Select(a => a.RelacionadoId).Distinct().Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet();

        var raiz = new Dictionary<int, int>();

        int Buscar(int x)
        {
            if (!raiz.TryGetValue(x, out var padre)) { raiz[x] = x; return x; }
            if (padre == x) return x;
            var r = Buscar(padre);
            raiz[x] = r;
            return r;
        }

        void Unir(int a, int b)
        {
            int ra = Buscar(a), rb = Buscar(b);
            if (ra == rb) return;
            // La raíz es siempre el id menor: resultado estable e independiente del orden de las aristas.
            if (ra < rb) raiz[rb] = ra; else raiz[ra] = rb;
        }

        foreach (var id in ids) Buscar(id);

        foreach (var arista in lista)
        {
            bool derivada = TiposDerivados.Contains(arista.Tipo);
            if (derivada && (nodosDeCruce.Contains(arista.AnimeId) || nodosDeCruce.Contains(arista.RelacionadoId))) continue;

            Unir(arista.AnimeId, arista.RelacionadoId);
        }

        return ids.Distinct().ToDictionary(id => id, Buscar);
    }
}
