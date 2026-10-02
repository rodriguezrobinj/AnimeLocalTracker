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

        sut.Seguimiento.EstadosChips.Should().HaveCount(5);
        sut.Seguimiento.EstadosChips.Count(c => c.EsActivo).Should().Be(1);
        sut.Seguimiento.EstadosChips.Single(c => c.EsActivo).Clave.Should().Be(LocalizationService.T("Estado_Viendo"));
    }

    [Fact]
    public void SeleccionarEstado_MueveElChipActivoSinRecrearLosChips()
    {
        var sut = CrearSut(28);
        var chips = sut.Seguimiento.EstadosChips.ToList();

        sut.Seguimiento.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_EnPausa"));

        sut.Seguimiento.EstadosChips.Should().Equal(chips, "los botones no deben regenerarse al elegir (se perderían clics)");
        sut.Seguimiento.EstadosChips.Single(c => c.EsActivo).Clave.Should().Be(LocalizationService.T("Estado_EnPausa"));
        sut.Seguimiento.EditEstadoVisual.Should().Be(LocalizationService.T("Estado_EnPausa"));
    }

    [Fact]
    public void SeleccionarFinalizado_CompletaElProgresoYLaFechaDeFinSinPisarLaDeInicio()
    {
        var sut = CrearSut(12);
        var inicio = new DateTime(2024, 10, 5);
        sut.Seguimiento.EditFechaInicio = inicio;

        sut.Seguimiento.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.Seguimiento.EditProgreso.Should().Be(12);
        sut.Seguimiento.EditFechaFin.Should().Be(DateTime.Today);
        sut.Seguimiento.EditFechaInicio.Should().Be(inicio);
    }

    [Fact]
    public void SeleccionarFinalizado_ConTotalDesconocido_NoInventaElProgreso()
    {
        var sut = CrearSut(0);
        sut.Seguimiento.EditProgresoTexto = "7";

        sut.Seguimiento.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.Seguimiento.EditProgreso.Should().Be(7);
    }

    [Fact]
    public void SeleccionarFinalizado_NoPisaUnaFechaDeFinYaEscrita()
    {
        var sut = CrearSut(12);
        var fin = new DateTime(2024, 10, 12);
        sut.Seguimiento.EditFechaFin = fin;

        sut.Seguimiento.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Finalizado"));

        sut.Seguimiento.EditFechaFin.Should().Be(fin);
    }

    [Fact]
    public void SeleccionarViendo_PoneLaFechaDeInicioSoloSiEstabaVacia()
    {
        var sut = CrearSut(12);

        sut.Seguimiento.SeleccionarEstadoCommand.Execute(LocalizationService.T("Estado_Viendo"));

        sut.Seguimiento.EditFechaInicio.Should().Be(DateTime.Today);
    }

    [Fact]
    public void Stepper_SumaYRestaRespetandoElTotalYElCero()
    {
        var sut = CrearSut(3);
        sut.Seguimiento.EditProgresoTexto = "2";

        sut.Seguimiento.IncrementarProgresoCommand.Execute(null);
        sut.Seguimiento.EditProgreso.Should().Be(3);

        sut.Seguimiento.IncrementarProgresoCommand.Execute(null);
        sut.Seguimiento.EditProgreso.Should().Be(3, "no puede pasar del total de episodios");

        sut.Seguimiento.EditProgresoTexto = "0";
        sut.Seguimiento.DecrementarProgresoCommand.Execute(null);
        sut.Seguimiento.EditProgreso.Should().Be(0);
    }

    [Fact]
    public void TotalDeEpisodios_SeMuestraSoloSiSeConoce()
    {
        var conTotal = CrearSut(10);
        conTotal.Seguimiento.EditTotalEpisodiosTexto.Should().Be("/ 10");
        conTotal.Seguimiento.TieneTotalEpisodios.Should().BeTrue();

        var sinTotal = CrearSut(0);
        sinTotal.Seguimiento.TieneTotalEpisodios.Should().BeFalse();
        sinTotal.Seguimiento.EditTotalEpisodiosTexto.Should().BeEmpty();
    }

    [Fact]
    public void Puntuacion_MuestraGuionSiEsCero()
    {
        var sut = CrearSut(10);
        sut.Seguimiento.EditPuntajeTexto.Should().Be("—");

        sut.Seguimiento.EditPuntaje = 85;
        sut.Seguimiento.EditPuntajeTexto.Should().Be("85");

        sut.Seguimiento.EditPuntaje = 0;
        sut.Seguimiento.EditPuntajeTexto.Should().Be("—");
    }

    [Fact]
    public void CalendarioFecha_AbreConLaFechaYaElegidaYUnDiaLaAplicaYCierra()
    {
        var sut = CrearSut(10);
        sut.Seguimiento.EditFechaInicio = new DateTime(2024, 10, 5);

        sut.Seguimiento.AbrirCalendarioInicioCommand.Execute(null);

        sut.Seguimiento.MostrandoCalendarioFecha.Should().BeTrue();
        sut.Seguimiento.CalendarioTieneFecha.Should().BeTrue();
        sut.Seguimiento.CalendarioFechaInicial.Should().Be(new DateTime(2024, 10, 5));

        sut.Seguimiento.ElegirFechaCalendarioCommand.Execute(new DateTime(2014, 3, 9, 15, 30, 0));

        sut.Seguimiento.EditFechaInicio.Should().Be(new DateTime(2014, 3, 9), "se guarda solo la fecha, sin hora");
        sut.Seguimiento.MostrandoCalendarioFecha.Should().BeFalse();
        sut.Seguimiento.EditFechaFin.Should().BeNull("el calendario de inicio no toca la fecha de fin");
    }

    [Fact]
    public void CalendarioFecha_SinFechaAbreEnHoyYNoOfreceQuitar()
    {
        var sut = CrearSut(10);

        sut.Seguimiento.AbrirCalendarioFinCommand.Execute(null);

        sut.Seguimiento.CalendarioTieneFecha.Should().BeFalse();
        sut.Seguimiento.CalendarioFechaInicial.Should().Be(DateTime.Today);
    }

    [Fact]
    public void CalendarioFecha_QuitarVaciaSoloEseCampo()
    {
        var sut = CrearSut(10);
        sut.Seguimiento.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.Seguimiento.EditFechaFin = new DateTime(2024, 10, 12);
        sut.Seguimiento.AbrirCalendarioFinCommand.Execute(null);

        sut.Seguimiento.QuitarFechaCalendarioCommand.Execute(null);

        sut.Seguimiento.EditFechaFin.Should().BeNull();
        sut.Seguimiento.EditFechaInicio.Should().Be(new DateTime(2024, 10, 5));
        sut.Seguimiento.MostrandoCalendarioFecha.Should().BeFalse();
    }

    [Fact]
    public void CalendarioFecha_CancelarNoCambiaNada()
    {
        var sut = CrearSut(10);
        sut.Seguimiento.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.Seguimiento.AbrirCalendarioInicioCommand.Execute(null);

        sut.Seguimiento.CerrarCalendarioFechaCommand.Execute(null);

        sut.Seguimiento.EditFechaInicio.Should().Be(new DateTime(2024, 10, 5));
        sut.Seguimiento.MostrandoCalendarioFecha.Should().BeFalse();
    }

    [Fact]
    public void TextoDeLosCamposDeFecha_MuestraGuionSiNoHayFecha()
    {
        var sut = CrearSut(10);
        sut.Seguimiento.EditFechaInicioTexto.Should().Be("—");

        sut.Seguimiento.EditFechaInicio = new DateTime(2024, 10, 5);
        sut.Seguimiento.EditFechaInicioTexto.Should().Contain("2024");

        sut.Seguimiento.EditFechaInicio = null;
        sut.Seguimiento.EditFechaInicioTexto.Should().Be("—");
    }
}
