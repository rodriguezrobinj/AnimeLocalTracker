using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using AnimeLocalTracker.Converters;
using AnimeLocalTracker.Services;
using FluentAssertions;
using MaterialDesignThemes.Wpf;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Rediseño de los diálogos de confirmación y del selector de torrents: los colores sueltos que piden
/// los avisos se traducen a los tonos de la paleta, y el selector muestra chips con grupo/resolución.
/// </summary>
public class DialogosDiseñoTests
{
    // Todos los colores que hoy pasan los llamadores de MostrarDialogoAsync/MostrarToast
    [Theory]
    [InlineData("#EF4444", TonoDialogo.Peligro)]
    [InlineData("#F44336", TonoDialogo.Peligro)]
    [InlineData("#E53935", TonoDialogo.Peligro)]
    [InlineData("#FF5252", TonoDialogo.Peligro)]
    [InlineData("#4CAF50", TonoDialogo.Exito)]
    [InlineData("#10B981", TonoDialogo.Exito)]
    [InlineData("#F59E0B", TonoDialogo.Advertencia)]
    [InlineData("#FF9800", TonoDialogo.Advertencia)]
    [InlineData("#FFA000", TonoDialogo.Advertencia)]
    [InlineData("#FFC107", TonoDialogo.Advertencia)]
    [InlineData("#3F51B5", TonoDialogo.Info)]
    [InlineData("#2196F3", TonoDialogo.Info)]
    [InlineData("#60A5FA", TonoDialogo.Info)]
    [InlineData("#9C27B0", TonoDialogo.Info)]
    [InlineData("#A78BFA", TonoDialogo.Info)]
    public void Clasificar_ColoresDeLaApp_DeberiaDarElTonoSemanticoEsperado(string hex, TonoDialogo esperado)
    {
        DialogoTonoConverter.Clasificar(hex).Should().Be(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rojo")]
    [InlineData("#12")]
    [InlineData("#808080")] // gris: sin matiz
    public void Clasificar_ValorInvalidoOSinMatiz_DeberiaSerInformativo(string? hex)
    {
        DialogoTonoConverter.Clasificar(hex).Should().Be(TonoDialogo.Info);
    }

    [Fact]
    public void Clasificar_ConAlfa_DeberiaIgnorarloYUsarElColor()
    {
        DialogoTonoConverter.Clasificar("#FFEF4444").Should().Be(TonoDialogo.Peligro);
    }

    [Theory]
    [InlineData(TonoDialogo.Info)]
    [InlineData(TonoDialogo.Exito)]
    [InlineData(TonoDialogo.Advertencia)]
    [InlineData(TonoDialogo.Peligro)]
    public void ColorRelleno_DeberiaDarContrasteAAConTextoBlanco(TonoDialogo tono)
    {
        double ratio = 1.05 / (Luminancia(DialogoTonoConverter.ColorRelleno(tono)) + 0.05);

        ratio.Should().BeGreaterThanOrEqualTo(4.5, "el botón de confirmar lleva texto blanco (regla AA del proyecto)");
    }

    [Theory]
    [InlineData("Icono")]
    [InlineData("Fondo")]
    [InlineData("Relleno")]
    [InlineData(null)]
    public void Convert_DeberiaDevolverUnPincelCongelado(string? parametro)
    {
        var resultado = new DialogoTonoConverter().Convert("#EF4444", typeof(Brush), parametro!, System.Globalization.CultureInfo.InvariantCulture);

        resultado.Should().BeOfType<SolidColorBrush>().Which.IsFrozen.Should().BeTrue();
    }

    [Fact]
    public void Convert_Fondo_DeberiaSerElTonoTranslucido()
    {
        var pincel = (SolidColorBrush)new DialogoTonoConverter().Convert("#4CAF50", typeof(Brush), "Fondo", System.Globalization.CultureInfo.InvariantCulture);

        pincel.Color.A.Should().BeInRange(0x20, 0x40);
        pincel.Color.G.Should().Be(0xD3, "verde de éxito de la paleta (#34D399)");
    }

    [Theory]
    [InlineData("[Erai-raws] Tensei Shitara Slime Datta Ken 4th Season - 22 [720p CR WEB-DL AVC AAC][MultiSub][673683DB]", "Erai-raws", "720p")]
    [InlineData("[AnoZu] That Time I Got Reincarnated as a Slime S04E22 1080p CR WEB-DL", "AnoZu", "1080p")]
    [InlineData("That Time I Got Reincarnated as a Slime S04E22 Where the Soul Resides 1080p CR WEB-DL", null, "1080p")]
    [InlineData("Anime - 05 (WEB 1080P x265)", null, "1080p")]
    [InlineData("[SubsPlease] Anime - 05", "SubsPlease", null)]
    [InlineData("Anime 21600p fake", null, null)]
    public void CandidatoTorrentItem_DeberiaExtraerGrupoYResolucionDelTitulo(string titulo, string? grupo, string? resolucion)
    {
        var item = new CandidatoTorrentItem(new CandidatoTorrent(titulo, "https://x/t.torrent", "hash", 10, 500_000_000));

        item.Grupo.Should().Be(grupo);
        item.TieneGrupo.Should().Be(grupo != null);
        item.Resolucion.Should().Be(resolucion);
        item.TieneResolucion.Should().Be(resolucion != null);
    }

    [Fact]
    public void CandidatoTorrentItem_ConTituloVacio_NoDeberiaFallar()
    {
        var item = new CandidatoTorrentItem(new CandidatoTorrent("", "u", "h", 0, 0));

        item.Grupo.Should().BeNull();
        item.Resolucion.Should().BeNull();
    }

    // Las plantillas de fila se parsean al mostrar el primer ítem: un icono inexistente solo explotaba en ejecución.
    [Fact]
    public void Iconos_DeMainWindow_ExistenEnMaterialDesign()
    {
        var xaml = File.ReadAllText(BuscarXaml("Views", "MainWindow.xaml"));

        var iconos = Regex.Matches(xaml, "(?:Kind|Property=\"Kind\" Value)=\"(?<k>[A-Za-z0-9]+)\"")
            .Select(m => m.Groups["k"].Value)
            .Distinct()
            .ToList();

        iconos.Should().NotBeEmpty();
        iconos.Where(k => !Enum.TryParse<PackIconKind>(k, out _)).Should().BeEmpty("cada Kind del XAML debe existir en PackIconKind");
    }

    private static double Luminancia(Color c)
    {
        static double Canal(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Canal(c.R) + 0.7152 * Canal(c.G) + 0.0722 * Canal(c.B);
    }

    private static string BuscarXaml(string carpeta, string archivo)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidato = Path.Combine(dir.FullName, "AnimeLocalTracker", carpeta, archivo);
            if (File.Exists(candidato)) return candidato;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"No se encontró {carpeta}\\{archivo} subiendo desde {AppContext.BaseDirectory}");
    }
}
