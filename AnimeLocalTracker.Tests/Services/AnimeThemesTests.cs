using System.Collections.Generic;
using System.Text.Json;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Lógica pura de openings/endings (AnimeThemes.moe): pertenencia episodio↔rango, el nombre de
/// archivo local determinista (y su parseo inverso) y el mapeo de la respuesta JSON al modelo de
/// dominio. Nada de esto toca red ni disco — son los mismos cálculos que usa SkipTimesCoordinator
/// para decidir qué mp3 ya descargado aplica a un episodio, sin base de datos ni llamada de red.
/// </summary>
public class AnimeThemesTests
{
    [Theory]
    [InlineData(null, 1, true)]
    [InlineData("", 5, true)]
    [InlineData("1-16", 1, true)]
    [InlineData("1-16", 16, true)]
    [InlineData("1-16", 17, false)]
    [InlineData("1-16", 0, false)]
    [InlineData("1-14, 16", 15, false)]
    [InlineData("1-14, 16", 16, true)]
    [InlineData("28", 28, true)]
    [InlineData("28", 27, false)]
    [InlineData("2-4, 7-12", 8, true)]
    [InlineData("2-4, 7-12", 5, false)]
    public void EpisodioEnRango_DeberiaEvaluarLaPertenenciaCorrectamente(string? rango, int episodio, bool esperado)
    {
        AnimeThemeInfo.EpisodioEnRango(rango, episodio).Should().Be(esperado);
    }

    [Fact]
    public void EpisodioEnRango_ConTextoMalformado_NoDeberiaLanzarYDeberiaSerFalse()
    {
        AnimeThemeInfo.EpisodioEnRango("abc-def", 5).Should().BeFalse();
        AnimeThemeInfo.EpisodioEnRango("1-", 5).Should().BeFalse();
    }

