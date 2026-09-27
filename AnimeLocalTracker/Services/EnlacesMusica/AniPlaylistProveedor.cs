using System;
using System.Linq;
using System.Text;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.EnlacesMusica;

/// <summary>
/// Enlace a la página del anime en <c>aniplaylist.com</c>, que reúne sus openings y endings con enlaces oficiales y
/// verificados a Spotify, Apple Music y Deezer. AniPlaylist no tiene API pública: su URL es simplemente el nombre del anime
/// con guiones (<c>aniplaylist.com/Attack-on-Titan</c>) y la web busca por ese texto, así que solo se arma la dirección y
/// se abre en el navegador del usuario; la app no descarga ni copia nada de su sitio.
/// </summary>
public sealed class AniPlaylistProveedor : IProveedorEnlacesMusica
{
    public const string UrlBase = "https://aniplaylist.com/";
    public const string IdProveedor = "aniplaylist";

    /// <summary>Largo máximo del texto de búsqueda en la URL (los títulos larguísimos no ganan precisión).</summary>
    private const int LargoMaximoBusqueda = 80;

    /// <summary>Un título alternativo más corto que esto parece una sigla ("DBGT", "SnK") y buscaría mal.</summary>
    private const int LargoMinimoTituloAlternativo = 6;

    public string Id => IdProveedor;

    public EnlaceMusica? ObtenerEnlace(AnimeItem anime)
    {
        string? busqueda = ConstruirBusqueda(ElegirTitulo(anime));
        return busqueda == null ? null : new EnlaceMusica(IdProveedor, "AniPlaylist", UrlBase + busqueda, "Det_MusicaAniPlaylistTip");
    }

    /// <summary>
    /// El título en inglés si lo hay (AniPlaylist lo muestra en sus tarjetas y da mejores resultados: "Attack on Titan" encuentra
    /// Red Swan y Guren no Yumiya arriba, "Shingeki no Kyojin" mezcla canciones sueltas). AniList lo guarda como primer nombre
    /// alternativo; se acepta el primer alternativo en alfabeto latino, con largo de título y distinto del principal.
    /// </summary>
    internal static string? ElegirTitulo(AnimeItem anime)
    {
        string principal = anime.Titulo?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(anime.NombresAlternativos))
        {
            foreach (string alt in anime.NombresAlternativos.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (alt.Length < LargoMinimoTituloAlternativo || !EsAlfabetoLatino(alt)) continue;
                if (string.Equals(alt, principal, StringComparison.OrdinalIgnoreCase)) continue;
                return alt;
            }
        }

        return principal.Length > 0 ? principal : null;
    }

    /// <summary>
    /// "Re:Zero kara Hajimeru Isekai Seikatsu" → "Re-Zero-kara-Hajimeru-Isekai-Seikatsu". Se quitan apóstrofos, los demás signos
    /// separan palabras y las letras acentuadas se codifican para la URL. Null si no queda nada.
    /// </summary>
    internal static string? ConstruirBusqueda(string? titulo)
    {
        if (string.IsNullOrWhiteSpace(titulo)) return null;

        var sb = new StringBuilder(titulo.Length);
        foreach (char c in titulo)
        {
            if (c == '\'' || c == '’' || c == '`') continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var palabras = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length == 0) return null;

        string unido = string.Join('-', palabras);
        if (unido.Length > LargoMaximoBusqueda)
        {
            int corte = unido.LastIndexOf('-', LargoMaximoBusqueda);
            unido = unido[..(corte > 0 ? corte : LargoMaximoBusqueda)];
        }

        return Uri.EscapeDataString(unido);
    }

    /// <summary>True si todas las letras son latinas (incluye las acentuadas); los números y signos no cuentan.</summary>
    internal static bool EsAlfabetoLatino(string texto) => texto.Where(char.IsLetter).All(c => c <= 'ɏ');
}
