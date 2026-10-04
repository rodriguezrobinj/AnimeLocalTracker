using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.Core;

/// <summary>Textos de presentación comunes a toda la app.</summary>
public static class Formato
{
    private const double Kilo = 1024, Mega = Kilo * 1024, Giga = Mega * 1024;

    /// <summary>
    /// Tamaño de un archivo como lo ve el usuario, con una sola regla en toda la app: GB con un decimal; MB con un decimal por
    /// debajo de 10 (un opening de 2,4 MB no debe leerse "2 MB") y sin él por encima (un episodio son cientos de MB); KB y B
    /// enteros. El separador decimal es el del idioma de la app. Cadena vacía si el tamaño no se conoce (negativo).
    /// </summary>
    public static string Tamano(long bytes)
    {
        if (bytes < 0) return string.Empty;

        var cultura = LocalizationService.Cultura;
        // Los umbrales van medio paso antes del cambio de unidad para que el redondeo no escriba "1024 MB" ni "10,0 MB".
        if (bytes >= 1023.5 * Mega) return (bytes / Giga).ToString("0.0", cultura) + " GB";
        if (bytes >= 9.95 * Mega) return (bytes / Mega).ToString("0", cultura) + " MB";
        if (bytes >= 1023.5 * Kilo) return (bytes / Mega).ToString("0.0", cultura) + " MB";
        if (bytes >= Kilo) return (bytes / Kilo).ToString("0", cultura) + " KB";
        return bytes + " B";
    }
}
