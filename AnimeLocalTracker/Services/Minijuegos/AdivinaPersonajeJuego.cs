using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Minijuegos;

public enum TipoPistaPersonaje
{
    Rol,
    Genero,
    Edad,
    Apodo,
    Anime,
    Inicial
}

/// <summary>
/// Pista con datos crudos (sin traducir): el ViewModel la convierte en texto localizado.
/// <c>Valor</c> según el tipo: Rol → MAIN/SUPPORTING/BACKGROUND de AniList; Genero → Male/Female; Edad → número;
/// Apodo → el apodo; Anime → título del anime; Inicial → primera letra del nombre.
/// </summary>
public sealed record PistaPersonaje(TipoPistaPersonaje Tipo, string Valor);

public sealed record RondaAdivinaPersonaje(
    AnimeItem Anime,
    PersonajeAnime Respuesta,
    IReadOnlyList<PersonajeAnime> Opciones,
    int IndiceCorrecto,
    IReadOnlyList<PistaPersonaje> Pistas);

/// <summary>
/// Lógica pura de "Adivina el personaje" (sin UI ni acceso a datos, para poder probarla): elige un personaje por anime de
/// tu biblioteca, arma las pistas de menos a más reveladoras, busca distractores creíbles y decide cuánto se pixela la
/// imagen. Los personajes vienen de AniList (copia local), así que tras la primera partida se juega sin conexión.
/// </summary>
public static class AdivinaPersonajeJuego
{
    /// <summary>Cuántos personajes por anime se piden a AniList (ordenados: principales primero, luego los más queridos).</summary>
    public const int PersonajesPorAnime = 12;

    /// <summary>La respuesta de cada anime sale de sus N personajes más conocidos: preguntar por uno de fondo sería injusto.</summary>
    private const int TopParaElegir = 8;

    private const int LadoMinimo = 10;
    private const int LadoMaximo = 80;
    private const int LargoMaximoApodo = 30;
    private const int EdadMaxima = 999;

    private static readonly Regex ApodoValido = new(@"^[A-Za-z][A-Za-z ',.\-]*$", RegexOptions.Compiled);
    private static readonly Regex PrimerNumero = new(@"\d{1,3}", RegexOptions.Compiled);

