namespace AnimeLocalTracker.Models;

/// <summary>Estado de confianza de un archivo de plugin frente a lo que el usuario aprobó.</summary>
public enum EstadoConfianzaPlugin
{
    /// <summary>El usuario todavía no ha confiado en este archivo.</summary>
    SinConfiar,

    /// <summary>Confió en este archivo y su contenido sigue siendo el mismo (la huella coincide).</summary>
    Confiable,

    /// <summary>Había confiado, pero el archivo cambió desde entonces (la huella ya no coincide).</summary>
    Modificado
}

/// <summary>Un plugin de la carpeta Plugins con su huella y su estado de confianza.</summary>
/// <param name="Nombre">Nombre de archivo, ej. "MiProveedor.dll".</param>
/// <param name="Tipo">Extensión en minúsculas: ".dll" (C#) o ".py" (Python).</param>
/// <param name="Sha256">Huella SHA-256 actual del archivo, en hex mayúsculas ("" si no se pudo leer).</param>
/// <param name="Estado">Si el usuario confía en este contenido exacto.</param>
public sealed record PluginInfo(string Nombre, string Tipo, string Sha256, EstadoConfianzaPlugin Estado);
