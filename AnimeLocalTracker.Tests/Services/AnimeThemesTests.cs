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
}
