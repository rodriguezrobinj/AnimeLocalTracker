using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.EnlacesMusica;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.EnlacesMusica;

/// <summary>El enlace a AniPlaylist se arma solo con el nombre del anime (la web busca por ese texto): sin red, sin scraping.</summary>
public class AniPlaylistProveedorTests
{
    private readonly AniPlaylistProveedor _sut = new();

    private static AnimeItem Anime(string titulo, string alternativos = "") => new() { AniListId = 1, Titulo = titulo, NombresAlternativos = alternativos };

    // === Texto de búsqueda ===

    [Theory]
    [InlineData("Kusuriya no Hitorigoto 2nd Season", "Kusuriya-no-Hitorigoto-2nd-Season")]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu", "Re-Zero-kara-Hajimeru-Isekai-Seikatsu")]
    [InlineData("Frieren: Beyond Journey's End", "Frieren-Beyond-Journeys-End")]
    [InlineData("Frieren: Beyond Journey’s End", "Frieren-Beyond-Journeys-End")]
    [InlineData("Fate/stay night: Unlimited Blade Works", "Fate-stay-night-Unlimited-Blade-Works")]
    [InlineData("  Attack   on   Titan  ", "Attack-on-Titan")]
    [InlineData("86 -Eighty Six-", "86-Eighty-Six")]
    [InlineData("Steins;Gate 0", "Steins-Gate-0")]
    public void ConstruirBusqueda_QuitaSignosYUneLasPalabrasConGuiones(string titulo, string esperado) =>
        AniPlaylistProveedor.ConstruirBusqueda(titulo).Should().Be(esperado);

    [Fact]
    public void ConstruirBusqueda_CodificaLasLetrasAcentuadas() =>
        AniPlaylistProveedor.ConstruirBusqueda("Pokémon Journeys").Should().Be("Pok%C3%A9mon-Journeys");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ???")]
    public void ConstruirBusqueda_SinNadaUtil_DevuelveNull(string? titulo) =>
        AniPlaylistProveedor.ConstruirBusqueda(titulo).Should().BeNull();

    [Fact]
    public void ConstruirBusqueda_TituloLarguisimo_SeCortaEnUnaPalabraCompleta()
    {
        string largo = string.Join(" ", System.Linq.Enumerable.Repeat("Palabra", 30));

        string resultado = AniPlaylistProveedor.ConstruirBusqueda(largo)!;

        resultado.Length.Should().BeLessThanOrEqualTo(80);
        resultado.Should().EndWith("Palabra", "no se corta a mitad de palabra");
    }

    [Fact]
    public void ConstruirBusqueda_NoDejaCaracteresQueCambienLaDireccion()
    {
        string resultado = AniPlaylistProveedor.ConstruirBusqueda("Foo/../bar?x=1&y=2#frag <script>")!;

        resultado.Should().MatchRegex("^[A-Za-z0-9-]+$");
    }

    // === Qué título usar ===

    [Fact]
    public void ElegirTitulo_ConTituloEnInglesEntreLosAlternativos_LoPrefiere() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("Shingeki no Kyojin", "Attack on Titan | 進撃の巨人 | SnK")).Should().Be("Attack on Titan");

    [Fact]
    public void ElegirTitulo_SinAlternativos_UsaElTituloPrincipal() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("Kusuriya no Hitorigoto")).Should().Be("Kusuriya no Hitorigoto");

    [Fact]
    public void ElegirTitulo_SiElAlternativoEsElMismoQueElPrincipal_UsaElPrincipal() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("BLEACH", "Bleach | ブリーチ")).Should().Be("BLEACH");

    [Fact]
    public void ElegirTitulo_IgnoraSiglasYTitulosNoLatinos() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("Dragon Ball GT", "ドラゴンボールGT | DBGT | Драконий жемчуг БП")).Should().Be("Dragon Ball GT");

    [Fact]
    public void ElegirTitulo_ConSoloTitulosNoLatinosComoAlternativos_UsaElPrincipal() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("Naruto", "ナルト | Наруто")).Should().Be("Naruto");

    [Fact]
    public void ElegirTitulo_SinNingunTitulo_DevuelveNull() =>
        AniPlaylistProveedor.ElegirTitulo(Anime("", "")).Should().BeNull();

    [Theory]
    [InlineData("Attack on Titan", true)]
    [InlineData("Pokémon", true)]
    [InlineData("進撃の巨人", false)]
    [InlineData("Ван-Пис", false)]
    [InlineData("Re:Zero 2nd Season", true)]
    public void EsAlfabetoLatino_DistingueElAlfabeto(string texto, bool esperado) =>
        AniPlaylistProveedor.EsAlfabetoLatino(texto).Should().Be(esperado);

    // === Enlace completo ===

    [Fact]
    public void ObtenerEnlace_ArmaLaUrlDeAniPlaylistConElTituloEnIngles()
    {
        var enlace = _sut.ObtenerEnlace(Anime("Shingeki no Kyojin", "Attack on Titan | 進撃の巨人"));

        enlace.Should().NotBeNull();
        enlace!.Url.Should().Be("https://aniplaylist.com/Attack-on-Titan");
        enlace.Nombre.Should().Be("AniPlaylist");
        enlace.ProveedorId.Should().Be("aniplaylist");
        enlace.ClaveDescripcion.Should().Be("Det_MusicaAniPlaylistTip");
    }

    [Fact]
    public void ObtenerEnlace_ConSoloElTituloPrincipal_FuncionaComoLaPruebaManual() =>
        _sut.ObtenerEnlace(Anime("Kusuriya no Hitorigoto 2nd Season"))!.Url.Should().Be("https://aniplaylist.com/Kusuriya-no-Hitorigoto-2nd-Season");

    [Fact]
    public void ObtenerEnlace_SinTitulo_DevuelveNull() =>
        _sut.ObtenerEnlace(Anime("")).Should().BeNull();

    [Fact]
    public void Id_EsEstable() => _sut.Id.Should().Be("aniplaylist");
}
