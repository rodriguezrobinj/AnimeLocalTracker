using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Lógica pura: qué mostrar arriba y qué mostrar abajo del video en un instante dado, cuando dos líneas de
/// subtítulo se solapan en el tiempo (dos personajes hablando a la vez). Como mucho 2 líneas visibles: si hay
/// 3 o más activas al mismo tiempo (raro) se conservan solo las 2 que empezaron más tarde.
/// </summary>
public static class SubtitulosSolapadosResolver
{
    /// <summary>
    /// Abajo = la línea que ya estaba sonando (misma posición que el subtítulo de siempre). Arriba = solo se llena
    /// cuando otra línea empieza mientras la de abajo sigue activa. Null en cualquiera de las dos = no hay nada que
    /// mostrar ahí.
    /// </summary>
    public static (string? Abajo, string? Arriba) Resolver(IReadOnlyList<SubtitleCue> cues, TimeSpan posicion)
    {
        if (cues == null || cues.Count == 0) return (null, null);

        var activos = cues
            .Where(c => c.Inicio <= posicion && posicion < c.Fin)
            .OrderBy(c => c.Inicio)
            .ToList();

        if (activos.Count == 0) return (null, null);
        if (activos.Count == 1) return (activos[0].Texto, null);

        // 2+: se descartan las más antiguas y se quedan las 2 que empezaron más tarde.
        var ultimasDos = activos.Skip(activos.Count - 2).ToList();
        return (ultimasDos[0].Texto, ultimasDos[1].Texto);
    }
}
