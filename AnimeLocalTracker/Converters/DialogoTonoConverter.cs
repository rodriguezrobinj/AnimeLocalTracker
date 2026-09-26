using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AnimeLocalTracker.Converters;

/// <summary>Tono semántico de un diálogo; cada uno tiene su color en la paleta de la app.</summary>
public enum TonoDialogo
{
    /// <summary>Informativo / neutro (azul de acento).</summary>
    Info,
    Exito,
    Advertencia,
    Peligro
}

/// <summary>
/// Los avisos y confirmaciones de la app piden su color como hex suelto ("#4CAF50", "#F44336", "#3F51B5"…),
/// heredado de la paleta de Material. Este conversor lo traduce, por tono (matiz), a los colores del sistema
/// de diseño de la app para que todos los diálogos combinen con la UI sin tocar a cada llamador:
/// rojos → peligro, naranjas/ámbar → advertencia, verdes → éxito y el resto (azules, índigo, morado) → acento.
///
/// El parámetro elige qué color se devuelve:
///  • <c>Icono</c>   — el tono claro, para el icono sobre fondo oscuro.
///  • <c>Fondo</c>   — el mismo tono al ~16 % de opacidad, para la insignia circular del icono.
///  • <c>Relleno</c> — una variante oscura para el botón de confirmar (texto blanco con contraste AA).
/// </summary>
public class DialogoTonoConverter : IValueConverter
{
    /// <summary>Clasifica un color "#RRGGBB" en su tono. Valores no válidos o grises → <see cref="TonoDialogo.Info"/>.</summary>
    public static TonoDialogo Clasificar(string? hex)
    {
        if (!TryLeerRgb(hex, out double r, out double g, out double b)) return TonoDialogo.Info;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 0.08) return TonoDialogo.Info; // gris/blanco/negro: sin matiz

        double matiz;
        if (max == r) matiz = 60 * (((g - b) / delta) % 6);
        else if (max == g) matiz = 60 * (((b - r) / delta) + 2);
        else matiz = 60 * (((r - g) / delta) + 4);
        if (matiz < 0) matiz += 360;

        return matiz switch
        {
            < 18 or >= 335 => TonoDialogo.Peligro,      // rojos y rosas fuertes
            < 55 => TonoDialogo.Advertencia,            // naranja, ámbar, amarillo
            >= 75 and < 175 => TonoDialogo.Exito,       // verdes y verde azulado
            _ => TonoDialogo.Info                       // azules, índigo, morado
        };
    }

    public static Color ColorIcono(TonoDialogo tono) => tono switch
    {
        TonoDialogo.Peligro => Color.FromRgb(0xF8, 0x71, 0x71),      // Brush.Danger
        TonoDialogo.Advertencia => Color.FromRgb(0xF5, 0x9E, 0x0B),  // Brush.Warning
        TonoDialogo.Exito => Color.FromRgb(0x34, 0xD3, 0x99),        // Brush.Success
        _ => Color.FromRgb(0x60, 0xA5, 0xFA)                         // Brush.Accent
    };

    /// <summary>Variante oscura del tono: blanco encima da ≥ 4.5:1 (AA), igual que AppPrimaryButton.</summary>
    public static Color ColorRelleno(TonoDialogo tono) => tono switch
    {
        TonoDialogo.Peligro => Color.FromRgb(0xDC, 0x26, 0x26),
        TonoDialogo.Advertencia => Color.FromRgb(0xB4, 0x53, 0x09),
        TonoDialogo.Exito => Color.FromRgb(0x04, 0x78, 0x57),
        _ => Color.FromRgb(0x25, 0x63, 0xEB)
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tono = Clasificar(value as string);
        Color color = (parameter as string) switch
        {
            "Relleno" => ColorRelleno(tono),
            "Fondo" => Color.FromArgb(0x2B, ColorIcono(tono).R, ColorIcono(tono).G, ColorIcono(tono).B),
            _ => ColorIcono(tono)
        };

        var pincel = new SolidColorBrush(color);
        pincel.Freeze();
        return pincel;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static bool TryLeerRgb(string? hex, out double r, out double g, out double b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // #AARRGGBB → ignora el alfa
        if (s.Length != 6) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int valor)) return false;

        r = ((valor >> 16) & 0xFF) / 255.0;
        g = ((valor >> 8) & 0xFF) / 255.0;
        b = (valor & 0xFF) / 255.0;
        return true;
    }
}
