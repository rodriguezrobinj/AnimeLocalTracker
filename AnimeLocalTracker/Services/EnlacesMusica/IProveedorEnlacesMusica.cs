using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.EnlacesMusica;

/// <summary>
/// Un enlace externo que la ficha ofrece junto a los openings/endings (p. ej. la página del anime en AniPlaylist).
/// <see cref="ClaveDescripcion"/> es una clave de <see cref="LocalizationService"/> (el texto que explica qué encontrarás
/// al abrirlo); el nombre es una marca y no se traduce.
/// </summary>
public sealed record EnlaceMusica(string ProveedorId, string Nombre, string Url, string ClaveDescripcion);

/// <summary>
/// Fuente de enlaces externos de música para un anime. Es el punto de extensión de la sección de música de la ficha:
/// para añadir otra fuente (Apple Music, un catálogo propio…) basta una clase nueva registrada en DI, sin tocar la ficha.
/// Debe ser rápida y sin red (solo arma la URL); un proveedor que falle o devuelva null se ignora sin afectar a los demás.
/// </summary>
public interface IProveedorEnlacesMusica
{
    /// <summary>Identificador estable (minúsculas, sin espacios).</summary>
    string Id { get; }

    /// <summary>Enlace para este anime, o null si no se puede armar uno fiable.</summary>
    EnlaceMusica? ObtenerEnlace(AnimeItem anime);
}
