using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Franquicias;

/// <summary>Una parte de la línea principal de una saga con los extras (películas, OVAs, resúmenes) que cuelgan de ella.</summary>
public sealed record ParteLineaPrincipal(int AniListId, IReadOnlyList<int> Extras);

/// <summary>Orden de visionado de una saga: la línea principal y, aparte, spin-offs y versiones alternativas.</summary>
public sealed record ResultadoOrdenVisionado(IReadOnlyList<ParteLineaPrincipal> LineaPrincipal, IReadOnlyList<int> Aparte);

/// <summary>
/// Orden de visionado a partir de las relaciones de AniList (ver docs/plan-orden-visionado-franquicia.md). Cálculo puro, sin red ni
/// base de datos. La línea principal sigue la cadena de precuelas y secuelas; los extras cuelgan de la parte con la que se relacionan;
/// spin-offs y versiones alternativas van aparte. Los cruces de personajes (CHARACTER) y lo marcado como OTHER no cuentan.
/// </summary>
public static class OrdenVisionado
{
    /// <summary>Tope de partes de la línea principal: con más, se quedan las más cercanas al anime de la ficha.</summary>
    public const int MaximoPartes = 60;

    private static readonly HashSet<string> Secuencia = new(StringComparer.OrdinalIgnoreCase) { "PREQUEL", "SEQUEL" };
    private static readonly HashSet<string> Colgados = new(StringComparer.OrdinalIgnoreCase) { "SIDE_STORY", "SUMMARY", "PARENT" };
    private static readonly HashSet<string> DeAparte = new(StringComparer.OrdinalIgnoreCase) { "SPIN_OFF", "ALTERNATIVE" };

    public static bool CuentaParaElOrden(string? tipo) =>
        tipo != null && (Secuencia.Contains(tipo) || Colgados.Contains(tipo) || DeAparte.Contains(tipo));

    /// <summary>
    /// Fecha en la escala de <see cref="AnimeItem.OrdenEstreno"/> (año × 10 + temporada 1–4). La temporada sale del mes como la
    /// cuenta AniList (invierno = enero–marzo). 0 = sin fecha.
    /// </summary>
    public static int FechaComparable(int? anio, int? mes) =>
        anio is not > 0 ? 0 : anio.Value * 10 + (mes is >= 1 and <= 12 ? (mes.Value - 1) / 3 + 1 : 0);

    /// <param name="fecha">Fecha comparable de cada id (<see cref="FechaComparable"/>; 0 = sin fecha, va al final de su grupo).</param>
    public static ResultadoOrdenVisionado Construir(int aniListId, IEnumerable<RelacionAnime> aristas, Func<int, int> fecha)
    {
        var lista = aristas.Where(a => a.AnimeId != a.RelacionadoId && CuentaParaElOrden(a.Tipo)).ToList();
        (int, int) Clave(int id)
        {
            int f = fecha(id);
            return (f > 0 ? f : int.MaxValue, id);
        }

        var vecinosSecuencia = Vecinos(lista, Secuencia);
        var vecinosColgados = Vecinos(lista, Colgados);
        var vecinosAparte = Vecinos(lista, DeAparte);

        // Una OVA o película sin precuela/secuela propia se ve dentro de la saga de su padre.
        int inicio = aniListId;
        if (!vecinosSecuencia.ContainsKey(aniListId))
        {
            int padre = De(vecinosColgados, aniListId).Where(vecinosSecuencia.ContainsKey).OrderBy(Clave).FirstOrDefault();
            if (padre > 0) inicio = padre;
        }

        // Recorrido en anchura desde el inicio: con el tope se quedan las partes más cercanas.
        var linea = new HashSet<int> { inicio };
        var cola = new Queue<int>();
        cola.Enqueue(inicio);
        while (cola.Count > 0 && linea.Count < MaximoPartes)
        {
            foreach (int vecino in De(vecinosSecuencia, cola.Dequeue()).OrderBy(Clave))
            {
                if (linea.Count >= MaximoPartes) break;
                if (linea.Add(vecino)) cola.Enqueue(vecino);
            }
        }

        var ordenLinea = OrdenTopologico(linea, lista, Clave);
        var posicion = ordenLinea.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var extras = ordenLinea.ToDictionary(id => id, _ => new List<int>());
        var colocados = new HashSet<int>(linea);
        foreach (int extra in linea.SelectMany(id => De(vecinosColgados, id)).Where(id => !linea.Contains(id)).Distinct().OrderBy(Clave))
        {
            int duenio = De(vecinosColgados, extra).Where(linea.Contains).OrderByDescending(id => posicion[id]).First();
            extras[duenio].Add(extra);
            colocados.Add(extra);
        }

        var aparte = linea.SelectMany(id => De(vecinosAparte, id)).Distinct().Where(id => !colocados.Contains(id)).OrderBy(Clave).ToList();

        return new ResultadoOrdenVisionado(ordenLinea.Select(id => new ParteLineaPrincipal(id, extras[id])).ToList(), aparte);
    }

    /// <summary>Vecinos por esos tipos, en las dos direcciones (AniList no siempre guarda las dos aristas).</summary>
    private static Dictionary<int, List<int>> Vecinos(List<RelacionAnime> aristas, HashSet<string> tipos)
    {
        var vecinos = new Dictionary<int, List<int>>();
        void Unir(int a, int b)
        {
            if (!vecinos.TryGetValue(a, out var l)) vecinos[a] = l = new List<int>();
            if (!l.Contains(b)) l.Add(b);
        }
        foreach (var a in aristas.Where(a => tipos.Contains(a.Tipo)))
        {
            Unir(a.AnimeId, a.RelacionadoId);
            Unir(a.RelacionadoId, a.AnimeId);
        }
        return vecinos;
    }

    private static IEnumerable<int> De(Dictionary<int, List<int>> vecinos, int id) =>
        vecinos.TryGetValue(id, out var l) ? l : Enumerable.Empty<int>();

    /// <summary>
    /// Lo que es precuela de algo va antes (A SEQUEL B = B PREQUEL A); entre las partes libres, la más temprana. Si los datos forman
    /// un ciclo, se rompe por la parte más temprana: nada se pierde ni se repite. ponytail: O(n²) con n ≤ 60.
    /// </summary>
    private static List<int> OrdenTopologico(HashSet<int> nodos, List<RelacionAnime> aristas, Func<int, (int, int)> clave)
    {
        var despues = nodos.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var a in aristas)
        {
            if (!nodos.Contains(a.AnimeId) || !nodos.Contains(a.RelacionadoId)) continue;
            if (a.Tipo.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase)) despues[a.AnimeId].Add(a.RelacionadoId);
            else if (a.Tipo.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase)) despues[a.RelacionadoId].Add(a.AnimeId);
        }

        var antesPendientes = nodos.ToDictionary(id => id, _ => 0);
        foreach (var siguientes in despues.Values)
            foreach (int s in siguientes) antesPendientes[s]++;

        var orden = new List<int>();
        var restantes = new HashSet<int>(nodos);
        while (restantes.Count > 0)
        {
            var libres = restantes.Where(id => antesPendientes[id] <= 0).ToList();
            int elegido = (libres.Count > 0 ? libres : restantes.ToList()).OrderBy(clave).First();
            restantes.Remove(elegido);
            orden.Add(elegido);
            foreach (int s in despues[elegido]) antesPendientes[s]--;
        }
        return orden;
    }
}
