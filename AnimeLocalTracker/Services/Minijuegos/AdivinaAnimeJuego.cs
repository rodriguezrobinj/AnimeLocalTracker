using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Minijuegos;

public enum TipoPista
{
    Generos,
    Estreno,
    Episodios,
    Produccion,
    Sinopsis
}

/// <summary>
/// Pista con datos crudos (sin traducir): el ViewModel la convierte en texto localizado, así que un
/// cambio de idioma en mitad de la ronda no obliga a regenerar nada.
/// <c>Valor</c>/<c>Extra</c>/<c>Extra2</c> según el tipo: Generos → "Action, Fantasy"; Estreno → año + temporada
/// de AniList (WINTER…); Episodios → cantidad; Produccion → estudio + formato + fuente; Sinopsis → texto ya censurado.
/// </summary>
public sealed record PistaAnime(TipoPista Tipo, string Valor, string? Extra = null, string? Extra2 = null);

public sealed record RondaAdivinaAnime(
    AnimeItem Respuesta,
    IReadOnlyList<AnimeItem> Opciones,
    int IndiceCorrecto,
    IReadOnlyList<PistaAnime> Pistas);

/// <summary>
/// Lógica pura de "Adivina el anime" (sin UI ni acceso a datos, para poder probarla): elige respuestas,
/// arma las pistas de menos a más reveladoras, elige distractores creíbles y puntúa. Todo sale de la
/// biblioteca local, así que el juego funciona sin conexión.
/// </summary>
public static class AdivinaAnimeJuego
{
    public const int OpcionesPorRonda = 4;
    public const int RondasPorPartida = 10;
    public const int MinimoAnimes = OpcionesPorRonda;

    private const int PuntosMaximos = 100;
    private const int PenalizacionPorPista = 20;
    private const int PuntosMinimosAcierto = 20;
    private const double DesenfoqueMaximo = 30;
    private const int LargoMaximoSinopsis = 300;
    public const string MarcaCensura = "[???]";

    /// <summary>Puntos por acertar habiendo visto <paramref name="pistasReveladas"/> pistas (la primera es gratis).</summary>
    public static int Puntos(int pistasReveladas)
    {
        int extra = Math.Max(0, pistasReveladas - 1);
        return Math.Max(PuntosMinimosAcierto, PuntosMaximos - PenalizacionPorPista * extra);
    }

    /// <summary>Radio del desenfoque de la portada: más borrosa con pocas pistas, se aclara al pedir más.</summary>
    public static double RadioDesenfoque(int pistasReveladas, int totalPistas)
    {
        if (totalPistas <= 0) return 0;
        int reveladas = Math.Clamp(pistasReveladas, 1, totalPistas);
        return DesenfoqueMaximo * (totalPistas - reveladas + 1) / totalPistas;
    }

    public static bool EsUtilizable(AnimeItem anime) =>
        anime.AniListId > 0 && !string.IsNullOrWhiteSpace(anime.Titulo);

    /// <summary>Hacen falta al menos <see cref="MinimoAnimes"/> animes distintos para armar 4 opciones.</summary>
    public static bool PuedeJugar(IReadOnlyCollection<AnimeItem> biblioteca) =>
        biblioteca.Count(EsUtilizable) >= MinimoAnimes;

    /// <summary>
    /// Respuestas de la partida, en orden aleatorio. Descarta los animes con datos insuficientes
    /// (menos de 2 pistas sin contar las de AniList): serían rondas injugables.
    /// </summary>
    public static List<AnimeItem> ElegirRespuestas(IReadOnlyList<AnimeItem> biblioteca, int rondas, Random rng)
    {
        return biblioteca
            .Where(EsUtilizable)
            .Where(a => ConstruirPistas(a, null).Count >= 2)
            .OrderBy(_ => rng.Next())
            .Take(rondas)
            .ToList();
    }

    /// <summary>Null si no se pueden reunir suficientes distractores distintos (biblioteca pequeña o muy repetitiva).</summary>
    public static RondaAdivinaAnime? CrearRonda(AnimeItem respuesta, IReadOnlyList<AnimeItem> biblioteca, DatosExtraAnime? extra, Random rng)
    {
        var pistas = ConstruirPistas(respuesta, extra);
        if (pistas.Count == 0) return null;

        var distractores = ElegirDistractores(respuesta, biblioteca, OpcionesPorRonda - 1, rng);
        if (distractores.Count < OpcionesPorRonda - 1) return null;

        var opciones = distractores.Append(respuesta).OrderBy(_ => rng.Next()).ToList();
        int indice = opciones.FindIndex(a => a.AniListId == respuesta.AniListId);
        return new RondaAdivinaAnime(respuesta, opciones, indice, pistas);
    }

    /// <summary>Pistas de menos a más reveladoras; se omiten las que el anime no tiene.</summary>
    public static List<PistaAnime> ConstruirPistas(AnimeItem anime, DatosExtraAnime? extra)
    {
        var pistas = new List<PistaAnime>();

        var generos = anime.GenerosLista;
        if (generos.Length > 0)
            pistas.Add(new PistaAnime(TipoPista.Generos, string.Join(", ", generos)));

        if (anime.AnioLanzamiento > 0)
            pistas.Add(new PistaAnime(TipoPista.Estreno, anime.AnioLanzamiento.ToString(),
                string.IsNullOrWhiteSpace(anime.Temporada) ? null : anime.Temporada));

        if (anime.TotalEpisodios > 0)
            pistas.Add(new PistaAnime(TipoPista.Episodios, anime.TotalEpisodios.ToString()));

        if (extra != null && (!string.IsNullOrWhiteSpace(extra.Estudio) || !string.IsNullOrWhiteSpace(extra.Fuente)
                              || !string.IsNullOrWhiteSpace(extra.Formato)))
            pistas.Add(new PistaAnime(TipoPista.Produccion, extra.Estudio ?? string.Empty, extra.Formato, extra.Fuente));

        string sinopsis = SinopsisCensurada(anime);
        if (sinopsis.Length >= 20)
            pistas.Add(new PistaAnime(TipoPista.Sinopsis, sinopsis));

        return pistas;
    }

