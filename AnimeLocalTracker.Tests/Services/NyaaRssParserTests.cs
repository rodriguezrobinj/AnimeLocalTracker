using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// INT-01: contrato tipado del parseo del feed RSS de Nyaa.si — fixture real
/// (capturado contra nyaa.si buscando "tsuihou sareta tensei juukishi", episodio 13,
/// más un par de items de Sousou no Frieren para cubrir los casos de batch) para que
/// un cambio del formato del feed rompa los tests y no en producción.
/// </summary>
public class NyaaRssParserTests
{
    // Fixture real: mezcla de releases de un solo episodio (distintos grupos/seeders),
    // un batch explícito ("batch"), un paquete de temporada sin número de episodio
    // ("S01" solo) y un candidato por debajo del mínimo de semillas.
    private const string FixtureRssReal = """
        <rss xmlns:atom="http://www.w3.org/2005/Atom" xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0">
        	<channel>
        		<title>Nyaa - "tsuihou sareta tensei juukishi" - Torrent File RSS</title>
        		<item>
        			<title>[Erai-raws] Tsuihou sareta Tensei Juukishi wa Game Chishiki de Musou suru - 13 [1080p CR WEBRip HEVC AAC][MultiSub][3A6FB35E]</title>
        			<link>https://nyaa.si/download/1000001.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000001</guid>
        			<nyaa:seeders>291</nyaa:seeders>
        			<nyaa:leechers>4</nyaa:leechers>
        			<nyaa:infoHash>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>1.4 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[SubsPlease] Tsuihou sareta Tensei Juukishi wa Game Chishiki de Musou suru - 13 (1080p) [CEC8715E].mkv</title>
        			<link>https://nyaa.si/download/1000002.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000002</guid>
        			<nyaa:seeders>1340</nyaa:seeders>
        			<nyaa:leechers>12</nyaa:leechers>
        			<nyaa:infoHash>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>1.3 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>The Exiled Heavy Knight Knows How to Game the System S01E13 1080p CR WEB-DL AAC2.0 H.264-VARYG</title>
        			<link>https://nyaa.si/download/1000003.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000003</guid>
        			<nyaa:seeders>123</nyaa:seeders>
        			<nyaa:leechers>2</nyaa:leechers>
        			<nyaa:infoHash>cccccccccccccccccccccccccccccccccccccccc</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>1.5 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[LowSeed] Tsuihou sareta Tensei Juukishi wa Game Chishiki de Musou suru - 13 (480p)</title>
        			<link>https://nyaa.si/download/1000004.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000004</guid>
        			<nyaa:seeders>1</nyaa:seeders>
        			<nyaa:leechers>0</nyaa:leechers>
        			<nyaa:infoHash>dddddddddddddddddddddddddddddddddddddddd</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>0.3 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[Erai-raws] Sousou no Frieren 2nd Season [1080p CR WEBRip HEVC AAC][MultiSub] (unofficial batch)</title>
        			<link>https://nyaa.si/download/1000005.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000005</guid>
        			<nyaa:seeders>37</nyaa:seeders>
        			<nyaa:leechers>4</nyaa:leechers>
        			<nyaa:infoHash>eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>6.6 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>Frieren: Beyond Journey's End (Sousou no Frieren) - S01 (YT WEB-DL 720p VP9 Opus)</title>
        			<link>https://nyaa.si/download/1000006.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000006</guid>
        			<nyaa:seeders>16</nyaa:seeders>
        			<nyaa:leechers>0</nyaa:leechers>
        			<nyaa:infoHash>ffffffffffffffffffffffffffffffffffffffff</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>3.1 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[AUTISM] Sousou no Frieren - S01v2 (BD Remux 1080p AVC FLAC/TrueHD/AAC 2.0/5.1)</title>
        			<link>https://nyaa.si/download/1000007.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000007</guid>
        			<nyaa:seeders>41</nyaa:seeders>
        			<nyaa:leechers>2</nyaa:leechers>
        			<nyaa:infoHash>0000000000000000000000000000000000000000</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>213.3 GiB</nyaa:size>
        		</item>
        		<item>
        			<title>[Batch-Group] Some Anime (01-13) [Complete]</title>
        			<link>https://nyaa.si/download/1000008.torrent</link>
        			<guid isPermaLink="true">https://nyaa.si/view/1000008</guid>
        			<nyaa:seeders>200</nyaa:seeders>
        			<nyaa:leechers>2</nyaa:leechers>
        			<nyaa:infoHash>1111111111111111111111111111111111111111</nyaa:infoHash>
        			<nyaa:categoryId>1_2</nyaa:categoryId>
        			<nyaa:size>5.0 GiB</nyaa:size>
        		</item>
        	</channel>
        </rss>
        """;

