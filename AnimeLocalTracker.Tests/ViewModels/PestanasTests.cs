using System.Linq;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

public class PestanasTests
{
    private static readonly string[] OrdenDeLaBarra =
    [
        "Galeria", "Historial", "Actualizaciones", "AgregarAnime", "Calendario", "Estadisticas", "Logros", "Descargas",
        "Configuracion", "AcercaDe"
    ];

    [Fact]
    public void Todas_EstanEnElOrdenDeLaBarraLateral()
        => Pestanas.Todas.Select(p => p.Clave).Should().Equal(OrdenDeLaBarra);

    [Fact]
    public void SoloConfiguracionYAcercaDeVanAbajo()
        => Pestanas.Todas.Where(p => p.Inferior).Select(p => p.Clave).Should().Equal("Configuracion", "AcercaDe");

    [Fact]
    public void CadaPestanaTieneSuTextoTraducido()
    {
        foreach (var pestana in Pestanas.Todas)
            LocalizationService.T(pestana.ClaveTexto).Should().NotBe(pestana.ClaveTexto, $"falta la clave {pestana.ClaveTexto} en el diccionario");
    }

    [Fact]
    public void LaBibliotecaTambienSeMarcaConLaFicha()
    {
        Pestanas.Galeria.Vistas.Should().Contain(typeof(GaleriaViewModel)).And.Contain(typeof(DetalleViewModel));
        Pestanas.Historial.Vistas.Should().NotContain(typeof(DetalleViewModel));
    }

    [Fact]
    public void ConfiguracionTambienSeMarcaConElVisorDeRegistros()
        => Pestanas.Configuracion.Vistas.Should().Contain(typeof(ConfiguracionViewModel)).And.Contain(typeof(VisorRegistrosViewModel));

    [Fact]
    public void MarcadaPor_SinVista_EsFalso()
        => Pestanas.Galeria.MarcadaPor(null).Should().BeFalse();

    [Fact]
    public void Item_MuestraElTextoDeLaPestanaYSuOrden()
    {
        var item = new PestanaItemViewModel(Pestanas.Calendario, orden: 5);

        item.Texto.Should().Be(LocalizationService.T("Nav_Calendario"));
        item.Icono.Should().Be("CalendarClock");
        item.Orden.Should().Be(5);
        item.EsActiva.Should().BeFalse();
    }

    [Fact]
    public void Item_LaInsigniaSoloSeVeConAlgoQueContar()
    {
        var item = new PestanaItemViewModel(Pestanas.Descargas, orden: 8);
        item.TieneInsignia.Should().BeFalse();

        item.Insignia = 3;

        item.TieneInsignia.Should().BeTrue();
    }

    [Fact]
    public void Item_RefrescarTexto_AvisaDelCambio()
    {
        var item = new PestanaItemViewModel(Pestanas.Logros, orden: 7);
        var avisos = new System.Collections.Generic.List<string?>();
        item.PropertyChanged += (_, e) => avisos.Add(e.PropertyName);

        item.RefrescarTexto();

        avisos.Should().Contain(nameof(PestanaItemViewModel.Texto));
    }
}