    /// <summary>
    /// Dos distractores de géneros parecidos (los que más dudas generan) y el resto al azar. Se excluyen los
    /// de la misma franquicia ("One Piece" vs "One Piece Film: Red") para que la respuesta no sea ambigua.
    /// </summary>
    public static List<AnimeItem> ElegirDistractores(AnimeItem respuesta, IReadOnlyList<AnimeItem> biblioteca, int cantidad, Random rng)
    {
        var titulosUsados = new HashSet<string> { NormalizarTitulo(respuesta.Titulo) };
        var candidatos = new List<AnimeItem>();
        foreach (var a in biblioteca.Where(EsUtilizable).OrderBy(_ => rng.Next()))
        {
            if (a.AniListId == respuesta.AniListId) continue;
            if (MismaFranquicia(respuesta.Titulo, a.Titulo)) continue;
            if (!titulosUsados.Add(NormalizarTitulo(a.Titulo))) continue;
            candidatos.Add(a);
        }

        var generosRespuesta = new HashSet<string>(respuesta.GenerosLista, StringComparer.OrdinalIgnoreCase);
        var parecidos = candidatos
            .OrderByDescending(c => c.GenerosLista.Count(generosRespuesta.Contains)) // estable: mantiene el orden aleatorio en empates
            .Take(Math.Min(2, cantidad))
            .ToList();

        var resto = candidatos.Except(parecidos).Take(cantidad - parecidos.Count);
        return parecidos.Concat(resto).ToList();
    }

    /// <summary>True si los dos títulos parecen de la misma serie (uno empieza por el otro o comparten un prefijo largo).</summary>
    public static bool MismaFranquicia(string a, string b)
    {
        string na = NormalizarTitulo(a), nb = NormalizarTitulo(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na.StartsWith(nb, StringComparison.Ordinal) || nb.StartsWith(na, StringComparison.Ordinal)) return true;

        int prefijo = 0;
        int minimo = Math.Min(na.Length, nb.Length);
        while (prefijo < minimo && na[prefijo] == nb[prefijo]) prefijo++;
        return prefijo >= 10 && prefijo >= minimo * 0.5;
    }

    /// <summary>Minúsculas, sin tildes ni signos y con espacios simples.</summary>
    public static string NormalizarTitulo(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return string.Empty;
        var sb = new StringBuilder(titulo.Length);
        foreach (char c in titulo.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Sinopsis sin etiquetas HTML, con el título (y sus alias) tapado y recortada.</summary>
    public static string SinopsisCensurada(AnimeItem anime)
    {
        string texto = anime.SinopsisLimpia;
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;

        texto = Regex.Replace(texto, @"\s+", " ").Trim();
        texto = CensurarTitulos(texto, TitulosDe(anime));
        return Recortar(texto, LargoMaximoSinopsis);
    }

    public static IEnumerable<string> TitulosDe(AnimeItem anime)
    {
        yield return anime.Titulo;
        if (string.IsNullOrWhiteSpace(anime.NombresAlternativos)) yield break;
        foreach (string alt in anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return alt;
    }

    private static readonly Regex SufijoTemporada = new(
        @"\s*[:\-]?\s*(\d+(st|nd|rd|th)\s+season|season\s+\d+|final\s+season|part\s+\d+|cour\s+\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tapa cada título/alias y sus variantes cortas ("Re:Zero" de "Re:Zero: Starting Life…", sin "2nd Season"),
    /// de más largo a más corto para no dejar restos a la vista. Ignora fragmentos de menos de 4 letras.
    /// </summary>
    public static string CensurarTitulos(string texto, IEnumerable<string> titulos)
    {
        var variantes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string titulo in titulos)
        {
            string t = titulo.Trim();
            if (t.Length == 0) continue;
            variantes.Add(t);

            string sinTemporada = SufijoTemporada.Replace(t, string.Empty).Trim();
            variantes.Add(sinTemporada);

            foreach (string separador in new[] { ":", " - " })
            {
                int i = sinTemporada.IndexOf(separador, StringComparison.Ordinal);
                if (i > 0) variantes.Add(sinTemporada[..i].Trim());
            }
        }

        foreach (string v in variantes.Where(v => v.Length >= 4).OrderByDescending(v => v.Length))
        {
            string patron = @"(?<![\p{L}\p{N}])" + Regex.Escape(v) + @"(?![\p{L}\p{N}])";
            texto = Regex.Replace(texto, patron, MarcaCensura, RegexOptions.IgnoreCase);
        }
        return texto;
    }

    private static string Recortar(string texto, int maximo)
    {
        if (texto.Length <= maximo) return texto;
        int corte = texto.LastIndexOf(' ', maximo);
        if (corte < maximo / 2) corte = maximo;
        return texto[..corte].TrimEnd(' ', ',', '.', ';', ':') + "…";
    }
}
