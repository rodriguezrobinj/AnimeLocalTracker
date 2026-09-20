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

namespace AnimeLocalTracker.Tests.ViewModels;

public class GaleriaPortadasOrdenTests
{
    [Fact]
    public async Task CargarBiblioteca_DeberiaPedirPrimeroLasPortadasQueSeVenEnLaGaleria()
    {
        // La BD devuelve los animes en orden inverso al que muestra la galería (título A-Z). Antes las portadas
        // se cargaban en orden de BD, así que las tarjetas visibles quedaban casi al final de la cola.
        var animes = Enumerable.Range(1, 40)
            .Select(i => new AnimeItem { AniListId = i, Titulo = $"Anime {i:D2}", UrlPortada = $"https://s4.anilist.co/{i}.jpg", EstadoUsuario = "CURRENT" })
            .Reverse()
            .ToList();

        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(animes);
        db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new List<RegistroEpisodio>());

        var pedidas = new ConcurrentQueue<int>();
        var cache = new Mock<IImageCacheService>();
        cache.Setup(c => c.ObtenerPortadaEnMemoria(It.IsAny<int>())).Returns((ImageSource?)null);
        cache.Setup(c => c.ObtenerPortadaAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>()))
             .Returns((int id, string? _, int _) => { pedidas.Enqueue(id); return Task.FromResult<ImageSource?>(null); });

        _ = new GaleriaViewModel(
            Mock.Of<IAnimeTrackingService>(), db.Object, Mock.Of<IAuthService>(), Mock.Of<IDialogService>(),
            Mock.Of<IHttpClientFactory>(), cache.Object, Mock.Of<IFileScannerService>());

        for (int i = 0; i < 100 && pedidas.Count < 40; i++) await Task.Delay(50);
        pedidas.Count.Should().Be(40, "se piden las 40 portadas");

        var ordenPedido = pedidas.ToList();
        // Título A-Z: el anime 1 es la primera tarjeta y el 40 la última. Con varios hilos el orden exacto puede
        // variar unos puestos, pero la primera debe salir al principio y la última al final.
        ordenPedido.IndexOf(1).Should().BeLessThan(4, "la primera tarjeta visible se pide de las primeras");
        ordenPedido.IndexOf(40).Should().BeGreaterThan(35, "la última tarjeta se pide de las últimas");
    }
}
