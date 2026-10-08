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
        "MIT", ".NET 8", "WPF", "Flyleaf", "SQLite-net", "MaterialDesign", "Velopack",
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
