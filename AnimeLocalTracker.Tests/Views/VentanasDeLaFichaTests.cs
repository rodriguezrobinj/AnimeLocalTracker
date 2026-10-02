using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using AnimeLocalTracker.Views;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// Las ventanas de la ficha (editor de seguimiento y música) viven en su propia vista y se crean cuando ya se han pedido,
/// recibiendo su ViewModel en ese momento.
/// </summary>
[Collection("WpfSmoke")]
public class VentanasDeLaFichaTests
{
    private readonly WpfHostFixture _host;

    public VentanasDeLaFichaTests(WpfHostFixture host) => _host = host;

    private static IEnumerable<T> Descendientes<T>(DependencyObject raiz) where T : DependencyObject
    {
        foreach (object hijo in LogicalTreeHelper.GetChildren(raiz))
        {
            if (hijo is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var mas in Descendientes<T>(d)) yield return mas;
        }
    }

    /// <summary>Deja que WPF active los enlaces pendientes (lo hace en su cola, no al asignar el DataContext).</summary>
    private static void ProcesarEnlaces() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static void CargarEstilosDeLaFicha()
    {
        var uri = new Uri("pack://application:,,,/AnimeLocalTracker;component/Views/FichaEstilos.xaml");
        var recursos = Application.Current.Resources.MergedDictionaries;
        if (!recursos.Any(r => r.Source == uri)) recursos.Add(new ResourceDictionary { Source = uri });
    }

    [Fact]
    public void EditorDeSeguimiento_CreadoConElEditorYaAbierto_MuestraSusDatos()
    {
        // Regresión real: la vista se crea cuando el editor ya está pedido. Al ajustar el idioma del calendario en ese mismo
        // instante (en mitad de la propagación del DataContext), los campos de texto se quedaban sin DataContext y salían
        // vacíos: ni episodios vistos, ni puntuación, ni fechas, ni el título del anime.
        _host.Ejecutar(() =>
        {
            var vm = new SeguimientoEditorViewModel(Mock.Of<IAnimeTrackingService>(), Mock.Of<IDatabaseService>(), Mock.Of<IAuthService>(), Mock.Of<IDialogService>())
            {
                Anime = new AnimeItem { AniListId = 1, Titulo = "Frieren", TotalEpisodios = 28 },
                EditProgresoTexto = "7",
                MostrandoEditorSeguimiento = true
            };

            var vista = new EditorSeguimientoView { DataContext = vm };
            ProcesarEnlaces();

            var campoEpisodios = Descendientes<TextBox>(vista).First();
            campoEpisodios.DataContext.Should().BeSameAs(vm);
            campoEpisodios.Text.Should().Be("7");
            Descendientes<TextBlock>(vista).Select(t => t.Text).Should().Contain("Frieren").And.Contain("/ 28");
        });
    }

    [Fact]
    public void PanelDeMusica_SeCreaConSuViewModel()
    {
        _host.Ejecutar(() =>
        {
            CargarEstilosDeLaFicha();
            var vm = new MusicaFichaViewModel(Mock.Of<IDialogService>()) { MostrandoPanelMusica = true };

            var vista = new PanelMusicaView { DataContext = vm };
            ProcesarEnlaces();

            vista.Visibility.Should().Be(Visibility.Visible);
            vm.MostrandoPanelMusica = false;
            ProcesarEnlaces();
            vista.Visibility.Should().Be(Visibility.Collapsed);
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task PanelDeMusica_ConUnTemaSonando_MuestraLaBarraDeSonandoAhora()
    {
        var tema = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", TituloCancion = "We Are!", Artistas = "Hiroshi Kitadani", AudioUrlOgg = "https://a/op1.ogg" };
        var temas = new Mock<IAnimeThemesService>();
        temas.Setup(s => s.ObtenerTemasAsync(7, It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { tema });
        var descargas = new Mock<IAnimeThemesDownloadService>();
        descargas.Setup(d => d.EstaDescargado(7, tema)).Returns(true);
        descargas.Setup(d => d.ObtenerRutaLocalEsperada(7, tema)).Returns(@"C:\Music\7\OP1.mp3");
        using var reproductor = new AnimeLocalTracker.Tests.ViewModels.FakeAudioTrackPlayer();
        var vm = new MusicaFichaViewModel(Mock.Of<IDialogService>(), temas.Object, descargas.Object, reproductor)
        {
            Anime = new AnimeItem { AniListId = 7, Titulo = "One Piece" },
            MostrandoPanelMusica = true
        };
        await vm.CargarTemasMusicalesAsync();

        _host.Ejecutar(() =>
        {
            CargarEstilosDeLaFicha();
            var vista = new PanelMusicaView { DataContext = vm };
            ProcesarEnlaces();
            Descendientes<TextBlock>(vista).Select(t => t.Text).Should().NotContain("We Are!", "sin nada cargado no hay barra");

            vm.ReproducirTemaCommand.Execute(vm.TemasMusicales[0]);
            ProcesarEnlaces();

            // Las filas de la lista salen de una plantilla (no están en el árbol lógico): este título es el de la barra.
            Descendientes<TextBlock>(vista).Select(t => t.Text).Should().Contain("We Are!");
            vm.DetenerMusica();
        });
    }
}
