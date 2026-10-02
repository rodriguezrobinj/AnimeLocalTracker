using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Lectura adelantada de la biblioteca al abrir la app: se entrega una sola vez, adelanta solo las portadas de la primera
/// pantalla y, si algo falla, la Galería sigue leyendo de la base de datos como siempre.
/// </summary>
public class PrecargaBibliotecaTests
{
    private static List<AnimeItem> Animes(int cantidad) => Enumerable.Range(1, cantidad)
        .Select(i => new AnimeItem { AniListId = i, Titulo = $"Anime {i:D3}", UrlPortada = $"https://s4.anilist.co/{i}.jpg", EstadoUsuario = "CURRENT" })
        .Reverse() // la base de datos no devuelve los animes en el orden en que los muestra la Galería
        .ToList();

    private static Mock<IDatabaseService> BaseDatos(List<AnimeItem> animes, List<RegistroEpisodio>? registros = null)
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);
        db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(registros ?? new List<RegistroEpisodio>());
        return db;
    }

    [Fact]
    public void Consumir_SinHaberIniciado_DeberiaDevolverNull()
    {
        var sut = new PrecargaBiblioteca(BaseDatos(Animes(3)).Object, Mock.Of<IImageCacheService>());

        sut.Consumir().Should().BeNull();
    }

    [Fact]
    public async Task Consumir_DeberiaEntregarLaLecturaUnaSolaVez()
    {
        var animes = Animes(5);
        var registros = new List<RegistroEpisodio> { new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true } };
        var db = BaseDatos(animes, registros);
        var sut = new PrecargaBiblioteca(db.Object, Mock.Of<IImageCacheService>());

        sut.Iniciar();
        sut.Iniciar(); // repetir no lanza otra lectura
        var lectura = sut.Consumir();

        lectura.Should().NotBeNull();
        var datos = await lectura!;
        datos.Animes.Should().BeSameAs(animes);
        datos.Registros.Should().BeSameAs(registros);
        sut.Consumir().Should().BeNull("la lectura adelantada es solo para la primera carga de la Galería");
        db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Once);
    }

    [Fact]
    public async Task Iniciar_DeberiaAdelantarSoloLasPortadasDeLaPrimeraPantalla_EnOrdenDeTitulo()
    {
        var pedidas = new ConcurrentBag<int>();
        var imagenes = new Mock<IImageCacheService>();
        imagenes.Setup(c => c.ObtenerPortadaAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>()))
                .Returns((int id, string? _, int _) => { pedidas.Add(id); return Task.FromResult<ImageSource?>(null); });
        var sut = new PrecargaBiblioteca(BaseDatos(Animes(60)).Object, imagenes.Object);

        sut.Iniciar();
        await sut.Consumir()!;

        pedidas.OrderBy(id => id).Should().Equal(Enumerable.Range(1, PrecargaBiblioteca.PortadasPrimeraPantalla),
            "solo se adelantan las portadas que se ven al abrir (título A-Z); el resto las carga la Galería después");
    }

    [Fact]
    public async Task Iniciar_SiLasPortadasFallan_DeberiaEntregarIgualLaBiblioteca()
    {
        var animes = Animes(4);
        var imagenes = new Mock<IImageCacheService>();
        imagenes.Setup(c => c.ObtenerPortadaAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("disco no disponible"));
        var sut = new PrecargaBiblioteca(BaseDatos(animes).Object, imagenes.Object);

        sut.Iniciar();
        var datos = await sut.Consumir()!;

        datos.Animes.Should().HaveCount(4);
    }

    [Fact]
    public async Task Galeria_ConLecturaAdelantada_DeberiaUsarlaSinVolverALeerLaBaseDeDatos()
    {
        var animes = Animes(6);
        var db = BaseDatos(animes, new List<RegistroEpisodio> { new() { AniListId = 2, NumeroEpisodio = 1, VistoLocal = true } });
        var imagenes = new Mock<IImageCacheService>();
        var precarga = new PrecargaBiblioteca(db.Object, imagenes.Object);
        precarga.Iniciar();

        var galeria = new GaleriaViewModel(
            Mock.Of<IAnimeTrackingService>(), db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<IHttpClientFactory>(), imagenes.Object, Mock.Of<IFileScannerService>(), precarga: precarga);

        for (int i = 0; i < 100 && galeria.BibliotecaLocales.Count < 6; i++) await Task.Delay(50);

        galeria.BibliotecaLocales.Should().HaveCount(6);
        galeria.BibliotecaLocales.Single(a => a.AniListId == 2).EpisodiosVistos.Should().Be(1);
        db.Verify(d => d.ObtenerTodosLosAnimesAsync(), Times.Once, "la Galería usa la lectura adelantada en vez de repetirla");
        db.Verify(d => d.ObtenerTodosLosRegistrosAsync(), Times.Once);
    }

    [Fact]
    public async Task Galeria_SiLaLecturaAdelantadaFalla_DeberiaLeerDeLaBaseDeDatos()
    {
        var animes = Animes(3);
        var db = new Mock<IDatabaseService>();
        db.SetupSequence(d => d.ObtenerTodosLosAnimesAsync())
          .ThrowsAsync(new InvalidOperationException("base de datos ocupada"))
          .ReturnsAsync(animes);
        db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());
        var imagenes = new Mock<IImageCacheService>();
        var precarga = new PrecargaBiblioteca(db.Object, imagenes.Object);
        precarga.Iniciar();

        var galeria = new GaleriaViewModel(
            Mock.Of<IAnimeTrackingService>(), db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<IHttpClientFactory>(), imagenes.Object, Mock.Of<IFileScannerService>(), precarga: precarga);

        for (int i = 0; i < 100 && galeria.BibliotecaLocales.Count < 3; i++) await Task.Delay(50);

        galeria.BibliotecaLocales.Should().HaveCount(3, "un fallo de la lectura adelantada no deja la Galería vacía");
    }
}
