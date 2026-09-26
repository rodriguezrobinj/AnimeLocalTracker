using System;
using System.Text.RegularExpressions;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Apariencia de los subtítulos del reproductor (Configuración → Reproducción → Estilo de los subtítulos).
/// Los valores por defecto reproducen el aspecto que tenían antes de que fuera configurable:
/// Trebuchet MS 44 en negrita, blanco con contorno negro.
/// Es un objeto de datos puro (sin tipos de WPF) para poder serializarlo en settings.json y probarlo.
/// </summary>
public class EstiloSubtitulos
{
    public const string FuentePorDefecto = "Trebuchet MS";
    public const double TamanoMinimo = 24;
    public const double TamanoMaximo = 80;
    public const double GrosorContornoMaximo = 8;
    public const double GrosorBordeMaximo = 6;

    private static readonly Regex ColorHex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>Tipografías que se ofrecen (todas vienen con Windows, para no depender de fuentes instaladas).</summary>
    public static readonly string[] FuentesDisponibles =
    {
        "Trebuchet MS", "Arial", "Segoe UI", "Verdana", "Tahoma", "Calibri",
        "Georgia", "Times New Roman", "Comic Sans MS", "Consolas", "Impact"
    };

    /// <summary>Colores que se ofrecen: clave de localización (Cfg_SubColor_*) y valor #RRGGBB.</summary>
    public static readonly (string Clave, string Hex)[] Paleta =
    {
        ("Cfg_SubColor_Blanco", "#FFFFFF"),
        ("Cfg_SubColor_Amarillo", "#FFE14D"),
        ("Cfg_SubColor_Cian", "#4DE1FF"),
        ("Cfg_SubColor_Verde", "#7CFC7C"),
        ("Cfg_SubColor_Rosa", "#FF8FD0"),
        ("Cfg_SubColor_Naranja", "#FFA94D"),
        ("Cfg_SubColor_Rojo", "#FF5A5A"),
        ("Cfg_SubColor_Gris", "#C8C8C8"),
        ("Cfg_SubColor_Negro", "#000000")
    };

    public string Fuente { get; set; } = FuentePorDefecto;

    /// <summary>Tamaño de la letra en píxeles independientes del dispositivo (24 a 80).</summary>
    public double Tamano { get; set; } = 44;

    public bool Negrita { get; set; } = true;
    public bool Cursiva { get; set; }
    public bool Subrayado { get; set; }

    public string ColorTexto { get; set; } = "#FFFFFF";

    /// <summary>Color del contorno que rodea cada letra.</summary>
    public string ColorContorno { get; set; } = "#000000";

    /// <summary>Grosor del contorno de las letras (0 = sin contorno).</summary>
    public double GrosorContorno { get; set; } = 3;

    /// <summary>Opacidad (0-100) del fondo negro de la caja que rodea el subtítulo (0 = sin caja).</summary>
    public int OpacidadFondo { get; set; }

    /// <summary>Grosor del borde de la caja del subtítulo (0 = sin borde).</summary>
    public double GrosorBorde { get; set; }

    public string ColorBorde { get; set; } = "#FFFFFF";

    /// <summary>
    /// Corrige valores fuera de rango o mal formados (settings.json editado a mano) y devuelve una
    /// copia lista para usar; nunca modifica la instancia original.
    /// </summary>
    public EstiloSubtitulos Normalizar() => new()
    {
        Fuente = string.IsNullOrWhiteSpace(Fuente) ? FuentePorDefecto : Fuente.Trim(),
        Tamano = double.IsNaN(Tamano) ? 44 : Math.Clamp(Tamano, TamanoMinimo, TamanoMaximo),
        Negrita = Negrita,
        Cursiva = Cursiva,
        Subrayado = Subrayado,
        ColorTexto = ColorValido(ColorTexto, "#FFFFFF"),
        ColorContorno = ColorValido(ColorContorno, "#000000"),
        GrosorContorno = double.IsNaN(GrosorContorno) ? 3 : Math.Clamp(GrosorContorno, 0, GrosorContornoMaximo),
        OpacidadFondo = Math.Clamp(OpacidadFondo, 0, 100),
        GrosorBorde = double.IsNaN(GrosorBorde) ? 0 : Math.Clamp(GrosorBorde, 0, GrosorBordeMaximo),
        ColorBorde = ColorValido(ColorBorde, "#FFFFFF")
    };

    private static string ColorValido(string? valor, string porDefecto)
        => valor != null && ColorHex.IsMatch(valor) ? valor.ToUpperInvariant() : porDefecto;
}
