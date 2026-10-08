using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Views;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// La tarjeta del resumen anual (la que se exporta como imagen para compartir) tenía todos sus rótulos fijos en español:
/// con la app en inglés salía a medias. Se pinta la vista real y se miran los textos que contiene.
/// </summary>
[Collection("WpfSmoke")]
public class TarjetaWrappedIdiomaTests
{
    private readonly WpfHostFixture _host;

    public TarjetaWrappedIdiomaTests(WpfHostFixture host) => _host = host;

    private static readonly string[] Rotulos =
    [
        "RESUMEN OFICIAL DE VISIONADO", "tiempo acumulado frente a la pantalla", "capítulos reproducidos y completados",
        "días seguidos viendo anime", "tu categoría temática predilecta", "TOP GÉNEROS MÁS CONSUMIDOS",
        "🥈 #2 PLATA", "👑 #1 ORO CAMPEÓN", "🥉 #3 BRONCE", "✨ GENERADO CON ANIMELOCALTRACKER • COLECCIÓN LOCAL Y PRIVADA",
    ];

    private static IEnumerable<string> TextosDe(DependencyObject raiz)
    {
        if (raiz is TextBlock bloque && !string.IsNullOrEmpty(bloque.Text)) yield return bloque.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
            foreach (string texto in TextosDe(VisualTreeHelper.GetChild(raiz, i))) yield return texto;
    }

    private List<string> PintarTarjeta(string idioma)
    {
        var textos = new List<string>();
        _host.Ejecutar(() =>
        {
            try
            {
                LocalizationService.Instance.Idioma = idioma;
                var vista = new AnimeWrappedCardView { DataContext = new WrappedCardData() };
                vista.Measure(new Size(1080, 1350));
                vista.Arrange(new Rect(0, 0, 1080, 1350));
                vista.UpdateLayout();
                textos.AddRange(TextosDe(vista));
            }
            finally
            {
                LocalizationService.Instance.Idioma = "es";
            }
        });
        return textos;
    }

    [Fact]
    public void ConLaAppEnIngles_LosRotulosFijosSalenEnIngles()
    {
        var textos = PintarTarjeta("en");

        textos.Should().Contain([
            "OFFICIAL VIEWING SUMMARY", "total time spent in front of the screen", "episodes played and completed",
            "days in a row watching anime", "your favorite thematic category", "MOST WATCHED GENRES",
            "🥈 #2 SILVER", "👑 #1 GOLD CHAMPION", "🥉 #3 BRONZE",
            "✨ GENERATED WITH ANIMELOCALTRACKER • LOCAL AND PRIVATE COLLECTION",
        ]);
        textos.Should().NotContain(Rotulos, "ningún rótulo debe quedarse en español");
    }

    [Fact]
    public void ConLaAppEnEspanol_LosRotulosSiguenIgualQueAntes()
    {
        PintarTarjeta("es").Should().Contain(Rotulos);
    }
}
