using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

// Los títulos reales de cada caso van en línea para que el test se lea solo (CA1861 pide campos estáticos).
#pragma warning disable CA1861

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Películas (numeradas por el sitio según su posición en la franquicia) y temporadas partidas en varias
/// partes (numeración continua en Nyaa). Casos reales que fallaban en la app del usuario.
/// </summary>
public class PeliculasYPartesTests
{
    private static readonly PrecuelaAnime[] PrecuelasReZeroS2P2 =
    {
        new(39587, 13, new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", "Re:ZERO -Starting Life in Another World- Season 2" }),
        new(31240, 25, new[] { "Re:Zero kara Hajimeru Isekai Seikatsu", "Re:ZERO -Starting Life in Another World-" }),
    };

    private static readonly string[] TitulosReZeroS2P2 = { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2", "Re:ZERO -Starting Life in Another World- Season 2 Part 2" };

    // ── Comparador: películas ──

    [Theory]
    [InlineData("Dragon Ball Z: Battle of Gods", "Dragon Ball Z Película 14: Battle of Gods", true)]
    [InlineData("Dragon Ball Z: Return my Gohan!!", "Dragon Ball Z: Movie 01 - Return my Gohan!!", true)]
    [InlineData("One Piece Film 10", "One Piece - Movie 10", true)]
    [InlineData("One Piece Film 15", "One Piece", false)]            // nuestro alternativo no es la serie
    [InlineData("Mairimashita! Iruma-kun Movie", "Mairimashita! Iruma-kun", false)]
    [InlineData("Dragon Ball Z", "Dragon Ball Z Movie 14", false)]    // película sin subtítulo: no es la serie
    [InlineData("One Piece Film 10", "One Piece Movie 13", false)]
    public void FirmaTitulo_Peliculas(string nuestro, string suyo, bool coincide)
    {
        (FirmaTitulo.Evaluar([nuestro], [suyo]).MismaTemporada >= 0.8).Should().Be(coincide);
    }

    // ── AnimeAv1: número de episodio ──

    [Fact]
    public void ResolverNumeroEpisodio_ListaQueNoEmpiezaEn1_SeUsaLaPosicion()
    {
        var parte2Continua = new AnimeAv1VideoSourceResolver.InfoMedia("p2", 1, "P2", new List<string>(),
            Enumerable.Range(13, 12).Select(n => (n, n)).ToList(), "");

        AnimeAv1VideoSourceResolver.ResolverNumeroEpisodio(parte2Continua, 1).Should().Be(13);
        AnimeAv1VideoSourceResolver.ResolverNumeroEpisodio(parte2Continua, 12).Should().Be(24);
        AnimeAv1VideoSourceResolver.ResolverNumeroEpisodio(parte2Continua, 13).Should().Be(13, "el número exacto manda");
    }

    // ── AnimeAv1: búsqueda con sitio simulado ──

    private sealed class SitioFalso : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;
        public readonly ConcurrentQueue<string> Pedidas = new();
        public SitioFalso(Func<string, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Pedidas.Enqueue(url);
            return Task.FromResult(_responder(url));
        }
    }

    private static HttpResponseMessage Ok(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    private static string Catalogo(params (string Slug, string Titulo)[] resultados) =>
        "<script>{type:\"data\",data:{results:[" +
        string.Join(",", resultados.Select((r, i) => $"{{id:\"{i}\",title:\"{r.Titulo}\",synopsis:\"...\",categoryId:1,slug:\"{r.Slug}\",category:a}}")) +
        "],genres:[{id:1,name:\"Acción\",slug:\"accion\",malId:1}]}}</script>";

    private static string Media(string slug, string titulo, int? malId, params int[] episodios) =>
        $"<script>{{data:{{media:{{id:1,title:\"{titulo}\",aka:{{}},votes:1,slug:\"{slug}\",malId:{(malId?.ToString() ?? "null")},episodes:[" +
        string.Join(",", episodios.Select(n => $"{{id:{n},number:{n}}}")) + "],relations:[]}}}}</script>";

    private static string Episodio(string slug, int? malId, int n) =>
        $"<script>{{data:{{media:{{id:1,votes:1,slug:\"{slug}\",malId:{(malId?.ToString() ?? "null")}}},episode:{{number:{n}}}," +
        $"embeds:{{SUB:[{{server:\"MP4Upload\",url:\"https://www.mp4upload.com/embed-{slug}{n}.html\"}}]}}}}}}</script>";

    [Fact]
    public async Task Pelicula_SinMalId_SeReconocePorNombreAunqueElSitioLaNumere()
    {
        // "Dragon Ball Z: Kami to Kami" en el sitio es "Dragon Ball Z Película 14: Battle of Gods", episodio 14.
        const string slug = "dragon-ball-z-movie-14-kami-to-kami";
        var sitio = new SitioFalso(url =>
        {
            if (url.Contains("/catalogo")) return Ok(Catalogo(("saint-seiya-battle-sanctuary", "Saint Seiya: Knights of the Zodiac - Battle Sanctuary"), (slug, "Dragon Ball Z Película 14: Battle of Gods")));
            if (url.EndsWith("/media/" + slug)) return Ok(Media(slug, "Dragon Ball Z Película 14: Battle of Gods", null, 14));
            if (url.EndsWith("/media/" + slug + "/14")) return Ok(Episodio(slug, null, 14));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio));

        var embeds = await resolver.ObtenerEmbedsEpisodioAsync(new[] { "Dragon Ball Z: Kami to Kami", "Dragon Ball Z: Battle of Gods" }, 1, aniListId: 14837);

        embeds.Should().ContainSingle().Which.Url.Should().Contain(slug + "14");
        sitio.Pedidas.Should().NotContain(u => u.EndsWith("/media/saint-seiya-battle-sanctuary"), "la película se parece más y va antes");
    }

    [Fact]
    public async Task ParteJuntadaEnElSitio_SeRevisaAntesQueAnimesAjenos()
    {
        // En la app real el catálogo devolvía Re:Monster, Tokyo Ghoul:re… antes que "Re:Zero 2nd Season", y con
        // los títulos chinos/coreanos del anime estos parecían "temporada 1": la página correcta quedaba fuera.
        const string correcta = "rezero-kara-hajimeru-isekai-seikatsu-2nd-season";
        var sitio = new SitioFalso(url =>
        {
            if (url.Contains("/catalogo")) return Ok(Catalogo(("remonster", "Re:Monster"), ("tokyo-ghoulre", "Tokyo Ghoul:re"), (correcta, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season")));
            if (url.EndsWith("/media/" + correcta)) return Ok(Media(correcta, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", 39587, Enumerable.Range(1, 25).ToArray()));
            if (url.EndsWith("/media/" + correcta + "/14")) return Ok(Episodio(correcta, 39587, 14));
            if (url.EndsWith("/media/remonster")) return Ok(Media("remonster", "Re:Monster", 56690, 1));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var resolver = new AnimeAv1VideoSourceResolver(new HttpClient(sitio),
            malIdResolver: (_, _) => Task.FromResult<int?>(42203),
            precuelas: (_, _) => Task.FromResult<IReadOnlyList<PrecuelaAnime>>(PrecuelasReZeroS2P2));

        var embeds = await resolver.ObtenerEmbedsEpisodioAsync(
            TitulosReZeroS2P2.Concat(new[] { "Re:从零开始的异世界生活第二季（下半）", "Re: 제로부터 시작하는 이세계 생활 2기 파트 2" }), 1, aniListId: 119661);

        embeds.Should().ContainSingle().Which.Url.Should().Contain(correcta + "14");
        var pedidas = sitio.Pedidas.ToList();
        pedidas.FindIndex(u => u.EndsWith("/media/" + correcta)).Should()
            .BeLessThan(pedidas.FindIndex(u => u.EndsWith("/media/remonster")), "la misma serie (otra parte) va antes que un anime ajeno");
    }

    // ── Nyaa: numeración continua ──

    private static bool Continuo(string release, int episodio)
        => NyaaRssParser.EsEpisodioContinuo(NyaaRssParser.AnalizarRelease(release), episodio, PrecuelasReZeroS2P2);

    [Theory]
    [InlineData("[SubsPlease] Re Zero kara Hajimeru Isekai Seikatsu - 39 (1080p) [26C6BE62].mkv", 1, true)]   // absoluta: 25 + 13 + 1
    [InlineData("[EMBER] Re:Zero kara Hajimeru Isekai Seikatsu S02E14 [Episode-39] [1080p] [HEVC WEBRip]", 1, true)] // dentro de la temporada: 13 + 1
    [InlineData("[EMBER] Re:Zero kara Hajimeru Isekai Seikatsu S02E18 [Episode-43] [1080p] [HEVC WEBRip]", 5, true)]
    [InlineData("[EMBER] Re:Zero kara Hajimeru Isekai Seikatsu S02E14 [Episode-39] [1080p] [HEVC WEBRip]", 2, false)]
    [InlineData("[SubsPlease] Re Zero kara Hajimeru Isekai Seikatsu - 14 (1080p).mkv", 1, false)]            // episodio 14 de la temporada 1
    public void EsEpisodioContinuo_ReZeroS2P2(string release, int episodio, bool esperado)
        => Continuo(release, episodio).Should().Be(esperado);

    [Fact]
    public void AlternativoConOtraTemporadaQueLaDelRelease_SeIgnora()
    {
        // Etiqueta equivocada del uploader: es la temporada 3 aunque diga "2nd Season Part 2" entre paréntesis.
        var release = NyaaRssParser.AnalizarRelease("Re ZERO Starting Life in Another World S03E01 Theatrical Malice 1080p BILI WEB-DL AAC2.0 H 264-VARYG (Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2, Multi-Subs)");

        NyaaRssParser.EsDelAnime(release, TitulosReZeroS2P2).Should().BeFalse();
    }

    [Fact]
    public void FiltrarYOrdenar_NumeracionContinua_CompiteConLosDeNumeracionPropia()
    {
        var candidatos = new List<CandidatoTorrent>
        {
            new("[Erai-raws] Re.Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2 - 05 [1080p][Multiple Subtitle].mkv", "https://nyaa.si/download/1.torrent", "", 3, 1),
            new("[EMBER] Re:Zero kara Hajimeru Isekai Seikatsu S02E18 [Episode-43] [1080p] [HEVC WEBRip]", "https://nyaa.si/download/2.torrent", "", 31, 1),
            new("[SubsPlease] Re Zero kara Hajimeru Isekai Seikatsu - 05 (1080p).mkv", "https://nyaa.si/download/3.torrent", "", 90, 1),
        };

        var elegidos = NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, TitulosReZeroS2P2, 5, 3, precuelas: PrecuelasReZeroS2P2);

        elegidos.Select(c => c.TorrentUrl).Should().Equal("https://nyaa.si/download/2.torrent", "https://nyaa.si/download/1.torrent");
    }

    [Fact]
    public void NumerosContinuos_UnoPorCadaParteAnterior()
    {
        NyaaRssParser.NumerosContinuos(1, PrecuelasReZeroS2P2).Should().Equal(14, 39);
    }

    // ── Nyaa: películas ──

    [Fact]
    public void FiltrarYOrdenar_PeliculaSinNumeroDeEpisodio_SeAceptaParaElEpisodio1()
    {
        var candidatos = new List<CandidatoTorrent>
        {
            new("[CBM] Dragon Ball Z - Battle of Gods (Directors Cut) (Dual Audio) [BDRip 1080p x265 10bit]", "https://nyaa.si/download/1.torrent", "", 4, 1),
            new("[HorribleSubs] Dragon Ball Z (01-291) [1080p] (Batch)", "https://nyaa.si/download/2.torrent", "", 50, 1),
        };
        string[] titulos = { "Dragon Ball Z: Kami to Kami", "Dragon Ball Z: Battle of Gods", "Dragon Ball Z Movie 14: God & God" };

        NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, titulos, 1, 3).Select(c => c.TorrentUrl)
            .Should().Equal("https://nyaa.si/download/1.torrent");
        NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, titulos, 2, 3).Should().BeEmpty();
    }

    [Fact]
    public void FiltrarYOrdenar_PeliculaDeOnePiece_NoAceptaElPackDeLaSerie()
    {
        var candidatos = new List<CandidatoTorrent>
        {
            new("[HorribleSubs] One Piece (01-900) [1080p] (Batch)", "https://nyaa.si/download/1.torrent", "", 200, 1),
            new("[EMBER] One Piece Film: Red (2022) (Movie) [BDRip] [1080p Dual Audio HEVC 10 bits DD]", "https://nyaa.si/download/2.torrent", "", 40, 1),
        };

        NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, new[] { "ONE PIECE FILM: RED", "One Piece Film: Red", "One Piece Film 15" }, 1, 3)
            .Select(c => c.TorrentUrl).Should().Equal("https://nyaa.si/download/2.torrent");
    }
}
