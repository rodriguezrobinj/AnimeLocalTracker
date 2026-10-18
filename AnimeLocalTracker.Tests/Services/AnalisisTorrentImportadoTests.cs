#pragma warning disable CA1861 // Datos constantes de prueba
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Torrents;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Torrent soltado en la ventana: de qué anime es y qué episodios trae.</summary>
public class AnalisisTorrentImportadoTests
{
    private static AnimeItem A(int id, string titulo, string alternativos = "") =>
        new() { AniListId = id, Titulo = titulo, NombresAlternativos = alternativos };

    private static readonly AnimeItem[] Biblioteca =
    {
        A(1, "Sousou no Frieren", "Frieren: Beyond Journey's End"),
        A(2, "Mushoku Tensei: Isekai Ittara Honki Dasu"),
        A(3, "Mushoku Tensei II: Isekai Ittara Honki Dasu"),
        A(4, "Dandadan"),
    };

    [Theory]
    [InlineData("[SubsPlease] Sousou no Frieren (01-28) (1080p) [Batch]", 1)]
    [InlineData("[SubsPlease] Sousou no Frieren - 05 (1080p) [ABCD1234].mkv", 1)]
    [InlineData("[Erai-raws] Mushoku Tensei II - Isekai Ittara Honki Dasu - 03 [1080p]", 3)]
    [InlineData("[ASW] Dandadan - 07 [1080p HEVC]", 4)]
    public void ReconocerAnime_NombresReales(string nombre, int esperado)
    {
        AnalisisTorrentImportado.ReconocerAnime(nombre, Biblioteca)!.AniListId.Should().Be(esperado);
    }

    [Fact]
    public void ReconocerAnime_PorNombreAlternativo()
    {
        AnalisisTorrentImportado.ReconocerAnime("[Group] Frieren - Beyond Journey's End - 02 [1080p]", new[] { A(1, "Sousou no Frieren", "Frieren: Beyond Journey's End") })
            !.AniListId.Should().Be(1);
    }

    [Fact]
    public void ReconocerAnime_SinCoincidenciaODosCandidatos_Null()
    {
        AnalisisTorrentImportado.ReconocerAnime("[SubsPlease] Kaiju No. 8 - 01 (1080p)", Biblioteca).Should().BeNull();
        AnalisisTorrentImportado.ReconocerAnime("[SubsPlease] Dandadan - 01", new[] { A(4, "Dandadan"), A(5, "Dandadan", "Dan Da Dan") }).Should().BeNull();
    }

    private static ArchivoTorrent F(string ruta, long tamano = 100) => new(ruta, tamano);

    [Fact]
    public void Repartir_Lote_NumerosYYaEstan()
    {
        var archivos = new[]
        {
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 02 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 03 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 03 (1080p).ass"),
            F(@"Frieren\info.nfo"),
        };

        var r = AnalisisTorrentImportado.Repartir(archivos, n => n == 2);

        r.Videos.Should().Be(3);
        r.Todos.Should().Equal(1, 2, 3);
        r.Nuevos.Should().Equal(1, 3);
        r.YaEstan.Should().Be(1);
        r.SinNumero.Should().Be(0);
    }

    [Fact]
    public void Repartir_PeliculaDeUnArchivoSinNumero_EsElEpisodioUno()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("[Group] Kimi no Na wa (BD 1080p).mkv", 5_000_000_000) }, _ => false);

        r.Nuevos.Should().Equal(1);
        r.SinNumero.Should().Be(0);
    }

    [Fact]
    public void Repartir_LoteConArchivosSinNumero_SeOmiten()
    {
        var archivos = new[] { F("Show - 01.mkv"), F("Show - 02.mkv"), F("Show - NCOP.mkv"), F("Show - Special.mkv") };

        var r = AnalisisTorrentImportado.Repartir(archivos, _ => false);

        r.Nuevos.Should().Equal(1, 2);
        r.SinNumero.Should().Be(2);
    }

    [Fact]
    public void Repartir_NumeroRepetido_CuentaUnaVez()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("Show - 01 [720p].mkv"), F("Show - 01 [1080p].mkv") }, _ => false);

        r.Nuevos.Should().Equal(1);
    }

    [Fact]
    public void Repartir_SinVideos()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("Show OST.flac"), F("cover.jpg") }, _ => false);

        r.Videos.Should().Be(0);
        r.Nuevos.Should().BeEmpty();
    }

    [Fact]
    public void Repartir_NumeracionContinua_SeRespetaYSeVeEnElRango()
    {
        var archivos = Enumerable.Range(13, 12).Select(n => F($"Show 2nd Season - {n:D2}.mkv")).ToArray();

        var r = AnalisisTorrentImportado.Repartir(archivos, _ => false);

        r.Todos[0].Should().Be(13);
        AnalisisTorrentImportado.FormatearRangos(r.Todos).Should().Be("13–24");
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 1, 2, 3, 4 }, "1–4")]
    [InlineData(new[] { 1, 2, 3, 5, 7, 8 }, "1–3, 5, 7–8")]
    public void FormatearRangos(int[] numeros, string esperado)
    {
        AnalisisTorrentImportado.FormatearRangos(numeros).Should().Be(esperado);
    }

    [Fact]
    public void ReconocerAnime_ConUnTituloJaponesDeDigitosAnchosEnLaBiblioteca_NoLanza()
    {
        var biblioteca = Biblioteca.Append(A(9, "Sasaki to Pii-chan Season 2", "佐々木とピーちゃん 第２期")).ToArray();

        AnalisisTorrentImportado.ReconocerAnime("[SubsPlease] Dandadan - 07 (1080p)", biblioteca)!.AniListId.Should().Be(4);
    }
}