    /// <summary>Personaje aprovechable: con nombre e imagen propia (AniList devuelve un "default.jpg" cuando no hay).</summary>
    public static bool EsUtilizable(PersonajeAnime p) =>
        p.PersonajeId > 0
        && !string.IsNullOrWhiteSpace(p.Nombre)
        && !string.IsNullOrWhiteSpace(p.ImagenUrl)
        && !p.ImagenUrl.Contains("/default.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puntos por acertar habiendo visto <paramref name="pistasReveladas"/> pistas (misma escala que "Adivina el anime").</summary>
    public static int Puntos(int pistasReveladas) => AdivinaAnimeJuego.Puntos(pistasReveladas);

    /// <summary>
    /// Ancho en píxeles al que se reduce la imagen antes de agrandarla: muy pocos píxeles = bloques enormes; con cada pista
    /// se ve con más detalle. Al responder se muestra entera (no pasa por aquí).
    /// </summary>
    public static int LadoPixelado(int pistasReveladas, int totalPistas)
    {
        if (totalPistas <= 1) return LadoMaximo;
        int reveladas = Math.Clamp(pistasReveladas, 1, totalPistas);
        double avance = (reveladas - 1) / (double)(totalPistas - 1);
        return (int)Math.Round(LadoMinimo + (LadoMaximo - LadoMinimo) * avance);
    }

    /// <summary>
    /// Un personaje por anime, animes en orden aleatorio. Se descartan los animes sin ningún personaje aprovechable y los
    /// personajes/nombres ya usados (una serie y su película comparten protagonista y saldría dos veces la misma pregunta).
    /// </summary>
    public static List<(AnimeItem Anime, PersonajeAnime Personaje)> ElegirRespuestas(
        IReadOnlyList<AnimeItem> animes, IReadOnlyDictionary<int, List<PersonajeAnime>> personajesPorAnime, int rondas, Random rng)
    {
        var resultado = new List<(AnimeItem, PersonajeAnime)>();
        var idsUsados = new HashSet<int>();
        var nombresUsados = new HashSet<string>();

        foreach (var anime in animes.OrderBy(_ => rng.Next()))
        {
            if (resultado.Count >= rondas) break;
            if (!personajesPorAnime.TryGetValue(anime.AniListId, out var personajes)) continue;

            var candidatos = Ordenar(personajes.Where(EsUtilizable))
                .Take(TopParaElegir)
                .Where(p => !idsUsados.Contains(p.PersonajeId) && !nombresUsados.Contains(AdivinaAnimeJuego.NormalizarTitulo(p.Nombre)))
                .ToList();
            if (candidatos.Count == 0) continue;

            var elegido = candidatos[rng.Next(candidatos.Count)];
            idsUsados.Add(elegido.PersonajeId);
            nombresUsados.Add(AdivinaAnimeJuego.NormalizarTitulo(elegido.Nombre));
            resultado.Add((anime, elegido));
        }
        return resultado;
    }

    /// <summary>Null si no hay suficientes distractores distintos o la respuesta tiene menos de 2 pistas.</summary>
    public static RondaAdivinaPersonaje? CrearRonda(
        AnimeItem anime, PersonajeAnime respuesta, IReadOnlyDictionary<int, List<PersonajeAnime>> personajesPorAnime,
        int opciones, Random rng)
    {
        var pistas = ConstruirPistas(respuesta, anime.Titulo);
        if (pistas.Count < 2) return null;

        var distractores = ElegirDistractores(respuesta, personajesPorAnime.Values.SelectMany(l => l), opciones - 1, rng);
        if (distractores.Count < opciones - 1) return null;

        var todas = distractores.Append(respuesta).OrderBy(_ => rng.Next()).ToList();
        int indice = todas.FindIndex(p => p.PersonajeId == respuesta.PersonajeId);
        return new RondaAdivinaPersonaje(anime, respuesta, todas, indice, pistas);
    }

    /// <summary>
    /// Dos distractores del mismo género que la respuesta (los que más confunden; si no se conoce, cualquiera) y el resto al
    /// azar. Nunca el mismo personaje ni otro con el mismo nombre (la misma persona en dos animes).
    /// </summary>
    public static List<PersonajeAnime> ElegirDistractores(PersonajeAnime respuesta, IEnumerable<PersonajeAnime> pool, int cantidad, Random rng)
    {
        var nombresUsados = new HashSet<string> { AdivinaAnimeJuego.NormalizarTitulo(respuesta.Nombre) };
        var idsUsados = new HashSet<int> { respuesta.PersonajeId };
        var candidatos = new List<PersonajeAnime>();

        foreach (var p in pool.Where(EsUtilizable).OrderBy(_ => rng.Next()))
        {
            if (!idsUsados.Add(p.PersonajeId)) continue;
            if (!nombresUsados.Add(AdivinaAnimeJuego.NormalizarTitulo(p.Nombre))) continue;
            candidatos.Add(p);
        }

        var parecidos = candidatos
            .Where(c => MismoGenero(respuesta, c))
            .Take(Math.Min(2, cantidad))
            .ToList();

        var resto = candidatos.Except(parecidos).Take(cantidad - parecidos.Count);
        return parecidos.Concat(resto).ToList();
    }

    private static bool MismoGenero(PersonajeAnime a, PersonajeAnime b) =>
        !string.IsNullOrWhiteSpace(a.Genero) && string.Equals(a.Genero, b.Genero, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pistas de menos a más reveladoras; se omiten las que el personaje no tiene.</summary>
    public static List<PistaPersonaje> ConstruirPistas(PersonajeAnime personaje, string tituloAnime)
    {
        var pistas = new List<PistaPersonaje>();

        string rol = personaje.Rol?.ToUpperInvariant() ?? string.Empty;
        if (rol is "MAIN" or "SUPPORTING" or "BACKGROUND")
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Rol, rol));

        string genero = personaje.Genero ?? string.Empty;
        if (genero.Equals("Male", StringComparison.OrdinalIgnoreCase) || genero.Equals("Female", StringComparison.OrdinalIgnoreCase))
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Genero, genero));

        int? edad = ExtraerEdad(personaje.Edad);
        if (edad != null)
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Edad, edad.Value.ToString(CultureInfo.InvariantCulture)));

        string? apodo = ElegirApodo(personaje);
        if (apodo != null)
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Apodo, apodo));

        if (!string.IsNullOrWhiteSpace(tituloAnime))
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Anime, tituloAnime));

        string inicial = Inicial(personaje.Nombre);
        if (inicial.Length > 0)
            pistas.Add(new PistaPersonaje(TipoPistaPersonaje.Inicial, inicial));

        return pistas;
    }

    /// <summary>
    /// Primer número del texto libre de AniList ("17-" → 17, "13-15 (Pre-timeskip); 19-20" → 13). Null si no hay o es absurdo.
    /// </summary>
    public static int? ExtraerEdad(string? edad)
    {
        if (string.IsNullOrWhiteSpace(edad)) return null;
        var m = PrimerNumero.Match(edad);
        if (!m.Success) return null;
        int valor = int.Parse(m.Value, CultureInfo.InvariantCulture);
        return valor is > 0 and <= EdadMaxima ? valor : null;
    }

    /// <summary>
    /// Primer apodo que no delate el nombre: solo letras latinas, corto y sin ninguna palabra del nombre ("Captain Levi"
    /// se descarta, "Straw Hat" vale).
    /// </summary>
    public static string? ElegirApodo(PersonajeAnime personaje)
    {
        if (string.IsNullOrWhiteSpace(personaje.Alternativos)) return null;

        var palabrasNombre = Palabras(personaje.Nombre);
        foreach (string alt in personaje.Alternativos.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (alt.Length < 3 || alt.Length > LargoMaximoApodo) continue;
            if (!ApodoValido.IsMatch(alt)) continue;
            if (Palabras(alt).Any(palabrasNombre.Contains)) continue;
            return alt;
        }
        return null;
    }

    private static HashSet<string> Palabras(string? texto) =>
        AdivinaAnimeJuego.NormalizarTitulo(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToHashSet();

    /// <summary>Primera letra del nombre en mayúscula ("" si no empieza por una letra).</summary>
    public static string Inicial(string? nombre)
    {
        string t = nombre?.Trim() ?? string.Empty;
        return t.Length > 0 && char.IsLetter(t[0]) ? char.ToUpperInvariant(t[0]).ToString() : string.Empty;
    }

    /// <summary>Principales primero, luego secundarios y el resto; a igual rol, los más queridos por la comunidad.</summary>
    private static IEnumerable<PersonajeAnime> Ordenar(IEnumerable<PersonajeAnime> personajes) =>
        personajes.OrderBy(p => RangoRol(p.Rol)).ThenByDescending(p => p.Favoritos);

    private static int RangoRol(string? rol) => rol?.ToUpperInvariant() switch
    {
        "MAIN" => 0,
        "SUPPORTING" => 1,
        _ => 2
    };
}
