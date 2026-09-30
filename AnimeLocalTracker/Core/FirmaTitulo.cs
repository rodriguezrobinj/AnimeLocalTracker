using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Identidad de un título de anime separando el NOMBRE de la serie de su TEMPORADA/PARTE:
/// "Mushoku Tensei III: Isekai…" y "Mushoku Tensei II: Isekai…" se parecen al 98 % letra a letra,
/// pero son temporadas distintas. Compara nombres (sin los marcadores de temporada) y exige que
/// temporada, parte y "Final Season" coincidan. Lo usan Nyaa (títulos de releases) y AnimeAv1
/// (páginas del sitio) para no descargar la temporada equivocada.
/// </summary>
public static partial class FirmaTitulo
{
    /// <summary>Una lectura posible de un título: nombre normalizado + temporada/parte.</summary>
    /// <param name="Explicita">True si la temporada venía escrita ("2nd Season", "S2", "III"); false si
    /// es la temporada 1 por omisión o sale de un número suelto al final ("Iruma-kun 2").</param>
    /// <param name="Pelicula">El título dice que es una película ("Movie", "Película 14", "Gekijouban", "劇場版").</param>
    /// <param name="NumeroPelicula">Número de la película dentro de la franquicia ("Película 14"), si lo lleva.</param>
    public readonly record struct Interpretacion(string Base, int Temporada, int Parte, bool Final, bool Explicita, bool Pelicula = false, int? NumeroPelicula = null)
    {
        /// <summary>
        /// Misma temporada, parte, "Final Season" y tipo (película o no), comparando nuestra lectura con la
        /// de un candidato (<paramref name="suya"/>). Se tolera que SOLO el candidato diga "Película 14" y
        /// nosotros nada (AniList: "Dragon Ball Z: Kami to Kami"; el sitio: "Dragon Ball Z Película 14:
        /// Battle of Gods") si el nombre es distintivo (4+ palabras). Al revés no: nuestro alternativo "One
        /// Piece Film 15" no es el pack "One Piece (01-900)", ni "Iruma-kun Movie" la serie "Iruma-kun".
        /// </summary>
        public bool MismaEntrega(Interpretacion suya)
        {
            if (Temporada != suya.Temporada || Parte != suya.Parte || Final != suya.Final) return false;
            if (Pelicula == suya.Pelicula) return NumeroPelicula == null || suya.NumeroPelicula == null || NumeroPelicula == suya.NumeroPelicula;
            return !Pelicula && suya.NumeroPelicula != null && suya.Base.Split(' ').Length >= 4;
        }
    }

    /// <summary>Resultado de comparar dos conjuntos de títulos.</summary>
    /// <param name="MismaTemporada">Mejor parecido de nombres entre lecturas con la MISMA temporada/parte (0..1).</param>
    /// <param name="SinImportarTemporada">Mejor parecido de nombres sin mirar la temporada (0..1).</param>
    public readonly record struct Evaluacion(double MismaTemporada, double SinImportarTemporada)
    {
        /// <summary>Mismo nombre de serie pero otra temporada/parte (ej. "Iruma-kun 2" frente a "Iruma-kun S4").</summary>
        public bool ConflictoTemporada(double umbral = 0.8) => SinImportarTemporada >= umbral && MismaTemporada < umbral;
    }

