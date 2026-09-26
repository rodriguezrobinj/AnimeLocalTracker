using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Minijuegos;

public enum TipoPasoOpEd
{
    MasAudio,
    Artista,
    Estreno,
    Generos,
    Cancion
}

/// <summary>Ayuda extra de una ronda. <c>MasAudio</c> alarga el clip a <c>Segundos</c>; el resto revela un dato.</summary>
public sealed record PasoOpEd(TipoPasoOpEd Tipo, string Valor = "", string? Extra = null, int Segundos = 0);

/// <summary>Tema listo para sonar: archivo mp3 local + lo que se sepa de él (offline solo se conoce el tipo y el slug).</summary>
public sealed record TemaParaJugar(string Tipo, string Slug, string Titulo, string Artistas, string RutaLocal);

public sealed record RondaOpEd(
    AnimeItem Respuesta,
    TemaParaJugar Tema,
    IReadOnlyList<AnimeItem> Opciones,
    int IndiceCorrecto,
    IReadOnlyList<PasoOpEd> Pasos,
    double PosicionInicio);

/// <summary>
/// Lógica pura de "Adivina el OP/ED" (sin audio, red ni disco, para poder probarla): qué tema sonará, qué ayudas
/// hay y en qué orden, y las 4 opciones. Reutiliza distractores y puntuación de <see cref="AdivinaAnimeJuego"/>.
/// </summary>
public static class AdivinaOpEdJuego
{
    public const int SegundosIniciales = 6;

    /// <summary>Al responder suena un trozo más largo para disfrutar la canción.</summary>
    public const int SegundosRevelacion = 30;

    private static readonly Regex SlugTema = new(@"^(OP|ED)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pasos en orden de menos a más revelador; se omiten los datos que se desconocen (p. ej. artista sin conexión).</summary>
    public static List<PasoOpEd> ConstruirPasos(AnimeItem anime, TemaParaJugar tema)
    {
        var pasos = new List<PasoOpEd> { new(TipoPasoOpEd.MasAudio, Segundos: 12) };

        if (!string.IsNullOrWhiteSpace(tema.Artistas))
            pasos.Add(new PasoOpEd(TipoPasoOpEd.Artista, tema.Artistas));

        if (anime.AnioLanzamiento > 0)
            pasos.Add(new PasoOpEd(TipoPasoOpEd.Estreno, anime.AnioLanzamiento.ToString(),
                string.IsNullOrWhiteSpace(anime.Temporada) ? null : anime.Temporada));

        pasos.Add(new PasoOpEd(TipoPasoOpEd.MasAudio, Segundos: 20));

        var generos = anime.GenerosLista;
        if (generos.Length > 0)
            pasos.Add(new PasoOpEd(TipoPasoOpEd.Generos, string.Join(", ", generos)));

        if (!string.IsNullOrWhiteSpace(tema.Titulo))
            pasos.Add(new PasoOpEd(TipoPasoOpEd.Cancion, tema.Titulo));

        return pasos;
    }

    /// <summary>Duración del clip con los primeros <paramref name="pasosReveladosExtra"/> pasos ya pedidos.</summary>
    public static int SegundosDeClip(IReadOnlyList<PasoOpEd> pasos, int pasosReveladosExtra) =>
        pasos.Take(Math.Max(0, pasosReveladosExtra))
            .Where(p => p.Tipo == TipoPasoOpEd.MasAudio)
            .Select(p => p.Segundos)
            .DefaultIfEmpty(SegundosIniciales)
            .Max();

    /// <summary>
    /// Elige un tema de los que devuelve AnimeThemes: sin spoilers y con audio, una sola vez cada OP/ED (aunque tenga
    /// varias versiones). Si alguno ya está descargado se elige entre esos, para no ir llenando el disco de temas nuevos.
    /// Null si no queda ninguno jugable.
    /// </summary>
    public static AnimeThemeInfo? ElegirTema(IReadOnlyList<AnimeThemeInfo> temas, Func<AnimeThemeInfo, bool> yaDescargado, Random rng)
    {
        var jugables = temas
            .Where(t => !t.EsSpoiler && !string.IsNullOrWhiteSpace(t.AudioUrlOgg))
            .GroupBy(t => t.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(yaDescargado) ?? g.First())
            .ToList();

        if (jugables.Count == 0) return null;

        var descargados = jugables.Where(yaDescargado).ToList();
        var candidatos = descargados.Count > 0 ? descargados : jugables;
        return candidatos[rng.Next(candidatos.Count)];
    }

    /// <summary>Elige al azar entre los temas ya descargados, un solo por OP/ED. Null si no hay.</summary>
    public static TemaLocalDisponible? ElegirTemaLocal(IReadOnlyList<TemaLocalDisponible> locales, Random rng)
    {
        var unicos = locales
            .Where(t => !string.IsNullOrWhiteSpace(t.RutaArchivo))
            .GroupBy(t => t.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return unicos.Count == 0 ? null : unicos[rng.Next(unicos.Count)];
    }

    /// <summary>Null si no se pueden reunir 3 distractores distintos de otra franquicia.</summary>
    public static RondaOpEd? CrearRonda(AnimeItem respuesta, TemaParaJugar tema, IReadOnlyList<AnimeItem> biblioteca, Random rng)
    {
        var distractores = AdivinaAnimeJuego.ElegirDistractores(respuesta, biblioteca, AdivinaAnimeJuego.OpcionesPorRonda - 1, rng);
        if (distractores.Count < AdivinaAnimeJuego.OpcionesPorRonda - 1) return null;

        var opciones = distractores.Append(respuesta).OrderBy(_ => rng.Next()).ToList();
        int indice = opciones.FindIndex(a => a.AniListId == respuesta.AniListId);
        return new RondaOpEd(respuesta, tema, opciones, indice, ConstruirPasos(respuesta, tema), PosicionAleatoria(rng));
    }

    /// <summary>
    /// Dónde empieza el clip, como fracción de la duración del tema (5 %–40 %): el arranque suele ser silencio o
    /// una nota suelta, y así cada ronda suena distinto sin llegar al final.
    /// </summary>
    public static double PosicionAleatoria(Random rng) => 0.05 + rng.NextDouble() * 0.35;

    /// <summary>"OP2" → ("OP", 2); "ED1-TV" → ("ED", 1); sin número (o sin formato) → el tipo dado y null.</summary>
    public static (string Tipo, int? Numero) ParsearSlug(string slug, string tipoPorDefecto)
    {
        var m = SlugTema.Match(slug ?? string.Empty);
        if (!m.Success) return (tipoPorDefecto.ToUpperInvariant(), null);
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value));
    }
}
