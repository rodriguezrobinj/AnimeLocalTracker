using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// ARQ-01 (paso 1 del split de DetalleViewModel): lógica pura de filtrado y
/// ordenamiento de episodios, extraída del god-object para ser testeable sin UI.
/// </summary>
public static class EpisodiosOrganizador
{
    // Claves fijas del filtro de episodios. NO son el texto que se ve: la ficha muestra la traducción de cada una. Antes se
    // comparaba contra el texto del desplegable y, con la app en inglés ("Watched", "Downloaded"…), ningún filtro coincidía.
    public const string FiltroTodos = "Todos";
    public const string FiltroDescargados = "Descargados";
    public const string FiltroVistos = "Vistos";
    public const string FiltroNoVistos = "No Vistos";
    public const string FiltroFavoritos = "Favoritos";

    /// <summary>
    /// Aplica el filtro de episodios (una de las claves <c>Filtro*</c>; cualquier otra cosa equivale a "Todos")
    /// y el orden (ascendente/descendente por número).
    /// </summary>
    public static List<EpisodioItem> FiltrarYOrdenar(
        IEnumerable<EpisodioItem> episodios, string filtro, bool ordenAscendente)
    {
        var query = episodios.AsEnumerable();

        switch (filtro)
        {
            case FiltroDescargados:
                query = query.Where(e => e.Descargado);
                break;
            case FiltroVistos:
                query = query.Where(e => e.Visto);
                break;
            case FiltroNoVistos:
                query = query.Where(e => !e.Visto);
                break;
            case FiltroFavoritos:
                query = query.Where(e => e.Favorito);
                break;
        }

        query = ordenAscendente
            ? query.OrderBy(e => e.NumeroEpisodio)
            : query.OrderByDescending(e => e.NumeroEpisodio);

        return query.ToList();
    }

    /// <summary>
    /// Un archivo local puede ir hasta esto por encima del total oficial y aun así ganarse una fila: cubre un
    /// preestreno/filtración de 1-2 días antes de la emisión oficial, o un par de especiales numerados justo después del
    /// último episodio regular. Un archivo mal nombrado con un número absurdo (p. ej. "Episodio 3000.mp4" en una carpeta
    /// con 12 episodios reales) queda muy por encima de este margen y no infla la lista.
    /// </summary>
    internal const int MargenEpisodiosAdelantados = 5;

    /// <summary>
    /// Hasta qué número de episodio dibuja la lista de la Ficha. Manda el total oficial de AniList cuando se conoce, con
    /// un pequeño margen (<see cref="MargenEpisodiosAdelantados"/>) para no ocultar un episodio real que ya tienes
    /// aunque AniList todavía no lo cuente como emitido — un archivo muchísimo más alto que ese margen (el caso del
    /// nombre mal puesto o un número absurdo) no cuenta. Solo sin dato oficial (anime recién añadido, sin sincronizar
    /// aún) se confía del todo en lo que hay en disco. El progreso visto localmente siempre puede extender la lista (si
    /// ya viste más episodios de los que AniList tiene registrados, esas filas no deben desaparecer).
    /// </summary>
    public static int CalcularMaxEpisodio(int totalEpisodiosOficial, IReadOnlyCollection<EpisodioItem> encontrados, int episodiosVistos)
    {
        int max;
        if (totalEpisodiosOficial > 0)
        {
            // Se descartan los archivos por encima del margen ANTES de calcular el máximo local: con un preestreno real (13)
            // y un archivo absurdo (4000) a la vez, el 4000 no debe "colarse" solo por ir acompañado de un número plausible.
            int limite = totalEpisodiosOficial + MargenEpisodiosAdelantados;
            int maxLocalPlausible = encontrados.Where(e => e.NumeroEpisodio <= limite).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max();
            max = System.Math.Max(totalEpisodiosOficial, maxLocalPlausible);
        }
        else
        {
            max = encontrados.Count > 0 ? encontrados.Max(e => e.NumeroEpisodio) : 0;
        }

        return episodiosVistos > max ? episodiosVistos : max;
    }

    /// <summary>
    /// Huecos de la carpeta local: episodios sin archivo, por debajo del más alto que sí está en disco, que además NO se han
    /// visto. Un episodio ya visto no es un hueco: lo normal es verlo y liberar su espacio (antes One Piece, con 1180 vistos y
    /// solo el último en disco, avisaba de "1179 episodios faltantes"). Los que aún no se han descargado por encima del más
    /// alto tampoco cuentan: es el estado normal de un anime en emisión.
    /// </summary>
    public static List<int> CalcularFaltantes(IReadOnlyCollection<EpisodioItem> episodios)
    {
        int maxDescargado = episodios.Where(e => e.Descargado).Select(e => e.NumeroEpisodio).DefaultIfEmpty(0).Max();

        return episodios
            .Where(e => !e.Descargado && !e.Visto && e.NumeroEpisodio < maxDescargado)
            .Select(e => e.NumeroEpisodio)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Episodios que "Descargar temporada" pone en cola: los ya emitidos (1..<paramref name="ultimoEmitido"/>) que no están en
    /// disco, no se están descargando y no se han visto. Los vistos no cuentan por la misma razón que en
    /// <see cref="CalcularFaltantes"/> (ver y liberar el archivo es lo normal; con "Eliminar tras ver" activo, volver a ofrecer
    /// lo recién borrado sería absurdo). Sin episodio emitido conocido (<paramref name="ultimoEmitido"/> ≤ 0) no hay nada.
    /// </summary>
    public static List<int> CalcularPendientesTemporada(IReadOnlyCollection<EpisodioItem> episodios, int ultimoEmitido)
    {
        if (ultimoEmitido <= 0) return [];

        return episodios
            .Where(e => e.NumeroEpisodio >= 1 && e.NumeroEpisodio <= ultimoEmitido && !e.Descargado && !e.IsDownloading && !e.Visto)
            .Select(e => e.NumeroEpisodio)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Espacio estimado para <paramref name="pendientes"/> episodios: tamaño medio de los archivos que ya hay en disco por el
    /// número de pendientes. Null si no hay ningún archivo de referencia (no se inventa una cifra).
    /// </summary>
    public static long? EstimarBytes(IReadOnlyCollection<long> tamanosDescargados, int pendientes)
    {
        if (pendientes <= 0 || tamanosDescargados.Count == 0) return null;
        return (long)(tamanosDescargados.Average() * pendientes);
    }
}
