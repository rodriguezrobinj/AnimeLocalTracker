using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Bucle de búsqueda de <see cref="NyaaSourceService"/>: prueba los términos generados
/// a partir de los títulos conocidos (mismo generador que usa AnimeAV1) hasta que uno
/// encuentra un release de un solo episodio con semillas suficientes.
/// </summary>
public class NyaaSourceServiceTests
{
    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string[] TitulosFrieren = { "Frieren" };
    private static readonly string[] TitulosFrierenCompleto = { "Frieren: Beyond Journey's End" };
    private static readonly string[] TitulosInexistente = { "Anime Inexistente" };

    private const string FixtureConEpisodio13 = """
        <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
        	<channel>
        		<item>
        			<title>[SubsPlease] Frieren - 13 (1080p) [CEC8715E].mkv</title>
        			<link>https://nyaa.si/download/2000001.torrent</link>
        			<nyaa:seeders>500</nyaa:seeders>
        			<nyaa:infoHash>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</nyaa:infoHash>
        			<nyaa:size>1.3 GiB</nyaa:size>
        		</item>
        	</channel>
        </rss>
        """;

    // Fase 2a: un batch con semillas SUFICIENTES ya no cuenta como "sin candidatos
    // válidos" (cae al batch como fallback) — para forzar que este término no aporte
    // nada de verdad, el batch tiene menos semillas que el mínimo.
    private const string FixtureSinCandidatosValidos = """
        <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
        	<channel>
        		<item>
        			<title>[Group] Frieren Batch (01-28) [Complete]</title>
        			<link>https://nyaa.si/download/2000002.torrent</link>
        			<nyaa:seeders>1</nyaa:seeders>
        			<nyaa:infoHash>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</nyaa:infoHash>
        			<nyaa:size>10 GiB</nyaa:size>
        		</item>
        	</channel>
        </rss>
        """;

    private const string FixtureVacia = """
        <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0"><channel></channel></rss>
        """;

    // Varios candidatos válidos del mismo episodio (Fase 2d: el selector manual debe
    // verlos TODOS, no solo el de más semillas).
    private const string FixtureConVariosCandidatos = """
        <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
        	<channel>
        		<item>
        			<title>[SubsPlease] Frieren - 13 (1080p) [CEC8715E].mkv</title>
        			<link>https://nyaa.si/download/3000001.torrent</link>
        			<nyaa:seeders>500</nyaa:seeders>
        			<nyaa:infoHash>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</nyaa:infoHash>
        			<nyaa:size>1.3 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[Erai-raws] Frieren - 13 [1080p][MultiSub]</title>
        			<link>https://nyaa.si/download/3000002.torrent</link>
        			<nyaa:seeders>200</nyaa:seeders>
        			<nyaa:infoHash>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</nyaa:infoHash>
        			<nyaa:size>1.4 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[ASW] Frieren - 13 [720p]</title>
        			<link>https://nyaa.si/download/3000003.torrent</link>
        			<nyaa:seeders>50</nyaa:seeders>
        			<nyaa:infoHash>cccccccccccccccccccccccccccccccccccccccc</nyaa:infoHash>
        			<nyaa:size>700 MiB</nyaa:size>
        		</item>
        	</channel>
        </rss>
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private static HttpResponseMessage Ok(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    private static NyaaSourceService Crear(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)));

    [Fact]
    public async Task BuscarEpisodioAsync_ConCandidatoValido_DeberiaDevolverlo()
    {
        var servicio = Crear(req =>
        {
            req.RequestUri!.Host.Should().Be("nyaa.si");
            req.RequestUri!.Query.Should().Contain("page=rss");
            return Ok(FixtureConEpisodio13);
        });

        var resultado = await servicio.BuscarEpisodioAsync(TitulosFrieren, 13);

        resultado.Should().NotBeNull();
        resultado!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/2000001.torrent");
        resultado.Value.Seeders.Should().Be(500);
    }

    [Fact]
    public async Task BuscarEpisodioAsync_PrimerTerminoSinCandidatosValidos_DeberiaProbarElSiguiente()
    {
        int intentos = 0;
        var servicio = Crear(req =>
        {
            intentos++;
            // El primer término (título completo) solo tiene el batch; el
            // segundo (variación más corta) sí trae el episodio suelto.
            return intentos == 1 ? Ok(FixtureSinCandidatosValidos) : Ok(FixtureConEpisodio13);
        });

        var resultado = await servicio.BuscarEpisodioAsync(TitulosFrierenCompleto, 13);

        resultado.Should().NotBeNull();
        intentos.Should().BeGreaterThan(1, "debió reintentar con otro término tras el primero sin candidatos válidos");
    }

    [Fact]
    public async Task BuscarEpisodioAsync_SinNingunTerminoConResultados_DeberiaDevolverNull()
    {
        var servicio = Crear(req => Ok(FixtureVacia));

        var resultado = await servicio.BuscarEpisodioAsync(TitulosInexistente, 1);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task BuscarEpisodioAsync_ConRespuestaHttpFallida_DeberiaSeguirConElSiguienteTerminoSinLanzar()
    {
        var servicio = Crear(req => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = async () => await servicio.BuscarEpisodioAsync(TitulosFrieren, 13);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BuscarCandidatosAsync_DeberiaDevolverTodosLosCandidatosValidosOrdenadosPorSemillas()
    {
        var servicio = Crear(req => Ok(FixtureConVariosCandidatos));

        var candidatos = await servicio.BuscarCandidatosAsync(TitulosFrieren, 13);

        candidatos.Should().HaveCount(3);
        candidatos.Select(c => c.TorrentUrl).Should().ContainInOrder(
            "https://nyaa.si/download/3000001.torrent", // SubsPlease, 500
            "https://nyaa.si/download/3000002.torrent", // Erai-raws, 200
            "https://nyaa.si/download/3000003.torrent"); // ASW, 50
    }

    [Fact]
    public async Task BuscarCandidatosAsync_SinNingunTerminoConResultados_DeberiaDevolverListaVacia()
    {
        var servicio = Crear(req => Ok(FixtureVacia));

        var candidatos = await servicio.BuscarCandidatosAsync(TitulosInexistente, 1);

        candidatos.Should().BeEmpty();
    }

    [Fact]
    public async Task BuscarEpisodioAsync_DeberiaSerElPrimeroDeBuscarCandidatosAsync()
    {
        var servicio = Crear(req => Ok(FixtureConVariosCandidatos));

        var mejor = await servicio.BuscarEpisodioAsync(TitulosFrieren, 13);
        var todos = await servicio.BuscarCandidatosAsync(TitulosFrieren, 13);

        mejor.Should().NotBeNull();
        mejor!.Value.TorrentUrl.Should().Be(todos[0].TorrentUrl);
    }
}
