using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Personajes de AniList: consulta por lotes, copia local con vigencia e imágenes en una carpeta temporal.</summary>
public class PersonajesServiceTests : IDisposable
{
    private static readonly byte[] PngValido = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0];

    private readonly Mock<HttpMessageHandler> _handler = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly List<string> _peticiones = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Personajes_{Guid.NewGuid():N}");
    private readonly PersonajesService _sut;

    private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public PersonajesServiceTests()
    {
        _handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (peticion, ct) =>
            {
                _peticiones.Add(peticion.Content == null ? peticion.RequestUri!.ToString() : await peticion.Content.ReadAsStringAsync(ct));
                return _responder(peticion);
            });

        _db.Setup(d => d.ObtenerPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(new List<PersonajeAnime>());
        _db.Setup(d => d.ObtenerMarcasPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(new List<PersonajesAnimeSync>());

        _sut = new PersonajesService(_db.Object, new HttpClient(_handler.Object), _carpeta);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_carpeta)) Directory.Delete(_carpeta, true); } catch { /* ignore */ }
    }

    private static string Respuesta(params int[] animes) =>
        "{\"data\":{\"Page\":{\"media\":[" + string.Join(",", animes.Select(id => $@"
            {{""id"": {id}, ""characters"": {{ ""edges"": [
                {{ ""role"": ""MAIN"", ""node"": {{ ""id"": {id * 10 + 1}, ""name"": {{ ""full"": ""Light Yagami"", ""native"": ""夜神月"", ""alternative"": [""Kira"", ""Light Asahi""] }},
                   ""image"": {{ ""large"": ""https://s4.anilist.co/file/anilistcdn/character/large/b{id}.png"" }}, ""gender"": ""Male"", ""age"": ""17-23"", ""favourites"": 20349 }} }},
                {{ ""role"": ""supporting"", ""node"": {{ ""id"": {id * 10 + 2}, ""name"": {{ ""full"": ""Ryuk"", ""native"": null, ""alternative"": null }},
                   ""image"": {{ ""large"": null }}, ""gender"": null, ""age"": null, ""favourites"": null }} }}
            ] }} }}")) + "]}}}";

    private void RespondeJson(Func<string> json) =>
        _responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json()) };

    // === Conversión de la respuesta ===

    [Fact]
    public async Task Obtener_ConvierteLosCamposDeAniList()
    {
        RespondeJson(() => Respuesta(1535));

        var r = await _sut.ObtenerAsync([1535]);

        var lista = r[1535];
        lista.Should().HaveCount(2);
        var light = lista[0];
        light.AnimeId.Should().Be(1535);
        light.PersonajeId.Should().Be(15351);
        light.Nombre.Should().Be("Light Yagami");
        light.NombreNativo.Should().Be("夜神月");
        light.Alternativos.Should().Be("Kira | Light Asahi");
        light.Genero.Should().Be("Male");
        light.Edad.Should().Be("17-23");
        light.Rol.Should().Be("MAIN");
        light.Favoritos.Should().Be(20349);
        lista[1].Rol.Should().Be("SUPPORTING", "el rol se normaliza a mayúsculas");
        lista[1].Genero.Should().BeEmpty();
        lista[1].ImagenUrl.Should().BeEmpty();
    }

    [Fact]
    public void Convertir_IgnoraNodosSinNombreOSinId_YRepetidos()
    {
        var json = @"{""data"":{""Page"":{""media"":[{""id"":5,""characters"":{""edges"":[
            {""role"":""MAIN"",""node"":{""id"":1,""name"":{""full"":""  ""}}},
            {""role"":""MAIN"",""node"":{""id"":0,""name"":{""full"":""Sin id""}}},
            {""role"":""MAIN"",""node"":null},
            {""role"":""MAIN"",""node"":{""id"":7,""name"":{""full"":""Bien""}}},
            {""role"":""SUPPORTING"",""node"":{""id"":7,""name"":{""full"":""Bien""}}}]}}]}}}";

        var r = PersonajesService.Convertir([5, 6], System.Text.Json.JsonSerializer.Deserialize<PersonajesService.RespuestaPersonajes>(json));

        r[5].Should().ContainSingle().Which.Nombre.Should().Be("Bien");
        r[6].Should().BeEmpty("un anime que AniList no devuelve queda con lista vacía");
    }

    // === Copia local y consultas ===

    [Fact]
    public async Task Obtener_ConCopiaVigente_NoConsultaAAniList()
    {
        var guardado = new PersonajeAnime { AnimeId = 1, PersonajeId = 5, Nombre = "Guardado" };
        _db.Setup(d => d.ObtenerPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync([guardado]);
        _db.Setup(d => d.ObtenerMarcasPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([new PersonajesAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow.AddDays(-3) }]);

        var r = await _sut.ObtenerAsync([1]);

        r[1].Should().ContainSingle().Which.Nombre.Should().Be("Guardado");
        _peticiones.Should().BeEmpty("ya hay copia de hace 3 días");
        _db.Verify(d => d.GuardarPersonajesAsync(It.IsAny<IReadOnlyDictionary<int, List<PersonajeAnime>>>()), Times.Never);
    }

    [Fact]
    public async Task Obtener_ConCopiaVieja_ConsultaDeNuevoYGuarda()
    {
        _db.Setup(d => d.ObtenerMarcasPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([new PersonajesAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow.AddDays(-40) }]);
        RespondeJson(() => Respuesta(1));

        var r = await _sut.ObtenerAsync([1]);

        _peticiones.Should().ContainSingle();
        r[1].Should().HaveCount(2);
        _db.Verify(d => d.GuardarPersonajesAsync(It.Is<IReadOnlyDictionary<int, List<PersonajeAnime>>>(x => x.ContainsKey(1) && x[1].Count == 2)), Times.Once);
    }

    [Fact]
    public async Task Obtener_SoloConsultaLosAnimesQueFaltan()
    {
        _db.Setup(d => d.ObtenerMarcasPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([new PersonajesAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow }]);
        RespondeJson(() => Respuesta(2));

        await _sut.ObtenerAsync([1, 2]);

        _peticiones.Should().ContainSingle();
        _peticiones[0].Should().Contain("[2]").And.NotContain("[1,2]");
    }

    [Fact]
    public async Task Obtener_AgrupaLasConsultasEnLotesDeCinco()
    {
        _responder = peticion => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Respuesta(Enumerable.Range(1, 12).ToArray())) // devuelve de más: se ignoran los no pedidos
        };

        var r = await _sut.ObtenerAsync(Enumerable.Range(1, 7).ToList());

        _peticiones.Should().HaveCount(2, "7 animes = un lote de 5 y otro de 2");
        r.Keys.Should().BeEquivalentTo(Enumerable.Range(1, 7));
    }

    [Fact]
    public async Task Obtener_SinRed_DevuelveLaCopiaVieja_YNoGuardaNiMarcaNada()
    {
        var viejo = new PersonajeAnime { AnimeId = 1, PersonajeId = 5, Nombre = "Viejo" };
        _db.Setup(d => d.ObtenerPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync([viejo]);
        _db.Setup(d => d.ObtenerMarcasPersonajesAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([new PersonajesAnimeSync { AnimeId = 1, FechaUtc = DateTime.UtcNow.AddDays(-90) }]);
        _responder = _ => throw new HttpRequestException("sin conexión");

        var r = await _sut.ObtenerAsync([1, 2]);

        r[1].Should().ContainSingle().Which.Nombre.Should().Be("Viejo");
        r.Should().NotContainKey(2);
        _db.Verify(d => d.GuardarPersonajesAsync(It.IsAny<IReadOnlyDictionary<int, List<PersonajeAnime>>>()), Times.Never,
            "un fallo no debe marcar el anime como consultado");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Obtener_ConRespuestaDeError_NoGuarda(HttpStatusCode codigo)
    {
        _responder = _ => new HttpResponseMessage(codigo);

        var r = await _sut.ObtenerAsync([1]);

        r.Should().BeEmpty();
        _db.Verify(d => d.GuardarPersonajesAsync(It.IsAny<IReadOnlyDictionary<int, List<PersonajeAnime>>>()), Times.Never);
    }

    [Fact]
    public async Task Obtener_ConErroresGraphQl_NoGuarda()
    {
        RespondeJson(() => "{\"errors\":[{\"message\":\"boom\"}],\"data\":null}");

        (await _sut.ObtenerAsync([1])).Should().BeEmpty();
        _db.Verify(d => d.GuardarPersonajesAsync(It.IsAny<IReadOnlyDictionary<int, List<PersonajeAnime>>>()), Times.Never);
    }

    [Fact]
    public async Task Obtener_IgnoraIdsInvalidosYNoConsultaSiNoQuedaNinguno()
    {
        var r = await _sut.ObtenerAsync([0, -3]);

        r.Should().BeEmpty();
        _peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task Obtener_SiSeCancela_PropagaLaCancelacion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> f = () => _sut.ObtenerAsync([1], cts.Token);

        await f.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void DebeConsultar_DependeDeLaVigenciaDeUnMes()
    {
        var ahora = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        PersonajesService.DebeConsultar(new PersonajesAnimeSync { FechaUtc = ahora.AddDays(-29) }, ahora).Should().BeFalse();
        PersonajesService.DebeConsultar(new PersonajesAnimeSync { FechaUtc = ahora.AddDays(-31) }, ahora).Should().BeTrue();
        // sqlite-net devuelve Kind = Unspecified: se trata como UTC
        PersonajesService.DebeConsultar(new PersonajesAnimeSync { FechaUtc = DateTime.SpecifyKind(ahora.AddDays(-1), DateTimeKind.Unspecified) }, ahora).Should().BeFalse();
    }

    // === Imágenes ===

    private static PersonajeAnime ConImagen(string url = "https://s4.anilist.co/file/anilistcdn/character/large/b1.png") =>
        new() { PersonajeId = 1, Nombre = "Light", ImagenUrl = url };

    [Fact]
    public async Task ObtenerImagen_LaDescargaUnaVezYLaReutilizaDeDisco()
    {
        _responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngValido) };

        string? ruta = await _sut.ObtenerImagenAsync(ConImagen());
        string? otra = await _sut.ObtenerImagenAsync(ConImagen());

        ruta.Should().NotBeNull();
        File.ReadAllBytes(ruta!).Should().Equal(PngValido);
        Path.GetDirectoryName(ruta).Should().Be(_carpeta, "las pruebas nunca deben escribir en AppDataPaths");
        otra.Should().Be(ruta);
        _peticiones.Should().ContainSingle("la segunda vez sale del disco");
        Directory.GetFiles(_carpeta, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://evil.example.com/b1.png")]
    [InlineData("http://s4.anilist.co/file/b1.png")]
    [InlineData("")]
    public async Task ObtenerImagen_RechazaHostsNoPermitidos(string url)
    {
        (await _sut.ObtenerImagenAsync(ConImagen(url))).Should().BeNull();
        _peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ObtenerImagen_RechazaContenidoQueNoEsUnaImagen()
    {
        _responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>no soy una imagen</html>") };

        (await _sut.ObtenerImagenAsync(ConImagen())).Should().BeNull();
        Directory.Exists(_carpeta).Should().BeFalse("no se escribe nada a disco si los bytes no son una imagen");
    }

    [Fact]
    public async Task ObtenerImagen_ConErrorDeRed_DevuelveNull()
    {
        _responder = _ => throw new HttpRequestException("caído");

        (await _sut.ObtenerImagenAsync(ConImagen())).Should().BeNull();
    }

    [Fact]
    public async Task ObtenerImagen_ConLaCarpetaLlena_BorraLasMasAntiguasYConservaLaNueva()
    {
        _sut.MaximoImagenesEnCache = 3;
        Directory.CreateDirectory(_carpeta);
        for (int i = 1; i <= 4; i++)
        {
            string vieja = Path.Combine(_carpeta, $"{100 + i}.jpg");
            File.WriteAllBytes(vieja, PngValido);
            File.SetLastWriteTimeUtc(vieja, DateTime.UtcNow.AddDays(-10 + i)); // 101 es la más antigua
        }
        _responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngValido) };

        string? nueva = await _sut.ObtenerImagenAsync(ConImagen());

        var quedan = Directory.GetFiles(_carpeta, "*.jpg").Select(Path.GetFileName).ToList();
        quedan.Should().HaveCount(3);
        quedan.Should().Contain(Path.GetFileName(nueva)).And.Contain(["104.jpg", "103.jpg"]);
        quedan.Should().NotContain("101.jpg").And.NotContain("102.jpg");
    }
}
