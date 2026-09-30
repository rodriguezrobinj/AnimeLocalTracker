using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

// Los títulos reales de cada caso van en línea para que el test se lea solo (CA1861 pide campos estáticos).
#pragma warning disable CA1861

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Verificación de identidad de los releases de Nyaa. Todos los títulos salen de búsquedas reales:
/// antes la app elegía el primer release con el número de episodio pedido sin mirar de qué anime ni de
/// qué temporada era (Iruma-kun 2 → pack de la temporada 4; One Piece → packs de otros animes).
/// </summary>
public class NyaaIdentidadReleaseTests
{
    private static readonly string[] IrumaKun2 = { "Mairimashita! Iruma-kun 2", "Welcome to Demon School! Iruma-kun Season 2" };
    private static readonly string[] OtonariS1 = { "Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken", "The Angel Next Door Spoils Me Rotten" };
    private static readonly string[] OtonariS2 = { "Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken 2nd Season", "The Angel Next Door Spoils Me Rotten Season 2" };

    private static bool EsDe(string release, IEnumerable<string> titulos)
        => NyaaRssParser.EsDelAnime(NyaaRssParser.AnalizarRelease(release), titulos);

    [Theory]
    [InlineData("[SubsPlease] Mairimashita! Iruma-kun S2 - 05 (1080p) [75D6606E].mkv", true)]
    [InlineData("[Erai-raws] Mairimashita! Iruma-kun 2nd Season - 05 [1080p][Multiple Subtitle].mkv", true)]
    [InlineData("[AnoZu] Welcome to Demon School! Iruma-kun S04 1080p CR WEB-DL Dual-Audio DDP 2.0 H.264 | Mairimashita! Iruma-kun 4th Season", false)]
    [InlineData("[Erai-raws] Mairimashita Iruma-kun 4th Season - 05 [1080p CR WEBRip HEVC AAC][MultiSub][55951233]", false)]
    [InlineData("[ASW] Mairimashita! Iruma-kun S4 - 05 [1080p HEVC x265 10Bit][AAC]", false)]
    public void EsDelAnime_IrumaKun2(string release, bool esperado) => EsDe(release, IrumaKun2).Should().Be(esperado);

    [Theory]
    [InlineData("[SubsPlease] Shingeki no Kyojin - The Final Season Part 3 - 02 (1080p) [49B60365].mkv", false)]
    [InlineData("[LostYears] Attack on Titan S03E02 (39) (WEB 1080p Hi10 AAC) [Dual Audio] (Shingeki no Kyojin)", true)]
    public void EsDelAnime_ShingekiSeason3(string release, bool esperado)
        => EsDe(release, new[] { "Shingeki no Kyojin Season 3", "Attack on Titan Season 3" }).Should().Be(esperado);

    [Fact]
    public void EsDelAnime_MushokuII_NoAceptaLaTemporada3()
        => EsDe("[Erai-raws] Mushoku Tensei III: Isekai Ittara Honki Dasu - 13 [1080p CR WEB-DL AVC AAC][MultiSub][AE099987]",
                new[] { "Mushoku Tensei II: Isekai Ittara Honki Dasu", "Mushoku Tensei: Jobless Reincarnation Season 2" }).Should().BeFalse();

    [Fact]
    public void EsDelAnime_OnePiece_NoAceptaPacksDeOtrosAnimes()
        => EsDe("Mistress.Kanan.is.Devilishly.Easy.S01.1080p.BluRay.Dual-Audio.Opus.2.0.x265-YURASUKA (Kanan-sama wa Akumade Choroi)",
                new[] { "ONE PIECE", "OP" }).Should().BeFalse();

    [Fact]
    public void EsDelAnime_NombreAlternativoHeredaLaTemporadaDelRelease()
    {
        // El nombre romaji entre paréntesis no dice la temporada, pero el release es S02: no vale para la temporada 1.
        const string release = "[ToonsHub] The Angel Next Door Spoils Me Rotten S02E12 1080p CR WEB-DL AAC2.0 H.264 (Otonari no Tenshi-sama ni Itsunomanika Dame Ningen ni Sareteita Ken, English-Sub)";

        EsDe(release, OtonariS2).Should().BeTrue();
        EsDe(release, OtonariS1).Should().BeFalse();
    }

    [Fact]
    public void EsDelAnime_TemporadaConNombreDeArco_SeReconocePorElTituloInglesConNumero()
    {
        // El romaji de la temporada 3 no lleva número ("Shimetsu Kaiyuu - Zenpen"); el inglés sí
        // ("Season 3"), y es el que confirma que "Jujutsu Kaisen - S03E03" es esta temporada.
        const string release = "[Subeteka] Jujutsu Kaisen - S03E03 [1080p WEB DUAL DDP2.0 H.265] [67067855] | Jujutsu Kaisen: Shimetsu Kaiyuu - Zenpen";

        EsDe(release, new[] { "Jujutsu Kaisen: Shimetsu Kaiyuu - Zenpen", "JUJUTSU KAISEN Season 3: The Culling Game Part 1" }).Should().BeTrue();
        EsDe(release, new[] { "Jujutsu Kaisen", "JUJUTSU KAISEN" }).Should().BeFalse("es la temporada 3, no la 1");
    }

