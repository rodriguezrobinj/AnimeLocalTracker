using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// La zona de contenido de la ventana principal conserva la vista de cada pestaña (cambiar de pestaña no la reconstruye) y
/// reutiliza una sola vista para los ViewModels que se crean nuevos en cada visita (la ficha).
/// </summary>
[Collection("WpfSmoke")]
public class AnfitrionVistasTests
{
    private sealed class Pestana
    {
    }

    private sealed class Ficha : IVistaReutilizable
    {
    }

    private readonly WpfHostFixture _host;

    public AnfitrionVistasTests(WpfHostFixture host) => _host = host;

    private static ContentPresenter PresentadorDe(AnfitrionVistas anfitrion, object vista) =>
        anfitrion.Children.OfType<ContentPresenter>().Single(p => ReferenceEquals(p.Content, vista));

    [Fact]
    public void AlVolverAUnaPestana_DeberiaReutilizarSuVistaEnVezDeCrearOtra()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var galeria = new Pestana();
            var historial = new Pestana();

            anfitrion.Vista = galeria;
            var vistaGaleria = PresentadorDe(anfitrion, galeria);
            anfitrion.Vista = historial;

            anfitrion.Children.Count.Should().Be(2, "la galería se conserva oculta mientras se ve el historial");
            vistaGaleria.Visibility.Should().Be(Visibility.Collapsed);
            PresentadorDe(anfitrion, historial).Visibility.Should().Be(Visibility.Visible);

            anfitrion.Vista = galeria;

            anfitrion.Children.Count.Should().Be(2, "volver a la galería no crea una vista nueva");
            PresentadorDe(anfitrion, galeria).Should().BeSameAs(vistaGaleria);
            vistaGaleria.Visibility.Should().Be(Visibility.Visible);
            PresentadorDe(anfitrion, historial).Visibility.Should().Be(Visibility.Collapsed);
        });
    }

    [Fact]
    public void SoloUnaVistaALaVez_DeberiaEstarVisible()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var pestanas = new object[] { new Pestana(), new Pestana(), new Ficha(), new Pestana() };

            foreach (var vista in pestanas.Concat(pestanas))
            {
                anfitrion.Vista = vista;
                anfitrion.Children.OfType<ContentPresenter>().Count(p => p.Visibility == Visibility.Visible)
                    .Should().Be(1, "nunca debe verse una pestaña encima de otra");
                PresentadorDe(anfitrion, vista).Visibility.Should().Be(Visibility.Visible);
            }
        });
    }

    [Fact]
    public void AlAbrirOtraFichaTrasVolverALaGaleria_DeberiaReutilizarLaMismaVista()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var galeria = new Pestana();
            var primera = new Ficha();
            var segunda = new Ficha();

            anfitrion.Vista = galeria;
            anfitrion.Vista = primera;
            var vistaFicha = PresentadorDe(anfitrion, primera);

            anfitrion.Vista = galeria;
            vistaFicha.Visibility.Should().Be(Visibility.Collapsed, "la vista de la ficha se guarda oculta, no se destruye");

            anfitrion.Vista = segunda;

            anfitrion.Children.Count.Should().Be(2, "hay una sola vista de ficha para todos los animes");
            PresentadorDe(anfitrion, segunda).Should().BeSameAs(vistaFicha);
            vistaFicha.Visibility.Should().Be(Visibility.Visible);
            PresentadorDe(anfitrion, galeria).Visibility.Should().Be(Visibility.Collapsed);
        });
    }

    [Fact]
    public void DeUnaFichaAOtra_DeberiaCambiarElContenidoDeLaMismaVista()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var primera = new Ficha();
            var segunda = new Ficha();

            anfitrion.Vista = primera;
            var vistaFicha = PresentadorDe(anfitrion, primera);
            anfitrion.Vista = segunda;

            anfitrion.Children.Count.Should().Be(1);
            vistaFicha.Content.Should().BeSameAs(segunda);
            vistaFicha.Visibility.Should().Be(Visibility.Visible);
        });
    }

    [Fact]
    public void SinVista_NoDeberiaVerseNinguna()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var juego = new Pestana();

            anfitrion.Vista = juego;
            anfitrion.Vista = null;

            PresentadorDe(anfitrion, juego).Visibility.Should().Be(Visibility.Collapsed, "volver al menú de minijuegos oculta el juego sin destruirlo");
        });
    }

    [Fact]
    public void Precalentar_DeberiaDejarLaVistaHechaYOcultaYUsarseAlNavegar()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var galeria = new Pestana();
            var historial = new Pestana();
            anfitrion.Vista = galeria;

            anfitrion.Precalentar(historial).Should().BeTrue();

            var preparada = PresentadorDe(anfitrion, historial);
            preparada.Visibility.Should().Be(Visibility.Collapsed, "una pestaña preparada por adelantado no se muestra");
            PresentadorDe(anfitrion, galeria).Visibility.Should().Be(Visibility.Visible);
            anfitrion.Precalentar(historial).Should().BeFalse("ya estaba preparada");

            anfitrion.Vista = historial;

            PresentadorDe(anfitrion, historial).Should().BeSameAs(preparada, "la primera visita usa la vista ya preparada");
            preparada.Visibility.Should().Be(Visibility.Visible);
        });
    }

    [Fact]
    public void Precalentar_NoDeberiaTocarLaPestanaEnPantalla_YLaFichaSoloSePreparaUnaVez()
    {
        _host.Ejecutar(() =>
        {
            var anfitrion = new AnfitrionVistas();
            var galeria = new Pestana();
            anfitrion.Vista = galeria;

            anfitrion.Precalentar(galeria).Should().BeFalse();
            PresentadorDe(anfitrion, galeria).Visibility.Should().Be(Visibility.Visible);

            var fichaVacia = new Ficha();
            anfitrion.Precalentar(fichaVacia).Should().BeTrue("la vista de la ficha se deja hecha con una ficha vacía");
            var vistaFicha = PresentadorDe(anfitrion, fichaVacia);
            vistaFicha.Visibility.Should().Be(Visibility.Collapsed);
            anfitrion.Precalentar(new Ficha()).Should().BeFalse("ya hay una vista de ficha");
            vistaFicha.Content.Should().BeSameAs(fichaVacia, "preparar no cambia lo que muestra una vista ya creada");

            var real = new Ficha();
            anfitrion.Vista = real;

            PresentadorDe(anfitrion, real).Should().BeSameAs(vistaFicha, "la primera ficha real usa la vista preparada");
            anfitrion.Children.Count.Should().Be(2);
        });
    }
}
