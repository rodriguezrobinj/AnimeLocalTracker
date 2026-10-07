using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

    /// <summary>Textos de "Descargar temporada" y "Eliminar el video tras verlo" (docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md).</summary>
    private static readonly string[] ClavesTemporadaYEliminarTrasVer =
    [
        "Det_TemporadaCompletaFormato", "Det_TemporadaFaltanFormato", "Det_TemporadaTitulo", "Det_TemporadaConfirmacionFormato",
        "Det_TemporadaAvisoEspacioFormato", "Det_TemporadaSinCarpetaMsj",
        "Cfg_EliminarTrasVer", "Cfg_EliminarTrasVerSub", "Cfg_EliminarTrasVer_Apagado", "Cfg_EliminarTrasVer_Automatico",
        "Cfg_EliminarTrasVer_AlCompletar", "Cfg_EliminarTrasVer_ConsumoLigero", "Cfg_EpisodiosAConservar", "Cfg_EpisodiosAConservarSub",
        "Cfg_EliminarTrasVerAvisoTitulo", "Cfg_EliminarTrasVerAvisoMsj",
        "Lim_CompletarTitulo", "Lim_CompletarMsjFormato", "Lim_LiberadoTitulo", "Lim_LiberadoMsjFormato",
    ];

    [Fact]
    public void ClavesNuevas_ExistenEnLosDosIdiomas_NoSonCopiaYLlevanLosMismosMarcadores()
    {
        static HashSet<string> Marcadores(string texto) => Regex.Matches(texto, @"\{\d+\}").Select(m => m.Value).ToHashSet();

        var problemas = new List<string>();
        try
        {
            foreach (string clave in ClavesTemporadaYEliminarTrasVer)
            {
                LocalizationService.Instance.Idioma = "es";
                string es = LocalizationService.T(clave);
                LocalizationService.Instance.Idioma = "en";
                string en = LocalizationService.T(clave);

                if (es == clave) problemas.Add($"{clave}: falta en español");
                else if (en == clave) problemas.Add($"{clave}: falta en inglés");
                else if (en == es) problemas.Add($"{clave}: el inglés es idéntico al español");
                else if (!Marcadores(es).SetEquals(Marcadores(en))) problemas.Add($"{clave}: los marcadores {{n}} no coinciden entre idiomas");
            }
        }
        finally
        {
            LocalizationService.Instance.Idioma = "es";
        }

        problemas.Should().BeEmpty();
    }
}
