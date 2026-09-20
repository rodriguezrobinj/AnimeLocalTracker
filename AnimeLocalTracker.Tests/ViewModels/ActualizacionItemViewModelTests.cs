using System.Collections.Generic;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// El estado de un episodio nuevo decide el texto, el color, el icono y el botón principal de su tarjeta:
/// el usuario debe ver de un vistazo QUÉ puede hacer con cada uno.
/// </summary>
public class ActualizacionItemViewModelTests
{
    private static ActualizacionItemViewModel Nuevo() => new() { AniListId = 1, NumeroEpisodio = 12, TituloAnime = "Anime" };

    [Fact]
    public void SinArchivo_EstaSinDescargar_YLaAccionEsDescargar()
    {
        var item = Nuevo();

        item.Estado.Should().Be(EstadoActualizacion.SinDescargar);
        item.AccionTexto.Should().Be(LocalizationService.T("Act_Descargar"));
        item.AccionIcono.Should().Be("Download");
        item.AccionEsPrimaria.Should().BeTrue();
        item.MostrarAccionPrimaria.Should().BeTrue();
        item.MostrarAccionSecundaria.Should().BeFalse();
    }

    [Fact]
    public void ConArchivoSinVerNiEmpezar_EstaListoParaVer_YLaAccionEsVerAhora()
    {
        var item = Nuevo();
        item.Descargado = true;

        item.Estado.Should().Be(EstadoActualizacion.ListoParaVer);
        item.EstadoTexto.Should().Be(LocalizationService.T("Act_Estado_Listo"));
        item.AccionTexto.Should().Be(LocalizationService.T("Act_Accion_VerAhora"));
        item.AccionIcono.Should().Be("Play");
        item.ListoParaVer.Should().BeTrue();
    }

    [Fact]
    public void ConProgresoGuardado_EstaEnProgreso_YLaAccionEsContinuar()
    {
        var item = Nuevo();
        item.Descargado = true;
        item.ProgresoSegundos = 300;
        item.TotalSegundos = 1400;

        item.Estado.Should().Be(EstadoActualizacion.EnProgreso);
        item.AccionTexto.Should().Be(LocalizationService.T("Act_Accion_Continuar"));
        item.TieneProgresoGuardado.Should().BeTrue();
    }

    [Fact]
    public void ProgresoCasCompleto_NoCuentaComoEnProgreso()
    {
        // Mismo umbral que la ficha: pasado el 90 % ya se considera terminado
        var item = Nuevo();
        item.Descargado = true;
        item.ProgresoSegundos = 1350;
        item.TotalSegundos = 1400;

        item.Estado.Should().Be(EstadoActualizacion.ListoParaVer);
    }

    [Fact]
    public void Visto_ConArchivo_LaAccionEsVerDeNuevo_YEsSecundaria()
    {
        var item = Nuevo();
        item.Descargado = true;
        item.Visto = true;

        item.Estado.Should().Be(EstadoActualizacion.Visto);
        item.AccionTexto.Should().Be(LocalizationService.T("Act_Accion_VerDeNuevo"));
        item.AccionIcono.Should().Be("Replay");
        item.AccionEsPrimaria.Should().BeFalse();
        item.MostrarAccionSecundaria.Should().BeTrue();
        item.MostrarAccionPrimaria.Should().BeFalse();
    }

    [Fact]
    public void Visto_SinArchivo_LaAccionSigueSiendoDescargar_PeroSecundaria()
    {
        // Lo viste por otro lado: se puede descargar, pero ya no es "lo siguiente que hacer"
        var item = Nuevo();
        item.Visto = true;

        item.Estado.Should().Be(EstadoActualizacion.Visto);
        item.AccionTexto.Should().Be(LocalizationService.T("Act_Descargar"));
        item.AccionIcono.Should().Be("Download");
        item.MostrarAccionSecundaria.Should().BeTrue();
    }