    [Theory]
    [InlineData("OP", "OP1", 1, "1-16", "OP_OP1_v1_ep1-16.mp3")]
    [InlineData("ED", "ED1", 2, "17-27", "ED_ED1_v2_ep17-27.mp3")]
    [InlineData("ED", "ED1-TV", 1, null, "ED_ED1-TV_v1_eptodos.mp3")]
    [InlineData("ED", "ED1", 1, "1-14, 16", "ED_ED1_v1_ep1-14_16.mp3")]
    public void NombreArchivoLocal_YSuParseoInverso_DeberianSerConsistentes(string tipo, string slug, int version, string? rango, string nombreEsperado)
    {
        var tema = new AnimeThemeInfo
        {
            Slug = slug,
            Tipo = tipo,
            Version = version,
            RangoEpisodios = rango,
            AudioUrlOgg = "https://a.animethemes.moe/x.ogg"
        };

        string nombre = tema.NombreArchivoLocal();
        nombre.Should().Be(nombreEsperado);

        // El nombre de archivo debe poder reconstruirse en un TemaLocalDisponible equivalente
        // (esto es justo lo que hace SkipTimesCoordinator sin red ni base de datos).
        bool ok = AnimeThemesDownloadService.TryParseNombreArchivo(@"C:\datos\Music\123\" + nombre, out var reconstruido);

        ok.Should().BeTrue();
        reconstruido.Should().NotBeNull();
        reconstruido!.Tipo.Should().Be(tipo);
        reconstruido.Slug.Should().Be(slug);
        reconstruido.Version.Should().Be(version);
        reconstruido.AplicaAlEpisodio(1).Should().Be(tema.AplicaAlEpisodio(1));
    }

    [Fact]
    public void TryParseNombreArchivo_ConNombreAjenoAlFormato_DeberiaFallarSinLanzar()
    {
        bool ok = AnimeThemesDownloadService.TryParseNombreArchivo(@"C:\datos\Music\123\portada.mp3", out var resultado);

        ok.Should().BeFalse();
        resultado.Should().BeNull();
    }

    [Fact]
    public void MapearTemas_DeberiaAplanarEntradasYUsarSoloElLinkDeAudio()
    {
        var anime = new AnimeThemesAnimeDto
        {
            Slug = "sousou_no_frieren",
            AnimeThemes = new List<AnimeThemeDto>
            {
                new()
                {
                    Slug = "OP1",
                    Type = "OP",
                    Song = new AnimeThemeSongDto
                    {
                        Title = "Yuusha",
                        Artists = new List<AnimeThemeArtistDto> { new() { Name = "YOASOBI" } }
                    },
                    AnimeThemeEntries = new List<AnimeThemeEntryDto>
                    {
                        new()
                        {
                            Version = 1,
                            Episodes = "1-16",
                            Videos = new List<AnimeThemeVideoDto>
                            {
                                new()
                                {
                                    Basename = "SousouNoFrieren-OP1.webm",
                                    Audio = new AnimeThemeAudioDto
                                    {
                                        Basename = "SousouNoFrieren-OP1-NCBD1080.ogg",
                                        Link = "https://a.animethemes.moe/SousouNoFrieren-OP1-NCBD1080.ogg"
                                    }
                                }
                            }
                        }
                    }
                },
                // Entrada sin audio (solo video): debe descartarse, nunca se usa el .webm.
                new()
                {
                    Slug = "OP2",
                    Type = "OP",
                    Song = new AnimeThemeSongDto { Title = "SinAudio" },
                    AnimeThemeEntries = new List<AnimeThemeEntryDto>
                    {
                        new()
                        {
                            Version = 1,
                            Videos = new List<AnimeThemeVideoDto> { new() { Basename = "SinAudio.webm", Audio = null } }
                        }
                    }
                }
            }
        };

        var resultado = AnimeThemesService.MapearTemas(anime);

        resultado.Should().HaveCount(1, "la entrada sin audio.link no debe generar un AnimeThemeInfo");
        var tema = resultado[0];
        tema.Slug.Should().Be("OP1");
        tema.Tipo.Should().Be("OP");
        tema.TituloCancion.Should().Be("Yuusha");
        tema.Artistas.Should().Be("YOASOBI");
        tema.RangoEpisodios.Should().Be("1-16");
        tema.AudioUrlOgg.Should().Be("https://a.animethemes.moe/SousouNoFrieren-OP1-NCBD1080.ogg");
    }

    [Fact]
    public void MapearTemas_ConAnimeNulo_DeberiaDevolverListaVacia()
    {
        AnimeThemesService.MapearTemas(null).Should().BeEmpty();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Deserialización real contra JSON capturado de la API en vivo (no un DTO construido a mano):
    /// esto es justo lo que se rompió la primera vez — el wrapper real es "resources" (plural), no
    /// "resource", y el bug pasó desapercibido porque los demás tests solo probaban MapearTemas con
    /// un AnimeThemesAnimeDto ya construido, nunca el paso de JSON crudo → DTO.
    /// </summary>
    [Fact]
    public void AnimeThemesResourceResponse_DeserializaJsonRealDeLaApi()
    {
        const string json = """
        {"resources":[{"id":8193,"external_id":21613,"link":"https:\/\/anilist.co\/anime\/21613","site":"AniList",
        "anime":[{"id":3208,"name":"Youjo Senki","media_format":"TV","season":"Winter","slug":"youjo_senki","year":2017}]}]}
        """;

        var dto = JsonSerializer.Deserialize<AnimeThemesResourceResponse>(json, JsonOptions);

        dto.Should().NotBeNull();
        dto!.Resource.Should().ContainSingle();
        dto.Resource[0].Anime.Should().ContainSingle();
        dto.Resource[0].Anime[0].Slug.Should().Be("youjo_senki");
    }

    /// <summary>Deserialización real de la respuesta de /anime/{slug}?include=... (JSON capturado, recortado).</summary>
    [Fact]
    public void AnimeThemesAnimeResponse_DeserializaJsonRealDeLaApi()
    {
        const string json = """
        {"anime":{"id":3208,"name":"Youjo Senki","slug":"youjo_senki","animethemes":[
            {"id":7936,"slug":"OP1","type":"OP",
             "song":{"id":7937,"title":"JINGO JUNGLE","artists":[{"id":174,"name":"MYTH & ROID"}]},
             "animethemeentries":[{"id":9294,"episodes":"2-4, 7-12","spoiler":false,"version":1,
                 "videos":[{"id":8756,"basename":"YoujoSenki-OP1.webm","link":"https:\/\/v.animethemes.moe\/YoujoSenki-OP1.webm",
                     "audio":{"id":7122,"basename":"YoujoSenki-OP1.ogg","link":"https:\/\/a.animethemes.moe\/YoujoSenki-OP1.ogg"}}]}]}
        ]}}
        """;

        var dto = JsonSerializer.Deserialize<AnimeThemesAnimeResponse>(json, JsonOptions);
        var temas = AnimeThemesService.MapearTemas(dto?.Anime);

        temas.Should().ContainSingle();
        var tema = temas[0];
        tema.Slug.Should().Be("OP1");
        tema.Tipo.Should().Be("OP");
        tema.TituloCancion.Should().Be("JINGO JUNGLE");
        tema.Artistas.Should().Be("MYTH & ROID");
        tema.RangoEpisodios.Should().Be("2-4, 7-12");
        tema.AudioUrlOgg.Should().Be("https://a.animethemes.moe/YoujoSenki-OP1.ogg");
    }

    /// <summary>
    /// JSON real capturado el 2026-09-26: "song.artists" responde HTTP 500, los artistas llegan por
    /// "song.performances[].artist". Si se rompe este mapeo, el OP/ED se muestra sin cantante.
    /// </summary>
    [Fact]
    public void AnimeThemesAnimeResponse_ArtistasVienenDePerformances()
    {
        const string json = """
        {"anime":{"id":2611,"name":"Shingeki no Kyojin","slug":"shingeki_no_kyojin","animethemes":[
            {"id":1,"slug":"OP1","type":"OP",
             "song":{"id":5279,"title":"Guren no Yumiya","performances":[
                 {"id":2162,"alias":null,"as":null,"relevance":1,
                  "artist":{"id":121,"name":"Linked Horizon","slug":"linked_horizon","information":null}}]},
             "animethemeentries":[{"id":2,"episodes":"1-13","spoiler":false,"version":1,
                 "videos":[{"id":3,"basename":"ShingekiNoKyojin-OP1.webm",
                     "audio":{"id":4,"basename":"ShingekiNoKyojin-OP1.ogg","link":"https:\/\/a.animethemes.moe\/ShingekiNoKyojin-OP1.ogg"}}]}]}
        ]}}
        """;

        var dto = JsonSerializer.Deserialize<AnimeThemesAnimeResponse>(json, JsonOptions);
        var temas = AnimeThemesService.MapearTemas(dto?.Anime);

        temas.Should().ContainSingle();
        temas[0].TituloCancion.Should().Be("Guren no Yumiya");
        temas[0].Artistas.Should().Be("Linked Horizon");
    }

    /// <summary>JSON real (26-sep-2026) de Grand Blue S3: el primer "resource" viene sin anime y el segundo sí.</summary>
    [Fact]
    public void ExtraerSlugs_ConPrimerResourceVacio_DeberiaEncontrarElAnimeDelSegundo()
    {
        const string json = """
        {"resources":[
          {"id":1,"external_id":199111,"site":"AniList","anime":[]},
          {"id":2,"external_id":199111,"site":"AniList",
           "anime":[{"id":9,"name":"Grand Blue Season 3","slug":"grand_blue_season_3","year":2026,"season":"Summer"}]}
        ]}
        """;

        var dto = JsonSerializer.Deserialize<AnimeThemesResourceResponse>(json, JsonOptions);

        AnimeThemesService.ExtraerSlugs(dto).Should().Equal("grand_blue_season_3");
    }

    /// <summary>JSON real (26-sep-2026) de Re:Zero OVAs: dos "resources", cada uno con un OVA distinto.</summary>
    [Fact]
    public void ExtraerSlugs_ConVariosResources_DeberiaDevolverTodosLosAnimesSinDuplicarEnOrden()
    {
        const string json = """
        {"resources":[
          {"id":1,"external_id":100049,"site":"AniList","anime":[
            {"slug":"rezero_kara_hajimeru_isekai_seikatsu_memory_snow","name":"Memory Snow"}]},
          {"id":2,"external_id":100049,"site":"AniList","anime":[
            {"slug":"rezero_kara_hajimeru_isekai_seikatsu_hyouketsu_no_kizuna","name":"Hyouketsu no Kizuna"},
            {"slug":"rezero_kara_hajimeru_isekai_seikatsu_memory_snow","name":"Memory Snow"}]}
        ]}
        """;

        var dto = JsonSerializer.Deserialize<AnimeThemesResourceResponse>(json, JsonOptions);

        AnimeThemesService.ExtraerSlugs(dto).Should().Equal(
            "rezero_kara_hajimeru_isekai_seikatsu_memory_snow",
            "rezero_kara_hajimeru_isekai_seikatsu_hyouketsu_no_kizuna");
    }

    [Fact]
    public void ExtraerSlugs_SinResourcesOSinAnimes_DeberiaDevolverVacio()
    {
        AnimeThemesService.ExtraerSlugs(null).Should().BeEmpty();
        AnimeThemesService.ExtraerSlugs(new AnimeThemesResourceResponse()).Should().BeEmpty();
    }

    /// <summary>
    /// Los dos OVAs de Re:Zero tienen un "ED1" versión 1 para todos los episodios: sin sufijo compartirían
    /// nombre de archivo local (descargar uno marcaría el otro como descargado y lo pisaría).
    /// </summary>
    [Fact]
    public void MapearVarios_ConMismoSlugEnDosAnimes_DeberiaGenerarArchivosLocalesDistintos()
    {
        static AnimeThemesAnimeDto Ovas(string cancion, string audio) => new()
        {
            AnimeThemes = new List<AnimeThemeDto>
            {
                new()
                {
                    Slug = "ED1",
                    Type = "ED",
                    Song = new AnimeThemeSongDto { Title = cancion },
                    AnimeThemeEntries = new List<AnimeThemeEntryDto>
                    {
                        new()
                        {
                            Version = 1,
                            Videos = new List<AnimeThemeVideoDto> { new() { Audio = new AnimeThemeAudioDto { Link = audio } } }
                        }
                    }
                }
            }
        };

        var temas = AnimeThemesService.MapearVarios(new[]
        {
            Ovas("White White Snow", "https://a.animethemes.moe/MemorySnow-ED1.ogg"),
            Ovas("Yuki no Hate ni Kimi no Na wo", "https://a.animethemes.moe/HyouketsuNoKizuna-ED1.ogg")
        });

        temas.Should().HaveCount(2);
        temas[0].Slug.Should().Be("ED1", "el primer anime conserva su slug para no invalidar mp3 ya descargados");
        temas[1].Slug.Should().Be("ED1-2");
        temas.Select(t => t.NombreArchivoLocal()).Should().OnlyHaveUniqueItems();
        temas.Select(t => t.TituloCancion).Should().Equal("White White Snow", "Yuki no Hate ni Kimi no Na wo");
    }

    [Fact]
    public void MapearVarios_ConUnSoloAnime_NoDeberiaAlterarLosSlugs()
    {
        var anime = new AnimeThemesAnimeDto
        {
            AnimeThemes = new List<AnimeThemeDto>
            {
                new()
                {
                    Slug = "OP1", Type = "OP", Song = new AnimeThemeSongDto { Title = "X" },
                    AnimeThemeEntries = new List<AnimeThemeEntryDto>
                    {
                        new() { Videos = new List<AnimeThemeVideoDto> { new() { Audio = new AnimeThemeAudioDto { Link = "https://a.animethemes.moe/x.ogg" } } } }
                    }
                }
            }
        };

        AnimeThemesService.MapearVarios(new[] { anime }).Should().ContainSingle().Which.Slug.Should().Be("OP1");
    }

    [Fact]
    public void MapearTemas_ConVariosArtistasYRepetidos_DeberiaUnirlosSinDuplicar()
    {
        var anime = new AnimeThemesAnimeDto
        {
            AnimeThemes = new List<AnimeThemeDto>
            {
                new()
                {
                    Slug = "ED1",
                    Type = "ED",
                    Song = new AnimeThemeSongDto
                    {
                        Title = "Duo",
                        Performances = new List<AnimeThemePerformanceDto>
                        {
                            new() { Artist = new AnimeThemeArtistDto { Name = "Aimer" } },
                            new() { Artist = new AnimeThemeArtistDto { Name = "milet" } },
                            new() { Artist = null }
                        },
                        Artists = new List<AnimeThemeArtistDto> { new() { Name = "aimer" } }
                    },
                    AnimeThemeEntries = new List<AnimeThemeEntryDto>
                    {
                        new()
                        {
                            Videos = new List<AnimeThemeVideoDto>
                            {
                                new() { Audio = new AnimeThemeAudioDto { Link = "https://a.animethemes.moe/Duo-ED1.ogg" } }
                            }
                        }
                    }
                }
            }
        };

        AnimeThemesService.MapearTemas(anime).Should().ContainSingle()
            .Which.Artistas.Should().Be("Aimer, milet");
    }
}
