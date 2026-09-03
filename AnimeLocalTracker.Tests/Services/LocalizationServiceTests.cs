using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Comportamiento del diccionario ES/EN (LOC-03): idioma por defecto, traducción,
/// degradación a español con valores no reconocidos y claves sin traducir visibles.
/// </summary>
public class LocalizationServiceTests
{
    [Fact]
    public void T_ConIdiomaEspanol_DeberiaDevolverValorEspanol()
    {
        LocalizationService.Instance.Idioma = "es";
        LocalizationService.T("Nav_Galeria").Should().Be("Galería");
    }

    [Fact]
    public void T_ConIdiomaIngles_DeberiaDevolverValorIngles()
    {
        LocalizationService.Instance.Idioma = "en";
        LocalizationService.T("Nav_Galeria").Should().Be("Library");
    }

    [Fact]
    public void T_ConIdiomaDesconocido_DeberiaDegradarAEspanol()
    {
        LocalizationService.Instance.Idioma = "xyz";
        LocalizationService.T("Nav_Galeria").Should().Be("Galería");
    }

    [Fact]
    public void T_ClaveInexistente_DeberiaDevolverLaClaveCruda()
    {
        LocalizationService.Instance.Idioma = "es";
        LocalizationService.T("Clave_Inexistente_XYZ").Should().Be("Clave_Inexistente_XYZ");
        LocalizationService.Instance.Idioma = "en";
        LocalizationService.T("Clave_Inexistente_XYZ").Should().Be("Clave_Inexistente_XYZ");
    }
}
