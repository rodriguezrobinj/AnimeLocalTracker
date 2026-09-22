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
        // Título A-Z: el anime 1 es la primera tarjeta y el 40 la última. Parallel.ForEachAsync ya recibe la
        // lista pre-ordenada por posición visual (el orden se calcula ANTES de paralelizar), pero el hilo que
        // toma el primer elemento de la cola puede quedar apartado por el planificador del SO si la CPU está
        // saturada (p. ej. la suite completa corriendo en paralelo bajo carga alta) — eso no es una regresión
        // del código, así que el margen debe tolerar esa demora sin dejar de detectar si el orden se rompe de
        // verdad (sin la priorización, el 1 saldría casi al final y el 40 casi al principio: la diferencia
        // sería enorme, no de unos pocos puestos).
        ordenPedido.IndexOf(1).Should().BeLessThan(15, "la primera tarjeta visible se pide entre las primeras, con margen para el jitter del scheduler bajo carga");
        ordenPedido.IndexOf(40).Should().BeGreaterThan(24, "la última tarjeta se pide entre las últimas, con el mismo margen");
    }
}
