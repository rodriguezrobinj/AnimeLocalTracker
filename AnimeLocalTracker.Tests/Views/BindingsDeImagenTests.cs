using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// PERF-03: un binding a <c>Image.Source</c>/<c>ImageBrush.ImageSource</c> cuyo valor sea null obliga a WPF a intentar
/// convertirlo (ImageSourceConverter lanza NotSupportedException y WPF la captura). Medido: 104 excepciones en 64 s
/// solo con abrir la ficha de un anime sin miniaturas. <c>TargetNullValue={x:Null}</c> evita el intento.
/// </summary>
public class BindingsDeImagenTests
{
    private static readonly Regex BindingDeImagen = new(
        @"(?<tag><Image\b[^>]*?\sSource|<ImageBrush\b[^>]*?\sImageSource)\s*=\s*""(?<binding>\{Binding(?:[^{}]|\{[^{}]*\})*\})""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static string CarpetaVistas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidato = Path.Combine(dir.FullName, "AnimeLocalTracker", "Views");
            if (Directory.Exists(candidato)) return candidato;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No se encontró la carpeta Views subiendo desde " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TodosLosBindingsDeImagen_DeberianDefinirTargetNullValue()
    {
        var infractores = Directory.GetFiles(CarpetaVistas(), "*.xaml")
            .SelectMany(archivo => BindingDeImagen.Matches(File.ReadAllText(archivo))
                .Where(m => !m.Groups["binding"].Value.Contains("TargetNullValue"))
                .Select(m => $"{Path.GetFileName(archivo)}: {m.Groups["binding"].Value}"))
            .ToList();

        infractores.Should().BeEmpty("cada binding de imagen necesita TargetNullValue={x:Null} para no lanzar una excepción por cada valor null");
    }

    [Fact]
    public void ElPatron_DeberiaDetectarUnBindingSinTargetNullValue()
    {
        // Comprueba el propio detector, para que un cambio de regex no lo deje ciego
        const string xaml = "<Image Source=\"{Binding Ruta}\" Stretch=\"Fill\"/> <ImageBrush ImageSource=\"{Binding Otro, TargetNullValue={x:Null}}\"/>";

        BindingDeImagen.Matches(xaml).Should().HaveCount(2);
    }
}
