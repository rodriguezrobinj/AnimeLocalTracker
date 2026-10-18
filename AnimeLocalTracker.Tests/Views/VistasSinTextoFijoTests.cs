using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Guarda contra el texto visible escrito a mano en las vistas: un atributo de texto con un literal (en vez de <c>{loc:T Clave}</c>)
/// se queda en español aunque el usuario cambie el idioma a inglés (pasó con la tarjeta del resumen anual y los intervalos de
/// sincronización de Configuración). Procedimiento para añadir texto: skill <c>localizacion</c>.
/// </summary>
public class VistasSinTextoFijoTests
{
    private static readonly Regex AtributoDeTexto = new(
        @"\b(?:Text|Content|ToolTip|Header|Title|Watermark|PlaceholderText|AutomationProperties\.Name|HintAssist\.Hint)\s*=\s*""([^""{][^""]*)""",
        RegexOptions.Compiled);

    /// <summary>Texto que NO se traduce a propósito: nombres propios y tecnologías, nombres de tecla y cada idioma en su propio idioma.</summary>
    private static readonly HashSet<string> Permitidos = new(StringComparer.Ordinal)
    {
        "MIT", ".NET 8", "WPF", "libmpv", "Anime4K", "SQLite-net", "MaterialDesign", "Velopack", "GIF",
        "ANIMELOCALTRACKER", "AnimeTracker",
        "Enter", "Escape",
        "Español", "English",
        "·  SHA-256",
    };

    private static string RaizDelRepositorio()
    {
        var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
        while (carpeta != null && !File.Exists(Path.Combine(carpeta.FullName, "AnimeLocalTracker.sln"))) carpeta = carpeta.Parent;
        return carpeta?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (AnimeLocalTracker.sln).");
    }

    [Fact]
    public void LosIconosDeLasVistas_ExistenEnLaBiblioteca()
    {
        // Un nombre de icono que la versión instalada de MaterialDesignThemes no trae no falla al compilar: falla al abrir la vista.
        // De una versión a otra cambian (la compilación ci1462 renombró Rewind10 y FastForward10).
        string proyecto = Path.Combine(RaizDelRepositorio(), "AnimeLocalTracker");
        var icono = new Regex(@"<materialDesign:PackIcon\b[^>]*?\bKind=""([A-Za-z0-9]+)""", RegexOptions.Compiled);
        var desconocidos = new List<string>();

        foreach (string archivo in Directory.EnumerateFiles(proyecto, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in icono.Matches(File.ReadAllText(archivo)))
            {
                if (!Enum.TryParse<MaterialDesignThemes.Wpf.PackIconKind>(m.Groups[1].Value, out _))
                    desconocidos.Add($"{Path.GetFileName(archivo)}: {m.Groups[1].Value}");
            }
        }

        desconocidos.Should().BeEmpty("cada icono de las vistas tiene que existir en la versión de MaterialDesignThemes del proyecto");
    }

    [Fact]
    public void LaVistaDelMotorNuevo_NoTieneCapasDeSubtitulosPropias()
    {
        // Con libmpv los subtítulos van dentro del video: una capa de la app encima los mostraría dos veces.
        string xaml = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "AnimeLocalTracker", "Views", "ReproductorView.xaml"));

        Assert.DoesNotContain("SubtitleTextView", xaml);
        Assert.DoesNotContain("SubtitulosAssImagen", xaml);
    }

    [Fact]
    public void NingunaVistaTieneTextoVisibleFijo()
    {
        string proyecto = Path.Combine(RaizDelRepositorio(), "AnimeLocalTracker");
        var fijos = new List<string>();

        foreach (string archivo in Directory.EnumerateFiles(proyecto, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string[] lineas = File.ReadAllLines(archivo);
            for (int i = 0; i < lineas.Length; i++)
            {
                foreach (Match m in AtributoDeTexto.Matches(lineas[i]))
                {
                    string texto = m.Groups[1].Value.Trim();
                    // Sin al menos tres letras seguidas no es una palabra (símbolos, números, iconos).
                    if (!Regex.IsMatch(texto, @"\p{L}{3}") || Permitidos.Contains(texto) || texto.StartsWith("pack:", StringComparison.Ordinal)) continue;
                    fijos.Add($"{Path.GetRelativePath(proyecto, archivo)}:{i + 1}: {m.Value.Trim()}");
                }
            }
        }

        fijos.Should().BeEmpty("el texto visible va con {loc:T Clave} (skill localizacion); si un literal es un nombre propio, añádelo a Permitidos");
    }
}
