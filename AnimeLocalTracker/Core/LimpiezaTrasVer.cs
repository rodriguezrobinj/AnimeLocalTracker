using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Decide qué episodios hay que borrar tras terminar de ver uno (modo de "Eliminar tras ver"). Lógica pura, sin disco ni
/// base de datos: el servicio le pasa los episodios ya vistos que siguen teniendo archivo y ejecuta lo que decide.
/// </summary>
public static class LimpiezaTrasVer
{
    /// <summary>
    /// Números de episodio que se borran, de menor a mayor. Un modo desconocido, vacío o "Apagado" no borra nada.
    /// <paramref name="vistosConArchivo"/> son los episodios vistos que aún tienen su video en la carpeta del anime;
    /// los no vistos nunca entran en la decisión.
    /// </summary>
    public static List<int> Decidir(string modo, int episodiosAConservar, int episodioTerminado, int totalEpisodios, IReadOnlyCollection<int> vistosConArchivo)
    {
        switch (ModoEliminarTrasVerValores.Normalizar(modo))
        {
            case ModoEliminarTrasVerValores.Automatico:
                return vistosConArchivo.Contains(episodioTerminado) ? [episodioTerminado] : [];

            case ModoEliminarTrasVerValores.ConsumoLigero:
                return vistosConArchivo.OrderByDescending(n => n).Skip(Math.Max(1, episodiosAConservar)).OrderBy(n => n).ToList();

            case ModoEliminarTrasVerValores.AlCompletarSerie:
                return totalEpisodios > 0 && episodioTerminado >= totalEpisodios ? vistosConArchivo.OrderBy(n => n).ToList() : [];

            default:
                return [];
        }
    }
}