    /// <summary>Minúsculas, sin tildes, solo letras/dígitos separados por un espacio.</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var sinTildes = new StringBuilder(texto.Length);
        foreach (char c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sinTildes.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sinTildes.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Lecturas posibles del título. Normalmente una; dos cuando termina en un número suelto
    /// ("Iruma-kun 2" = temporada 2 de "Iruma-kun", pero "Kaiju No. 8" = serie que acaba en 8), y
    /// una extra con solo la parte principal (antes de ':') cuando la temporada está escrita
    /// ("Mushoku Tensei III: Isekai…" también es "Mushoku Tensei" temporada 3, como lo nombran
    /// muchos releases). Sin temporada escrita NO se recorta el subtítulo: "Kimetsu no Yaiba:
    /// Yuukaku-hen" no debe confundirse con la temporada 1 "Kimetsu no Yaiba".
    /// </summary>
    public static List<Interpretacion> Interpretar(string? titulo)
    {
        var resultado = new List<Interpretacion>();
        string norm = Normalizar(titulo);
        if (norm.Length == 0) return resultado;

        var tokens = norm.Split(' ').ToList();
        int? temporada = null;
        int? parte = null;
        bool final = false;
        bool pelicula = false;
        int? numeroPelicula = null;
        var quitar = new bool[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            string? siguiente = i + 1 < tokens.Count ? tokens[i + 1] : null;

            // "2nd season", "2da temporada", "3rd cour"
            if (siguiente != null && EsPalabraTemporada(siguiente) && OrdinalRegex().Match(t) is { Success: true } ord)
            {
                temporada ??= int.Parse(ord.Groups[1].Value, CultureInfo.InvariantCulture);
                quitar[i] = quitar[i + 1] = true; i++; continue;
            }
            // "season 2", "temporada 2", "season ii"
            if (EsPalabraTemporada(t) && siguiente != null && NumeroORomano(siguiente) is int n1)
            {
                temporada ??= n1;
                quitar[i] = quitar[i + 1] = true; i++; continue;
            }
            // "final season"
            if (t == "final" && siguiente != null && EsPalabraTemporada(siguiente))
            {
                final = true;
                quitar[i] = quitar[i + 1] = true; i++; continue;
            }
            // "part 2", "parte ii", "cour 2"
            if (t is "part" or "parte" or "cour" && siguiente != null && NumeroORomano(siguiente) is int p)
            {
                parte ??= p;
                quitar[i] = quitar[i + 1] = true; i++; continue;
            }
            // "2nd part"/"2nd cour"
            if (siguiente is "part" or "cour" && OrdinalRegex().Match(t) is { Success: true } ordP)
            {
                parte ??= int.Parse(ordP.Groups[1].Value, CultureInfo.InvariantCulture);
                quitar[i] = quitar[i + 1] = true; i++; continue;
            }
            // "Película 14", "Movie 2", "Gekijouban", "劇場版": el número de película de la franquicia no es parte del nombre
            if (EsPalabraPelicula(t))
            {
                pelicula = true;
                quitar[i] = true;
                if (siguiente != null && int.TryParse(siguiente, NumberStyles.None, CultureInfo.InvariantCulture, out int np) && np is > 0 and < 100)
                {
                    numeroPelicula ??= np;
                    quitar[i + 1] = true; i++;
                }
                continue;
            }
            // "s2", "s02", "s02e05"
            if (TemporadaCortaRegex().Match(t) is { Success: true } sc)
            {
                temporada ??= int.Parse(sc.Groups[1].Value, CultureInfo.InvariantCulture);
                quitar[i] = true; continue;
            }
            // "第3期"
            if (TemporadaJaponesaRegex().Match(t) is { Success: true } sj)
            {
                temporada ??= int.Parse(sj.Groups[1].Value, CultureInfo.InvariantCulture);
                quitar[i] = true; continue;
            }
            // Numeral romano suelto ("Overlord IV", "Mushoku Tensei III") — no en la primera palabra.
            if (i > 0 && Romano(t) is int r)
            {
                temporada ??= r;
                quitar[i] = true;
            }
        }

        // "The Final Season", "The 2nd Season": el artículo del marcador tampoco es parte del nombre.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] == "the" && quitar[i + 1] && !quitar[i]) quitar[i] = true;
        }

        bool explicita = temporada.HasValue;
        var baseTokens = tokens.Where((_, i) => !quitar[i]).ToList();
        if (baseTokens.Count == 0) baseTokens = tokens;
        string baseCompleta = string.Join(' ', baseTokens);

        resultado.Add(new Interpretacion(baseCompleta, temporada ?? 1, parte ?? 1, final, explicita, pelicula, numeroPelicula));

        // Número suelto al final: dos lecturas ("…kun 2" temporada 1 / "…kun" temporada 2).
        if (!explicita && baseTokens.Count >= 2 && int.TryParse(baseTokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int suelto)
            && suelto is >= 2 and <= 20 && baseTokens[^1].Length <= 2)
        {
            resultado.Add(new Interpretacion(string.Join(' ', baseTokens.Take(baseTokens.Count - 1)), suelto, parte ?? 1, final, false, pelicula, numeroPelicula));
        }

        // Temporada escrita: también vale solo el nombre principal (antes de ':' / ' - ').
        if (explicita && titulo != null)
        {
            var principal = SeparadorSubtituloRegex().Split(titulo)[0];
            // Solo si de verdad había subtítulo (si no, la recursión no terminaría).
            var lecturaPrincipal = principal.Trim().Length < titulo.Trim().Length ? Interpretar(principal).FirstOrDefault() : default;
            if (lecturaPrincipal.Base is { Length: >= 4 } basePrincipal && basePrincipal != baseCompleta)
            {
                resultado.Add(new Interpretacion(basePrincipal, temporada!.Value, parte ?? 1, final, true, pelicula, numeroPelicula));
            }
        }

        return resultado;
    }

