using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Minijuegos;

/// <summary>Identificadores estables de los minijuegos (se guardan en la base de datos: no renombrar).</summary>
public static class JuegosMinijuego
{
    public const string AdivinaAnime = "adivina_anime";
    public const string AdivinaOpEd = "adivina_oped";
}

/// <summary>Récords y totales de un juego, calculados a partir de sus partidas terminadas.</summary>
public sealed record RecordsMinijuego(
    int MejorPuntuacion,
    DateTime? FechaMejorUtc,
    int Partidas,
    int Aciertos,
    int Rondas,
    int PartidasPerfectas,
    int MejorRacha)
{
    public static RecordsMinijuego Vacio { get; } = new(0, null, 0, 0, 0, 0, 0);

    public bool HayPartidas => Partidas > 0;

    /// <summary>Porcentaje de aciertos sobre todas las rondas jugadas (0 si no hay ninguna).</summary>
    public int PrecisionPorcentaje => Rondas > 0 ? (int)Math.Round(Aciertos * 100.0 / Rondas, MidpointRounding.AwayFromZero) : 0;

    /// <summary>Una partida perfecta: todas las rondas acertadas, con un mínimo para que 1 ronda suelta no cuente.</summary>
    public static bool EsPerfecta(PartidaMinijuego p) => p.Rondas >= MinimoRondasPerfecta && p.Aciertos >= p.Rondas;

    public const int MinimoRondasPerfecta = 5;

    public static RecordsMinijuego Calcular(IEnumerable<PartidaMinijuego> partidas)
    {
        var lista = partidas.ToList();
        if (lista.Count == 0) return Vacio;

        // Si dos partidas empatan en la mejor puntuación, el récord es la primera que la logró.
        var mejor = lista.OrderByDescending(p => p.Puntos).ThenBy(p => p.FechaUtc).First();

        return new RecordsMinijuego(
            MejorPuntuacion: mejor.Puntos,
            FechaMejorUtc: mejor.FechaUtc,
            Partidas: lista.Count,
            Aciertos: lista.Sum(p => p.Aciertos),
            Rondas: lista.Sum(p => p.Rondas),
            PartidasPerfectas: lista.Count(EsPerfecta),
            MejorRacha: lista.Max(p => p.RachaMaxima));
    }
}
