using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Microsoft.Extensions.Http;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// AnimeThemes cuando la API falla: reintentos cortos (no los 60 s de AniList) y un error pasajero nunca se recuerda como
/// "este anime no tiene openings". Sin red real: un manejador falso responde en orden. Cada prueba usa su propio AniList ID
/// porque la caché del servicio es estática.
/// </summary>
public class AnimeThemesServiceResilienciaTests
{
    private const string AnimeJson = """
        {"anime":{"slug":"x","animethemes":[{"slug":"OP1","type":"OP","song":{"title":"Canción"},
         "animethemeentries":[{"version":1,"episodes":"1-","videos":[{"audio":{"link":"https://a.animethemes.moe/x.ogg"}}]}]}]}}
        """;

    private const string RecursoJson = """{"resources":[{"anime":[{"slug":"x"}]}]}""";
    private const string SinRecursosJson = """{"resources":[]}""";

    private sealed class ManejadorEnOrden : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _respuestas = new();
        public List<string> Urls { get; } = [];

        public ManejadorEnOrden Luego(HttpStatusCode codigo, string? json = null, TimeSpan? retryAfter = null)
        {
            _respuestas.Enqueue(() =>
            {
                var r = new HttpResponseMessage(codigo) { Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json") };
                if (retryAfter != null) r.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
                return r;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            if (_respuestas.Count == 0) throw new InvalidOperationException("Petición inesperada: " + request.RequestUri);
            return Task.FromResult(_respuestas.Dequeue()());
        }
    }

    private static AnimeThemesService CrearSut(ManejadorEnOrden manejador)
    {
        var conPolitica = new PolicyHttpMessageHandler(AnimeThemesService.CrearPoliticaReintentos(TimeSpan.FromMilliseconds(1)))
        {
            InnerHandler = manejador
        };
        return new AnimeThemesService(new HttpClient(conPolitica));
    }

    private static int IdUnico() => 900_000_000 + Random.Shared.Next(1, 90_000_000);

    [Fact]
    public async Task UnErrorDelServidor_DeberiaReintentarseEnSegundosYDevolverLosTemas()
    {
        var manejador = new ManejadorEnOrden()
            .Luego(HttpStatusCode.InternalServerError)
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.OK, AnimeJson);

        var temas = await CrearSut(manejador).ObtenerTemasAsync(IdUnico());

        temas.Should().ContainSingle().Which.RangoEpisodios.Should().Be("1-");
        manejador.Urls.Should().HaveCount(3);
    }

    [Fact]
    public async Task SiElIncludeDeArtistasFallaSiempre_DeberiaUsarElRespaldoSinArtistas()
    {
        // Antes la política de AniList esperaba 60 s ante el 500 y el HttpClient cortaba a los 20: el respaldo no llegaba nunca.
        var manejador = new ManejadorEnOrden()
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.InternalServerError).Luego(HttpStatusCode.InternalServerError).Luego(HttpStatusCode.InternalServerError)
            .Luego(HttpStatusCode.OK, AnimeJson);

        var temas = await CrearSut(manejador).ObtenerTemasAsync(IdUnico());

        temas.Should().ContainSingle();
        manejador.Urls[^1].Should().NotContain("performances");
    }

    [Fact]
    public async Task UnBloqueoDeLaApi_NoDeberiaRecordarseComoAnimeSinTemas()
    {
        int id = IdUnico();
        var manejador = new ManejadorEnOrden()
            .Luego(HttpStatusCode.Forbidden) // Cloudflare
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.OK, AnimeJson);
        var sut = CrearSut(manejador);

        (await sut.ObtenerTemasAsync(id)).Should().BeEmpty();
        (await sut.ObtenerTemasAsync(id)).Should().ContainSingle("la segunda vez se vuelve a preguntar en vez de esperar 6 horas");
    }

    [Fact]
    public async Task UnAnimeQueDeVerdadNoEstaEnAnimeThemes_SiDeberiaRecordarse()
    {
        int id = IdUnico();
        var manejador = new ManejadorEnOrden().Luego(HttpStatusCode.OK, SinRecursosJson);
        var sut = CrearSut(manejador);

        (await sut.ObtenerTemasAsync(id)).Should().BeEmpty();
        (await sut.ObtenerTemasAsync(id)).Should().BeEmpty();

        manejador.Urls.Should().ContainSingle("no hace falta volver a preguntar por algo que no existe");
    }

    [Fact]
    public async Task SiFallanTodosLosAnimes_NoDeberiaRecordarseNada()
    {
        int id = IdUnico();
        var manejador = new ManejadorEnOrden()
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.NotFound).Luego(HttpStatusCode.NotFound)
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.OK, AnimeJson);
        var sut = CrearSut(manejador);

        (await sut.ObtenerTemasAsync(id)).Should().BeEmpty();
        (await sut.ObtenerTemasAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnLimiteDePeticionesConEsperaLarga_NoDeberiaQuedarseEsperando()
    {
        var manejador = new ManejadorEnOrden().Luego(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(60));

        var temas = await CrearSut(manejador).ObtenerTemasAsync(IdUnico());

        temas.Should().BeEmpty();
        manejador.Urls.Should().ContainSingle();
    }

    [Fact]
    public async Task UnLimiteDePeticionesConEsperaCorta_DeberiaReintentarse()
    {
        var manejador = new ManejadorEnOrden()
            .Luego(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMilliseconds(10))
            .Luego(HttpStatusCode.OK, RecursoJson)
            .Luego(HttpStatusCode.OK, AnimeJson);

        (await CrearSut(manejador).ObtenerTemasAsync(IdUnico())).Should().ContainSingle();
    }

    [Fact]
    public void EsLimiteDePeticionesReintentable_SinCabeceraRetryAfter_DeberiaReintentarse()
    {
        AnimeThemesService.EsLimiteDePeticionesReintentable(new HttpResponseMessage(HttpStatusCode.TooManyRequests)).Should().BeTrue();
        AnimeThemesService.EsLimiteDePeticionesReintentable(new HttpResponseMessage(HttpStatusCode.OK)).Should().BeFalse();
    }
}
