using System;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Editor de seguimiento de AniList: estados en chips, stepper de episodios, atajos de fecha y puntuación.</summary>
public class DetalleSeguimientoEditorTests
{
    private static DetalleViewModel CrearSut(int totalEpisodios)
    {
        var sut = new DetalleViewModel(
            Mock.Of<IAnimeTrackingService>(),
            Mock.Of<IDatabaseService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IFileScannerService>(),
            Mock.Of<IDialogService>(),
            Mock.Of<IDownloadService>());
        sut.AnimeSeleccionado = new AnimeItem { AniListId = 1, Titulo = "Frieren", TotalEpisodios = totalEpisodios };
        return sut;
    }

    [Fact]
    public void EstadosChips_TienenLosCincoEstadosYSoloUnoActivo()
    {
        var sut = CrearSut(28);

        sut.EstadosChips.Should().HaveCount(5);
        sut.EstadosChips.Count(c => c.EsActivo).Should().Be(1);
        sut.EstadosChips.Single(c => c.EsActivo).Clave.Should().Be(LocalizationService.T("Estado_Viendo"));
    }

    [Fact]
    public void SeleccionarEstado_MueveElChipActivoSinRecrearLosChips()
    {
        var sut = CrearSut(28);
        var chips = sut.EstadosChips.ToList();

        sut.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_EnPausa"));

        sut.EstadosChips.Should().Equal(chips, "los botones no deben regenerarse al elegir (se perderían clics)");
        sut.EstadosChips.Single(c => c.EsActivo).Clave.Should().Be(LocalizationService.T("Estado_EnPausa"));
        sut.EditEstadoVisual.Should().Be(LocalizationService.T("Estado_EnPausa"));
    }

    [Fact]
    public void SeleccionarFinalizado_CompletaElProgresoYLaFechaDeFinSinPisarLaDeInicio()
    {
        var sut = CrearSut(12);
        var inicio = new DateTime(2024, 10, 5);
        sut.EditFechaInicio = inicio;

        sut.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.EditProgreso.Should().Be(12);
        sut.EditFechaFin.Should().Be(DateTime.Today);
        sut.EditFechaInicio.Should().Be(inicio);
    }

    [Fact]
    public void SeleccionarFinalizado_ConTotalDesconocido_NoInventaElProgreso()
    {
        var sut = CrearSut(0);
        sut.EditProgresoTexto = "7";

        sut.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.EditProgreso.Should().Be(7);
    }

    [Fact]
    public void SeleccionarFinalizado_NoPisaUnaFechaDeFinYaEscrita()
    {
        var sut = CrearSut(12);
        var fin = new DateTime(2024, 10, 12);
        sut.EditFechaFin = fin;

        sut.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.EditFechaFin.Should().Be(fin);
    }

    [Fact]
    public void SeleccionarViendo_PoneLaFechaDeInicioSoloSiEstabaVacia()
    {
        var sut = CrearSut(12);

        sut.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Viendo"));

        sut.EditFechaInicio.Should().Be(DateTime.Today);
    }

    [Fact]
    public void Stepper_SumaYRestaRespetandoElTotalYElCero()
    {
        var sut = CrearSut(3);
        sut.EditProgresoTexto = "2";

        sut.IncrementarProgresoCommand.Execute(null);
        sut.EditProgreso.Should().Be(3);

        sut.IncrementarProgresoCommand.Execute(null);
        sut.EditProgreso.Should().Be(3, "no puede pasar del total de episodios");

        sut.EditProgresoTexto = "0";
        sut.DecrementarProgresoCommand.Execute(null);
        sut.EditProgreso.Should().Be(0);
    }

    [Fact]
    public void TotalDeEpisodios_SeMuestraSoloSiSeConoce()
    {
        var conTotal = CrearSut(10);
        conTotal.EditTotalEpisodiosTexto.Should().Be("/ 10");
        conTotal.TieneTotalEpisodios.Should().BeTrue();

        var sinTotal = CrearSut(0);
        sinTotal.TieneTotalEpisodios.Should().BeFalse();
        sinTotal.EditTotalEpisodiosTexto.Should().BeEmpty();
    }

    [Fact]
    public void Puntuacion_MuestraGuionSiEsCero()
    {
        var sut = CrearSut(10);
        sut.EditPuntajeTexto.Should().Be("—");

        sut.EditPuntaje = 85;
        sut.EditPuntajeTexto.Should().Be("85");

        sut.EditPuntaje = 0;
        sut.EditPuntajeTexto.Should().Be("—");
    }

    [Fact]
    public void CalendarioFecha_AbreConLaFechaYaElegidaYUnDiaLaAplicaYCierra()
    {
        var sut = CrearSut(10);
        sut.EditFechaInicio = new DateTime(2024, 10, 5);

        sut.AbrirCalendarioInicioCommand.Execute(null);

        sut.MostrandoCalendarioFecha.Should().BeTrue();
        sut.CalendarioTieneFecha.Should().BeTrue();
        sut.CalendarioFechaInicial.Should().Be(new DateTime(2024, 10, 5));

        sut.ElegirFechaCalendarioCommand.Execute(new DateTime(2014, 3, 9, 15, 30, 0));

        sut.EditFechaInicio.Should().Be(new DateTime(2014, 3, 9), "se guarda solo la fecha, sin hora");
        sut.MostrandoCalendarioFecha.Should().BeFalse();
        sut.EditFechaFin.Should().BeNull("el calendario de inicio no toca la fecha de fin");
    }

    [Fact]
    public void CalendarioFecha_SinFechaAbreEnHoyYNoOfreceQuitar()
    {
        var sut = CrearSut(10);

        sut.AbrirCalendarioFinCommand.Execute(null);

        sut.CalendarioTieneFecha.Should().BeFalse();
        sut.CalendarioFechaInicial.Should().Be(DateTime.Today);
    }

    [Fact]
    public void CalendarioFecha_QuitarVaciaSoloEseCampo()
    {
        var sut = CrearSut(10);
        sut.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.EditFechaFin = new DateTime(2024, 10, 12);
        sut.AbrirCalendarioFinCommand.Execute(null);

        sut.QuitarFechaCalendarioCommand.Execute(null);

        sut.EditFechaFin.Should().BeNull();
        sut.EditFechaInicio.Should().Be(new DateTime(2024, 10, 5));
        sut.MostrandoCalendarioFecha.Should().BeFalse();
    }

    [Fact]
    public void CalendarioFecha_CancelarNoCambiaNada()
    {
        var sut = CrearSut(10);
        sut.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.AbrirCalendarioInicioCommand.Execute(null);

        sut.CerrarCalendarioFechaCommand.Execute(null);

        sut.EditFechaInicio.Should().Be(new DateTime(2024, 10, 5));
        sut.MostrandoCalendarioFecha.Should().BeFalse();
    }

    [Fact]
    public void TextoDeLosCamposDeFecha_MuestraGuionSiNoHayFecha()
    {
        var sut = CrearSut(10);
        sut.EditFechaInicioTexto.Should().Be("—");

        sut.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.EditFechaInicioTexto.Should().Contain("2024");

        sut.EditFechaInicio = null;
        sut.EditFechaInicioTexto.Should().Be("—");
    }
}