    [Fact]
    public void AnalizarRelease_PackConRangoConTilde_NoEsElEpisodio1()
    {
        var r = NyaaRssParser.AnalizarRelease("[Erai-raws] Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2 - 01 ~ 12 [1080p][Multiple Subtitles][Unofficial Batch]");

        r.EsBatch.Should().BeTrue();
        r.Episodio.Should().BeNull();
        (r.RangoDesde, r.RangoHasta).Should().Be((1, 12));
        NyaaRssParser.EsDelAnime(r, new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2" }).Should().BeTrue();
        NyaaRssParser.EsDelAnime(r, new[] { "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season" }).Should().BeFalse();
    }

    [Fact]
    public void AnalizarRelease_PackDeVariasTemporadas_SeMarca()
    {
        NyaaRssParser.AnalizarRelease("[Tenrai-Sensei] Demon Slayer: Kimetsu no Yaiba S1+S2+S3 + Movie [BD][1080p][HEVC 10bit x265][Dual Audio]")
            .VariasTemporadas.Should().BeTrue();
    }

    [Theory]
    [InlineData("[Yameii] Tomb Raider King - S01E03 [English Dub] [CR WEB-DL 720p H264 AAC] [66AD1C8B] (Dogul Wang)", true)]
    [InlineData("Tomb Raider King S01E03 One Suited for Domination 1080p CR WEB-DL DUAL AAC2.0 H.264-VARYG (Dogul Wang, Dual-Audio, Multi-Subs)", false)]
    public void AnalizarRelease_SoloDoblaje(string release, bool esperado)
        => NyaaRssParser.AnalizarRelease(release).SoloDoblaje.Should().Be(esperado);

    [Theory]
    [InlineData("[Group] Anime - 12.5 [1080p]", null)]
    [InlineData("[Group] Anime - 12 [1080p]", 12)]
    [InlineData("[Group] Anime EP12 [1080p]", 12)]
    [InlineData("One Piece - 1100 [1080p]", 1100)]
    public void ExtraerNumeroEpisodio_CasosNuevos(string titulo, int? esperado)
        => NyaaRssParser.ExtraerNumeroEpisodio(titulo).Should().Be(esperado);

    [Fact]
    public void FiltrarYOrdenar_ConTitulos_DescartaOtrasTemporadasYDejaElDoblajeAlFinal()
    {
        var candidatos = new List<CandidatoTorrent>
        {
            new("[Yameii] Tomb Raider King - S01E03 [English Dub] [CR WEB-DL 1080p H264 AAC] (Dogul Wang)", "https://nyaa.si/download/1.torrent", "", 300, 1),
            new("Tomb Raider King S01E03 1080p CR WEB-DL DUAL AAC2.0 H.264-VARYG (Dogul Wang, Dual-Audio, Multi-Subs)", "https://nyaa.si/download/2.torrent", "", 60, 1),
            new("Tomb Raider King S02E03 1080p CR WEB-DL (Dogul Wang)", "https://nyaa.si/download/3.torrent", "", 900, 1),
        };

        var elegidos = NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, new[] { "Dogul Wang", "Tomb Raider King" }, 3, 3);

        elegidos.Select(c => c.TorrentUrl).Should().Equal("https://nyaa.si/download/2.torrent", "https://nyaa.si/download/1.torrent");
    }

    [Fact]
    public void FiltrarYOrdenar_IncluirDudosos_LosMarcaYLosPoneAlFinal()
    {
        var candidatos = new List<CandidatoTorrent>
        {
            new("[SubsPlease] Nombre Que No Se Parece - 05 (1080p).mkv", "https://nyaa.si/download/1.torrent", "", 900, 1),
            new("[SubsPlease] Sousou no Frieren - 05 (1080p).mkv", "https://nyaa.si/download/2.torrent", "", 10, 1),
        };

        var automaticos = NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, new[] { "Sousou no Frieren" }, 5, 3);
        var paraElegir = NyaaRssParser.FiltrarYOrdenarCandidatos(candidatos, new[] { "Sousou no Frieren" }, 5, 3, incluirDudosos: true);

        automaticos.Should().ContainSingle().Which.Dudoso.Should().BeFalse();
        paraElegir.Should().HaveCount(2);
        paraElegir[1].Dudoso.Should().BeTrue();
        paraElegir[1].TorrentUrl.Should().Be("https://nyaa.si/download/1.torrent");
    }

    [Fact]
    public void ConsultasPorTitulo_SinSiglasNiAlfabetosAjenosNiOperadores()
    {
        var consultas = NyaaSourceService.ConsultasPorTitulo(new[]
        {
            "ONE PIECE", "OP", "วันพีซ", "Ван-Пис",
            "Re:ZERO -Starting Life in Another World- Season 2",
        });

        consultas.Should().Equal("one piece", "re zero starting life in another world");
    }
}