    [Fact]
    public void Descargando_MuestraElProgreso_YOcultaLosBotones()
    {
        var item = Nuevo();
        item.IsDownloading = true;
        item.DownloadProgress = 42.4;

        item.Estado.Should().Be(EstadoActualizacion.Descargando);
        item.EstadoTexto.Should().Be(string.Format(LocalizationService.T("Act_Estado_DescargandoFormato"), 42));
        item.MostrarAccion.Should().BeFalse();
        item.MostrarAccionPrimaria.Should().BeFalse();
        item.MostrarAccionSecundaria.Should().BeFalse();
    }

    [Fact]
    public void Descargando_SinPorcentajeTodavia_EstaEnCola()
    {
        var item = Nuevo();
        item.IsDownloading = true;

        item.EstadoTexto.Should().Be(LocalizationService.T("Act_Estado_EnCola"));
    }

    [Fact]
    public void DescargandoTienePrioridadSobreLoDemas()
    {
        var item = Nuevo();
        item.Visto = true;
        item.IsDownloading = true;

        item.Estado.Should().Be(EstadoActualizacion.Descargando);
    }

    [Theory]
    [InlineData(EstadoActualizacion.SinDescargar, "#94A3B8")]
    [InlineData(EstadoActualizacion.Descargando, "#60A5FA")]
    [InlineData(EstadoActualizacion.ListoParaVer, "#34D399")]
    [InlineData(EstadoActualizacion.EnProgreso, "#FBBF24")]
    [InlineData(EstadoActualizacion.Visto, "#A78BFA")]
    public void CadaEstadoTieneSuColorDeLaPaleta(EstadoActualizacion estado, string colorEsperado)
    {
        var item = Nuevo();
        switch (estado)
        {
            case EstadoActualizacion.Descargando: item.IsDownloading = true; break;
            case EstadoActualizacion.ListoParaVer: item.Descargado = true; break;
            case EstadoActualizacion.EnProgreso: item.Descargado = true; item.ProgresoSegundos = 300; item.TotalSegundos = 1400; break;
            case EstadoActualizacion.Visto: item.Visto = true; break;
        }

        item.Estado.Should().Be(estado);
        item.EstadoColor.Should().Be(colorEsperado);
        item.EstadoIcono.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BanderasParaFiltrosYContadores()
    {
        var item = Nuevo();
        item.PorDescargar.Should().BeTrue();
        item.SinVer.Should().BeTrue();
        item.ListoParaVer.Should().BeFalse();

        item.Descargado = true;
        item.PorDescargar.Should().BeFalse();
        item.ListoParaVer.Should().BeTrue();

        item.Visto = true;
        item.SinVer.Should().BeFalse();
        item.ListoParaVer.Should().BeFalse();
    }

    [Fact]
    public void AlCambiarElEstado_SeNotificanTodasLasPropiedadesDerivadas()
    {
        var item = Nuevo();
        var cambios = new List<string?>();
        item.PropertyChanged += (_, e) => cambios.Add(e.PropertyName);

        item.Descargado = true;

        cambios.Should().Contain(new[]
        {
            nameof(ActualizacionItemViewModel.Estado), nameof(ActualizacionItemViewModel.EstadoTexto),
            nameof(ActualizacionItemViewModel.EstadoColor), nameof(ActualizacionItemViewModel.EstadoIcono),
            nameof(ActualizacionItemViewModel.AccionTexto), nameof(ActualizacionItemViewModel.AccionIcono),
            nameof(ActualizacionItemViewModel.MostrarAccionPrimaria), nameof(ActualizacionItemViewModel.MostrarAccionSecundaria),
            nameof(ActualizacionItemViewModel.ListoParaVer), nameof(ActualizacionItemViewModel.PorDescargar)
        });
    }

    [Fact]
    public void RefrescarTextos_NotificaTodoParaVolverALeerLosTextosLocalizados()
    {
        var item = Nuevo();
        var cambios = new List<string?>();
        item.PropertyChanged += (_, e) => cambios.Add(e.PropertyName);

        item.RefrescarTextos();

        cambios.Should().Contain(string.Empty, "un nombre vacío = todas las propiedades");
    }

    [Fact]
    public void LaPildoraDelEpisodioUsaElNumero()
    {
        Nuevo().EpisodioCorto.Should().Contain("12");
    }
}
