using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using MaterialDesignThemes.Wpf;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Las plantillas de fila (DataTemplate) se parsean al mostrar el primer ítem, no al construir la vista:
/// un icono inexistente en ellas solo explotaba en ejecución ("XamlParseException al abrir Descargas").
/// Este test valida todos los iconos citados en el XAML contra PackIconKind.
/// </summary>
public class DescargasViewIconosTests
{
    private static string BuscarXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidato = Path.Combine(dir.FullName, "AnimeLocalTracker", "Views", "DescargasView.xaml");
            if (File.Exists(candidato)) return candidato;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("No se encontró Views\\DescargasView.xaml subiendo desde " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TodosLosIconosDeLaVista_ExistenEnMaterialDesign()
    {
        var xaml = File.ReadAllText(BuscarXaml());

        var iconos = Regex.Matches(xaml, "(?:Kind|Property=\"Kind\" Value)=\"(?<k>[A-Za-z0-9]+)\"")
            .Select(m => m.Groups["k"].Value)
            .Distinct()
            .ToList();

        iconos.Should().NotBeEmpty();
        var invalidos = iconos.Where(k => !Enum.TryParse<PackIconKind>(k, out _)).ToList();
        invalidos.Should().BeEmpty("cada Kind del XAML debe existir en PackIconKind");
    }
}