    /// <summary>Compara nuestros títulos con los de un candidato (release, página del sitio…).</summary>
    public static Evaluacion Evaluar(IEnumerable<string> nuestros, IEnumerable<string> suyos)
    {
        var lecturasNuestras = nuestros.SelectMany(Interpretar).Distinct().ToList();
        var lecturasSuyas = suyos.SelectMany(Interpretar).Distinct().ToList();

        double misma = 0, cualquiera = 0;
        foreach (var a in lecturasNuestras)
        {
            foreach (var b in lecturasSuyas)
            {
                double sim = SimilitudBases(a.Base, b.Base);
                if (sim > cualquiera) cualquiera = sim;
                if (sim > misma && a.MismaEntrega(b)) misma = sim;
            }
        }
        return new Evaluacion(misma, cualquiera);
    }

    /// <summary>
    /// Parecido entre dos nombres ya normalizados. Los números forman parte de la identidad
    /// ("kaiju no 8" frente a "kaiju no 9"): si los números no son los mismos, no se parecen.
    /// </summary>
    public static double SimilitudBases(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;
        // Sin espacios ("re zero" / "rezero", "dogul wang" / "dogulwang").
        if (a.Replace(" ", "") == b.Replace(" ", "")) return 1.0;

        var numerosA = a.Split(' ').Where(EsNumero).OrderBy(x => x);
        var numerosB = b.Split(' ').Where(EsNumero).OrderBy(x => x);
        if (!numerosA.SequenceEqual(numerosB)) return 0;

        return TituloSimilaridad.Similitud(a, b);
    }

    /// <summary>True si el texto usa un alfabeto en el que buscan los sitios (latino o japonés).</summary>
    public static bool EsAlfabetoBuscable(string? titulo, bool incluirJapones)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return false;
        int letras = 0, latinas = 0, japonesas = 0;
        foreach (char c in titulo)
        {
            if (!char.IsLetter(c)) continue;
            letras++;
            if (c <= 'ɏ') latinas++;
            else if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' or >= 'ｦ' and <= 'ﾟ') japonesas++;
        }
        if (letras == 0) return false;
        if (latinas * 2 >= letras) return true;
        // Solo kanji sin kana suele ser chino (海贼王): se trata como japonés solo con kana.
        return incluirJapones && japonesas * 2 >= letras && titulo.Any(c => c is >= '぀' and <= 'ヿ');
    }

    private static bool EsNumero(string t) => t.All(char.IsDigit);

    private static bool EsPalabraPelicula(string t) => t is "movie" or "pelicula" or "film" or "gekijouban" or "劇場版";

    private static bool EsPalabraTemporada(string t) => t is "season" or "temporada" or "saison" or "staffel" or "stagione";

    private static int? NumeroORomano(string t)
        => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and < 100 ? n : Romano(t);

    /// <summary>ii..x (sin "v", "x" ni "i" sueltos: son demasiado ambiguos, "Gundam X", "Persona V").</summary>
    private static int? Romano(string t) => t switch
    {
        "ii" => 2, "iii" => 3, "iv" => 4, "vi" => 6, "vii" => 7, "viii" => 8, "ix" => 9,
        _ => null
    };

    [GeneratedRegex(@"^(\d{1,2})(?:st|nd|rd|th|da|do|ra|ro|ta|to|a|o|e|er|eme)$")]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"^s(\d{1,2})(?:e\d{1,4})?$")]
    private static partial Regex TemporadaCortaRegex();

    [GeneratedRegex(@"^第(\d{1,2})期$")]
    private static partial Regex TemporadaJaponesaRegex();

    [GeneratedRegex(@":|\s[-–]\s|～|~")]
    private static partial Regex SeparadorSubtituloRegex();
}