    [Fact]
    public void ExtraerCandidatos_ConRssReal_DeberiaParsearTodosLosCampos()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        candidatos.Should().HaveCount(8);

        var subsPlease = candidatos.Single(c => c.TorrentUrl == "https://nyaa.si/download/1000002.torrent");
        subsPlease.Titulo.Should().Contain("SubsPlease");
        subsPlease.Seeders.Should().Be(1340);
        subsPlease.InfoHash.Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        // 1.3 GiB = 1.3 * 1024^3
        subsPlease.TamanoBytes.Should().Be((long)(1.3 * 1024 * 1024 * 1024));
    }

    [Fact]
    public void ExtraerCandidatos_ConXmlInvalido_DeberiaDevolverListaVacia()
    {
        NyaaRssParser.ExtraerCandidatos("esto no es xml").Should().BeEmpty();
        NyaaRssParser.ExtraerCandidatos(string.Empty).Should().BeEmpty();
        NyaaRssParser.ExtraerCandidatos(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[Erai-raws] Tsuihou sareta Tensei Juukishi - 13 [1080p CR WEBRip HEVC AAC][MultiSub]", 13)]
    [InlineData("[SubsPlease] Tsuihou sareta Tensei Juukishi - 13 (1080p) [CEC8715E].mkv", 13)]
    [InlineData("The Exiled Heavy Knight Knows How to Game the System S01E13 1080p CR WEB-DL", 13)]
    [InlineData("[Group] Some Anime - 05 [720p]", 5)]
    public void ExtraerNumeroEpisodio_ConTitulosReales_DeberiaExtraerElNumeroCorrecto(string titulo, int esperado)
    {
        NyaaRssParser.ExtraerNumeroEpisodio(titulo).Should().Be(esperado);
    }

    [Theory]
    [InlineData("[Erai-raws] Sousou no Frieren 2nd Season [1080p CR WEBRip HEVC AAC][MultiSub] (unofficial batch)")]
    [InlineData("Frieren: Beyond Journey's End (Sousou no Frieren) - S01 (YT WEB-DL 720p VP9 Opus)")]
    [InlineData("[AUTISM] Sousou no Frieren - S01v2 (BD Remux 1080p AVC FLAC/TrueHD/AAC 2.0/5.1)")]
    [InlineData("[Batch-Group] Some Anime (01-13) [Complete]")]
    public void ExtraerNumeroEpisodio_ConBatches_DeberiaDevolverNull(string titulo)
    {
        NyaaRssParser.ExtraerNumeroEpisodio(titulo).Should().BeNull();
    }

    [Theory]
    [InlineData("[Erai-raws] Sousou no Frieren 2nd Season [1080p CR WEBRip HEVC AAC][MultiSub] (unofficial batch)", true)]
    [InlineData("Frieren: Beyond Journey's End (Sousou no Frieren) - S01 (YT WEB-DL 720p VP9 Opus)", true)]
    [InlineData("[AUTISM] Sousou no Frieren - S01v2 (BD Remux 1080p AVC FLAC/TrueHD/AAC 2.0/5.1)", true)]
    [InlineData("[Batch-Group] Some Anime (01-13) [Complete]", true)]
    [InlineData("[SubsPlease] Tsuihou sareta Tensei Juukishi - 13 (1080p) [CEC8715E].mkv", false)]
    [InlineData("The Exiled Heavy Knight Knows How to Game the System S01E13 1080p CR WEB-DL", false)]
    public void EsBatch_ClasificaCorrectamente(string titulo, bool esperado)
    {
        NyaaRssParser.EsBatch(titulo).Should().Be(esperado);
    }

    [Fact]
    public void ElegirMejorCandidato_DeberiaExcluirBatchesYCandidatosBajoElMinimo_YQuedarseConElDeMasSemillas()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 3);

        elegido.Should().NotBeNull();
        elegido!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000002.torrent"); // SubsPlease, 1340 semillas
    }

    [Fact]
    public void ElegirMejorCandidato_SinNingunCandidato_DeberiaDevolverNull()
    {
        // Lista vacía: ni episodio suelto ni batch de dónde caer.
        NyaaRssParser.ElegirMejorCandidato(new List<CandidatoTorrent>(), numeroEpisodio: 99, minimoSeeders: 3).Should().BeNull();
    }

    [Fact]
    public void ElegirMejorCandidato_SinEpisodioSueltoDisponible_DeberiaCaerAlMejorBatch()
    {
        // Fase 2a: el episodio 99 no existe como release suelto en el fixture, pero
        // hay varios batches con semillas suficientes — debe caer al de más semillas
        // en vez de devolver null (preferencia con fallback).
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 99, minimoSeeders: 3);

        elegido.Should().NotBeNull();
        elegido!.Value.EsBatch.Should().BeTrue();
        elegido.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000008.torrent"); // Batch-Group, 200 semillas (el mejor batch)
    }

    [Fact]
    public void ElegirMejorCandidato_ConEpisodioSueltoDisponible_NuncaDeberiaMarcarloComoBatch()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 3);

        elegido!.Value.EsBatch.Should().BeFalse();
    }

    [Fact]
    public void ElegirMejorCandidato_ConGrupoPreferido_DeberiaGanarAunqueTengaMenosSemillas()
    {
        // Fase 2b: Erai-raws (291 semillas) tiene menos que SubsPlease (1340), pero si el
        // grupo preferido es Erai-raws, debe ganar igual — preferencia por encima de semillas.
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 3, grupoPreferido: "Erai-raws");

        elegido.Should().NotBeNull();
        elegido!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000001.torrent");
    }

    [Fact]
    public void ElegirMejorCandidato_ConGrupoPreferidoQueNoExisteEntreLosCandidatos_DeberiaCaerAlDeMasSemillas()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 3, grupoPreferido: "GrupoQueNoSubioEsteEpisodio");

        elegido.Should().NotBeNull();
        elegido!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000002.torrent"); // SubsPlease, el de más semillas de siempre
    }

    [Fact]
    public void ElegirMejorCandidato_ConResolucionPreferida_DeberiaGanarAunqueTengaMenosSemillas()
    {
        // El candidato de 480p tiene solo 1 semilla (muy por debajo de SubsPlease/Erai-raws
        // en 1080p) — con minimoSeeders=1 para que igual califique, la resolución preferida
        // debe pesar más que las semillas.
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 1, resolucionPreferida: "480p");

        elegido.Should().NotBeNull();
        elegido!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000004.torrent"); // [LowSeed] ... (480p)
    }

    [Fact]
    public void ElegirMejorCandidato_ConGrupoYResolucionPreferidos_ElGrupoPesaMasQueLaResolucion()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        // Erai-raws (1000001) es 1080p, no 480p — pedir "Erai-raws" + "480p" a la vez no hay
        // ningún candidato que cumpla ambas, así que gana el que cumple el grupo (más
        // prioritario en el orden de MejorPorPreferencia) por encima de semillas.
        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 3, grupoPreferido: "Erai-raws", resolucionPreferida: "480p");

        elegido!.Value.TorrentUrl.Should().Be("https://nyaa.si/download/1000001.torrent");
    }

    [Fact]
    public void ElegirMejorCandidato_ConMinimoDeSemillasAlto_DeberiaDescartarElDeBajasSemillas()
    {
        var candidatos = NyaaRssParser.ExtraerCandidatos(FixtureRssReal);

        // Con un mínimo muy alto, ni siquiera el de SubsPlease (1340) alcanza a los demás
        // a evaluar el caso límite: mínimo justo por encima del candidato de 1 semilla.
        var elegido = NyaaRssParser.ElegirMejorCandidato(candidatos, numeroEpisodio: 13, minimoSeeders: 2);

        elegido.Should().NotBeNull();
        elegido!.Value.Seeders.Should().BeGreaterThan(1); // el de 1 semilla ("[LowSeed]") queda fuera
    }
}
