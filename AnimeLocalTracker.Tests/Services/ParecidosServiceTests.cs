using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Recomendaciones;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class ParecidosServiceTests
{
    private readonly Mock<IDatabaseService> _db = new();

    private static string Tags(params (string Nombre, int Rank)[] tags)
        => EtiquetasAnime.Serializar(tags.Select(t => new AniListTag { Name = t.Nombre, Rank = t.Rank }));

    private static readonly AnimeItem SteinsGate = new()
    {
        AniListId = 9253, Titulo = "Steins;Gate", Generos = "Sci-Fi, Thriller", EstadoUsuario = "COMPLETED", TotalEpisodios = 24,
        Etiquetas = Tags(("Time Manipulation", 95), ("Thriller", 80)),
    };

    private static readonly AnimeItem Erased = new()
    {
        AniListId = 21234, Titulo = "Erased", Generos = "Mystery, Thriller", EstadoUsuario = "PLANNING", TotalEpisodios = 12,
        Etiquetas = Tags(("Time Manipulation", 90), ("Thriller", 85)),
    };

    private ParecidosService Servicio(params RegistroEpisodio[] registros)
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new List<AnimeItem> { SteinsGate, Erased });
        _db.Setup(d => d.ObtenerEpisodiosVistosPorAnimeAsync()).ReturnsAsync(new Dictionary<int, int> { [Erased.AniListId] = 2 });
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(registros.ToList());
        return new ParecidosService(_db.Object);
    }

    [Fact]
    public async Task Buscar_DevuelveLosParecidosConSusEpisodiosSinVerEnDisco()
    {
        var servicio = Servicio(
            new RegistroEpisodio { AniListId = Erased.AniListId, NumeroEpisodio = 1, RutaArchivo = @"C:\Anime\Erased\01.mkv", VistoLocal = true },
            new RegistroEpisodio { AniListId = Erased.AniListId, NumeroEpisodio = 3, RutaArchivo = @"C:\Anime\Erased\03.mkv" },
            new RegistroEpisodio { AniListId = Erased.AniListId, NumeroEpisodio = 4, RutaArchivo = @"C:\Anime\Erased\04.mkv" },
            new RegistroEpisodio { AniListId = Erased.AniListId, NumeroEpisodio = 5, RutaArchivo = "" });

        var parecidos = await servicio.BuscarAsync(SteinsGate.AniListId, CancellationToken.None);

        parecidos.Should().ContainSingle();
        parecidos[0].Anime.Titulo.Should().Be("Erased");
        parecidos[0].EnDisco.Should().Be(2, "solo cuentan los episodios con archivo y sin ver");
    }

    [Fact]
    public async Task Buscar_AnimeQueNoEstaEnLaBiblioteca_ListaVacia()
        => (await Servicio().BuscarAsync(123456, CancellationToken.None)).Should().BeEmpty();

    [Fact]
    public async Task Buscar_ErrorDeLaBase_ListaVaciaSinLanzar()
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ThrowsAsync(new InvalidOperationException("base bloqueada"));

        (await new ParecidosService(_db.Object).BuscarAsync(SteinsGate.AniListId, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Buscar_Cancelado_ListaVacia()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        (await Servicio().BuscarAsync(SteinsGate.AniListId, cancelado.Token)).Should().BeEmpty();
    }
}
