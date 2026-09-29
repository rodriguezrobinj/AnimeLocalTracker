using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Microsoft.Extensions.Http;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// La lista de openings/endings guardada en disco: sale sin preguntar a AnimeThemes si es reciente, y si AnimeThemes no
/// responde se usa la última conocida (la música sigue en la ficha sin conexión). Carpeta temporal propia; cada prueba usa
/// su propio AniList ID porque la caché en memoria del servicio es estática.
/// </summary>
public class AnimeThemesCatalogoDiscoTests : IDisposable
{
    private const string RecursoJson = """{"resources":[{"anime":[{"slug":"x"}]}]}""";
    private const string AnimeJson = """
        {"anime":{"slug":"x","animethemes":[
          {"slug":"OP1","type":"OP","song":{"title":"Nueva"},"animethemeentries":[{"version":1,"episodes":"1-12","videos":[{"audio":{"link":"https://a.animethemes.moe/op.ogg"}}]}]},
          {"slug":"ED1","type":"ED","song":{"title":"Final"},"animethemeentries":[{"version":1,"episodes":"1-12","videos":[{"audio":{"link":"https://a.animethemes.moe/ed.ogg"}}]}]}]}}
        """;

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Catalogo_{Guid.NewGuid():N}");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_carpeta)) Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private sealed class Manejador(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Manejador ApiQueResponde() =>
        new(r => Json(r.RequestUri!.AbsolutePath.StartsWith("/resource") ? RecursoJson : AnimeJson));

    private static Manejador ApiCaida() => new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

    private AnimeThemesService CrearSut(Manejador manejador) =>
        new(new HttpClient(new PolicyHttpMessageHandler(AnimeThemesService.CrearPoliticaReintentos(TimeSpan.FromMilliseconds(1)))
        {
            InnerHandler = manejador
        }), _carpeta);

    private static int IdUnico() => 800_000_000 + Random.Shared.Next(1, 90_000_000);

    private void Guardar(int id, DateTime guardadoUtc, params string[] titulos)
    {
        var temas = new List<AnimeThemeInfo>();
        foreach (var titulo in titulos)
            temas.Add(new AnimeThemeInfo { Slug = "OP" + (temas.Count + 1), Tipo = "OP", TituloCancion = titulo, AudioUrlOgg = "https://a.animethemes.moe/v.ogg" });

        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Path.Combine(_carpeta, id + ".json"),
            JsonSerializer.Serialize(new AnimeThemesService.CatalogoGuardado(AnimeThemesService.FormatoCatalogo, guardadoUtc, temas)));
    }

    [Fact]
    public async Task UnaListaRecienGuardada_DeberiaUsarseSinPreguntarAAnimeThemes()
    {
        int id = IdUnico();
        Guardar(id, DateTime.UtcNow.AddHours(-1), "Guardada");
        var api = ApiQueResponde();

        var temas = await CrearSut(api).ObtenerTemasAsync(id);

        temas.Should().ContainSingle().Which.TituloCancion.Should().Be("Guardada");
        api.Urls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnaListaVieja_DeberiaRenovarseYGuardarseLaNueva()
    {
        int id = IdUnico();
        Guardar(id, DateTime.UtcNow.AddHours(-13), "Vieja");
        var api = ApiQueResponde();

        var temas = await CrearSut(api).ObtenerTemasAsync(id);

        temas.Should().HaveCount(2);
        temas[0].TituloCancion.Should().Be("Nueva");
        File.ReadAllText(Path.Combine(_carpeta, id + ".json")).Should().Contain("Nueva").And.NotContain("Vieja");
    }

    [Fact]
    public async Task SinConexion_DeberiaUsarLaUltimaListaConocidaAunqueSeaVieja()
    {
        int id = IdUnico();
        Guardar(id, DateTime.UtcNow.AddDays(-30), "De hace un mes");

        var temas = await CrearSut(ApiCaida()).ObtenerTemasAsync(id);

        temas.Should().ContainSingle().Which.TituloCancion.Should().Be("De hace un mes");
    }

    [Fact]
    public async Task SinConexionYSinNadaGuardado_DeberiaDevolverVacioSinCrearArchivos()
    {
        int id = IdUnico();

        (await CrearSut(ApiCaida()).ObtenerTemasAsync(id)).Should().BeEmpty();

        File.Exists(Path.Combine(_carpeta, id + ".json")).Should().BeFalse();
    }

    [Fact]
    public async Task UnaRespuestaCompleta_DeberiaGuardarseEnDisco()
    {
        int id = IdUnico();

        await CrearSut(ApiQueResponde()).ObtenerTemasAsync(id);

        string ruta = Path.Combine(_carpeta, id + ".json");
        File.Exists(ruta).Should().BeTrue();
        var guardado = JsonSerializer.Deserialize<AnimeThemesService.CatalogoGuardado>(File.ReadAllText(ruta));
        guardado!.Temas.Should().HaveCount(2);
        guardado.Temas[0].AudioUrlOgg.Should().Be("https://a.animethemes.moe/op.ogg");
        Directory.GetFiles(_carpeta, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task UnArchivoGuardadoIlegible_DeberiaIgnorarseYPreguntarALaApi()
    {
        int id = IdUnico();
        Directory.CreateDirectory(_carpeta);
        File.WriteAllText(Path.Combine(_carpeta, id + ".json"), "{ esto no es json");

        (await CrearSut(ApiQueResponde()).ObtenerTemasAsync(id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task SinCarpeta_NoDeberiaEscribirNadaEnDisco()
    {
        var sut = new AnimeThemesService(new HttpClient(ApiQueResponde()));

        (await sut.ObtenerTemasAsync(IdUnico())).Should().HaveCount(2);
        Directory.Exists(_carpeta).Should().BeFalse();
    }

    [Fact]
    public async Task LasPeticiones_DeberianPedirSoloLosCamposQueSeUsan()
    {
        var api = ApiQueResponde();

        await CrearSut(api).ObtenerTemasAsync(IdUnico());

        api.Urls.Should().HaveCount(2);
        api.Urls[0].Should().Contain("fields[anime]=slug");
        api.Urls[1].Should().Contain("fields[audio]=link").And.Contain("fields[animethemeentry]=version,episodes,spoiler");
    }
}
