#pragma warning disable CA1861 // Datos constantes de prueba: un arreglo por llamada es lo más legible aquí
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Orden de visionado: lo guardado, lo que se pide a AniList, el estado de cada parte y el alta desde la ventana.</summary>
public sealed class OrdenVisionadoServiceTests : IDisposable
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFranquiciaService> _franquicias = new();
    private readonly Mock<IAnimeTrackingService> _anilist = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_orden_" + Guid.NewGuid().ToString("N"));
    private readonly List<RelacionAnime> _aristas = new();
    private readonly List<RelacionAnimeSync> _sync = new();
    private readonly List<AnimeItem> _animes = new();
    private readonly List<AnimeRelacionadoInfo> _info = new();
    private readonly Dictionary<int, int> _vistos = new();
    private readonly List<RegistroEpisodio> _registros = new();

    public OrdenVisionadoServiceTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ajustes.Setup(s => s.ObtenerRutaBaseAnimes()).Returns(_carpeta);
        _db.Setup(d => d.ObtenerRelacionesAnimeAsync()).ReturnsAsync(() => _aristas.ToList());
        _db.Setup(d => d.ObtenerRelacionesSincronizadasAsync()).ReturnsAsync(() => _sync.ToList());
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(() => _animes.ToList());
        _db.Setup(d => d.ObtenerRelacionadosInfoAsync()).ReturnsAsync(() => _info.ToList());
        _db.Setup(d => d.ObtenerEpisodiosVistosPorAnimeAsync()).ReturnsAsync(() => new Dictionary<int, int>(_vistos));
        _db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(() => _registros.ToList());
        _db.Setup(d => d.GuardarRelacionadosInfoAsync(It.IsAny<IEnumerable<AnimeRelacionadoInfo>>()))
            .Callback<IEnumerable<AnimeRelacionadoInfo>>(nuevas =>
            {
                var lista = nuevas.ToList();
                _info.RemoveAll(i => lista.Any(n => n.AniListId == i.AniListId));
                _info.AddRange(lista);
            })
            .Returns(Task.CompletedTask);
        _anilist.Setup(a => a.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .ReturnsAsync(new Dictionary<int, AniListMedia>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private OrdenVisionadoService CrearSut() =>
        new(_db.Object, _franquicias.Object, _anilist.Object, new AnimeLibraryService(_db.Object, _ajustes.Object));

    private void Secuela(int a, int b) => _aristas.Add(new RelacionAnime { AnimeId = a, RelacionadoId = b, Tipo = "SEQUEL" });

    private void Sincronizado(int id) => _sync.Add(new RelacionAnimeSync { AnimeId = id, FechaUtc = DateTime.UtcNow });

    private static AniListMedia Media(int id, string titulo, string formato) => new()
    {
        Id = id, Title = new AniListTitle { Romaji = titulo }, Format = formato,
        StartDate = new AniListFuzzyDate { Year = 2020, Month = 10 }, CoverImage = new AniListCoverImage { ExtraLarge = $"https://img/{id}.jpg" },
    };

    [Theory]
    [InlineData("COMPLETED", 0, 0, false, EstadoParteSaga.Vista)]
    [InlineData("CURRENT", 12, 12, false, EstadoParteSaga.Vista)]
    [InlineData("CURRENT", 12, 3, true, EstadoParteSaga.AMedias)]
    [InlineData("PLANNING", 12, 0, true, EstadoParteSaga.Descargada)]
    [InlineData("PLANNING", 12, 0, false, EstadoParteSaga.EnBiblioteca)]
    public void EstadoDe_CadaCaso(string estadoUsuario, int total, int vistos, bool enDisco, EstadoParteSaga esperado)
    {
        var anime = new AnimeItem { AniListId = 1, EstadoUsuario = estadoUsuario, TotalEpisodios = total };
        OrdenVisionadoService.EstadoDe(anime, vistos, enDisco).Should().Be(esperado);
    }

    [Fact]
    public void EstadoDe_FueraDeLaBiblioteca_NoLaTienes() =>
        OrdenVisionadoService.EstadoDe(null, 0, false).Should().Be(EstadoParteSaga.NoLaTienes);

    [Fact]
    public async Task LeerGuardado_MezclaBibliotecaYDatosGuardados()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1", EstadoUsuario = "COMPLETED", AnioLanzamiento = 2013, Temporada = "SPRING" });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Formato = "TV", Anio = 2013, Mes = 4, FechaUtc = DateTime.UtcNow });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 2, Titulo = "Temporada 2", Formato = "TV", Anio = 2017, Mes = 4, UrlPortada = "https://img/2.jpg", FechaUtc = DateTime.UtcNow });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Select(p => (p.AniListId, p.Numero, p.Estado)).Should().Equal((1, 1, EstadoParteSaga.Vista), (2, 2, EstadoParteSaga.NoLaTienes));
        leido.Partes[0].Titulo.Should().Be("Temporada 1");
        leido.Partes[0].Formato.Should().Be("TV");
        leido.Partes[1].Titulo.Should().Be("Temporada 2");
        leido.Partes[1].Anio.Should().Be(2017);
    }

    [Fact]
    public async Task LeerGuardado_ParteSinDatos_TituloNulo()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1" });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Single(p => p.AniListId == 2).Titulo.Should().BeNull();
    }

    [Fact]
    public async Task LeerGuardado_ArchivoEnDisco_EsDescargada()
    {
        Secuela(1, 2);
        _animes.Add(new AnimeItem { AniListId = 2, Titulo = "Temporada 2", TotalEpisodios = 12 });
        _registros.Add(new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 1, RutaArchivo = @"C:\x\Episodio 01.mkv" });

        var leido = await CrearSut().LeerGuardadoAsync(1);

        leido.Partes.Single(p => p.AniListId == 2).Estado.Should().Be(EstadoParteSaga.Descargada);
    }

    [Fact]
    public async Task Actualizar_Sincroniza_YPideSoloLoQueFaltaOCaduco()
    {
        Secuela(1, 2); Secuela(2, 3);
        Sincronizado(1);
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Titulo = "Fresca", FechaUtc = DateTime.UtcNow });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 2, Titulo = "Vieja", FechaUtc = DateTime.UtcNow.AddDays(-40) });
        List<int>? pedidos = null;
        _anilist.Setup(a => a.ObtenerAnimesPorIdsLoteAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<string?>()))
            .Callback<IEnumerable<int>, string?>((ids, _) => pedidos = ids.ToList())
            .ReturnsAsync(new Dictionary<int, AniListMedia> { [2] = Media(2, "T2", "TV"), [3] = Media(3, "Película", "MOVIE") });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        _franquicias.Verify(f => f.SincronizarAsync(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 1 }))), Times.Once);
        pedidos.Should().BeEquivalentTo(new[] { 2, 3 });
        leido.SinConexion.Should().BeFalse();
        leido.Partes.Single(p => p.AniListId == 3).Formato.Should().Be("MOVIE");
    }

    [Fact]
    public async Task Actualizar_SinRed_DevuelveLoGuardadoYAvisa()
    {
        Secuela(1, 2);
        Sincronizado(1);
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Temporada 1" });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        leido.SinConexion.Should().BeTrue();
        leido.Partes.Select(p => p.AniListId).Should().Equal(1, 2);
    }

    [Fact]
    public async Task SinRedYSinRelacionesConsultadas_AvisaSinConexion()
    {
        _animes.Add(new AnimeItem { AniListId = 1, Titulo = "Recién añadido" });
        _info.Add(new AnimeRelacionadoInfo { AniListId = 1, Titulo = "Recién añadido", FechaUtc = DateTime.UtcNow });

        var leido = await CrearSut().ActualizarAsync(1, CancellationToken.None);

        leido.SinConexion.Should().BeTrue();
        leido.Partes.Should().ContainSingle(p => p.AniListId == 1);
    }

    [Fact]
    public async Task TieneFranquicia_SinConsultar_SiConRelacion_NoSinNinguna()
    {
        var sut = CrearSut();
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeTrue("nunca se consultó");

        Sincronizado(1);
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeFalse();

        _aristas.Add(new RelacionAnime { AnimeId = 1, RelacionadoId = 9, Tipo = "CHARACTER" });
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeFalse("un cruce de personajes no es saga");

        _aristas.Add(new RelacionAnime { AnimeId = 9, RelacionadoId = 1, Tipo = "PREQUEL" });
        (await sut.TieneFranquiciaOSinConsultarAsync(1)).Should().BeTrue("la arista guardada desde el otro lado también cuenta");
    }

    [Fact]
    public async Task Anadir_DaDeAltaConLosDatosDeAniList()
    {
        _anilist.Setup(a => a.ObtenerAnimePorIdAsync(3)).ReturnsAsync(Media(3, "Película", "MOVIE"));

        var anime = await CrearSut().AnadirABibliotecaAsync(3);

        anime.Should().NotBeNull();
        anime!.AniListId.Should().Be(3);
        _db.Verify(d => d.GuardarAnimeAsync(It.Is<AnimeItem>(a => a.AniListId == 3)), Times.Once);
    }

    [Fact]
    public async Task Anadir_SinRed_DevuelveNull()
    {
        (await CrearSut().AnadirABibliotecaAsync(3)).Should().BeNull();
    }

    [Fact]
    public async Task AnadirLoQueYaEstaba_DevuelveElDeLaBiblioteca()
    {
        var existente = new AnimeItem { AniListId = 3, Titulo = "Ya estaba" };
        _anilist.Setup(a => a.ObtenerAnimePorIdAsync(3)).ReturnsAsync(Media(3, "Película", "MOVIE"));
        _db.Setup(d => d.ExisteAnimeAsync(3)).ReturnsAsync(true);
        _db.Setup(d => d.ObtenerAnimePorIdAsync(3)).ReturnsAsync(existente);

        (await CrearSut().AnadirABibliotecaAsync(3)).Should().BeSameAs(existente);
    }
}
