using System;
using System.Windows.Controls;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using AnimeLocalTracker.Views;
using FluentAssertions;
using MaterialDesignThemes.Wpf;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Los avisos de la ventana principal quedan tapados por el video (Flyleaf dibuja en su propia
/// ventana nativa), así que la vista del reproductor dibuja los suyos encima del video.
/// </summary>
[Collection("WpfSmoke")]
public class ReproductorToastTests
{
    private readonly WpfHostFixture _host;

    public ReproductorToastTests(WpfHostFixture host) => _host = host;

    private static ReproductorViewModel CrearVm(IDialogService dialogo) =>
        new(Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), dialogService: dialogo);

    [Fact]
    public void ElToastDeIDialogService_SeDibujaEnLaVistaDelReproductor()
    {
        _host.Ejecutar(() =>
        {
            var dialogo = new DialogService();
            var vista = new ReproductorView { DataContext = CrearVm(dialogo) };

            dialogo.MostrarToast("Reanudar Reproducción", "Continuando desde 10:26", "PlaySpeed", "#2196F3");

            ((TextBlock)vista.FindName("ToastRepTitulo")).Text.Should().Be("Reanudar Reproducción");
            ((TextBlock)vista.FindName("ToastRepMensaje")).Text.Should().Be("Continuando desde 10:26");
            ((PackIcon)vista.FindName("ToastRepIcono")).Kind.Should().Be(PackIconKind.PlaySpeed);
        });
    }

    [Fact]
    public void UnIconoInvalido_NoRompeElAvisoYUsaUnoPorDefecto()
    {
        _host.Ejecutar(() =>
        {
            var dialogo = new DialogService();
            var vista = new ReproductorView { DataContext = CrearVm(dialogo) };

            var act = () => dialogo.MostrarToast("T", "M", "IconoQueNoExiste", "esto-no-es-un-color");

            act.Should().NotThrow();
            ((PackIcon)vista.FindName("ToastRepIcono")).Kind.Should().Be(PackIconKind.InformationOutline);
            ((TextBlock)vista.FindName("ToastRepTitulo")).Text.Should().Be("T");
        });
    }

    [Fact]
    public void AlQuitarElViewModel_LaVistaDejaDeEscucharLosAvisos()
    {
        _host.Ejecutar(() =>
        {
            var dialogo = new DialogService();
            var vista = new ReproductorView { DataContext = CrearVm(dialogo) };
            dialogo.MostrarToast("Primero", "M1", "InformationOutline", "#2196F3");

            vista.DataContext = null;
            dialogo.MostrarToast("Segundo", "M2", "InformationOutline", "#2196F3");

            ((TextBlock)vista.FindName("ToastRepTitulo")).Text.Should().Be("Primero", "sin ViewModel la vista ya no se suscribe al servicio");
        });
    }

    [Fact]
    public void SinIDialogService_LaVistaNoFalla()
    {
        _host.Ejecutar(() =>
        {
            var vm = new ReproductorViewModel(Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>());

            var act = () => _ = new ReproductorView { DataContext = vm };

            act.Should().NotThrow();
            vm.DialogService.Should().BeNull();
        });
    }
}
