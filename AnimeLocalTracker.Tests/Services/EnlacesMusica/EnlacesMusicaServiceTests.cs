using System;
using System.Collections.Generic;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.EnlacesMusica;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.EnlacesMusica;

/// <summary>Reúne los enlaces de todos los proveedores sin que uno roto afecte a los demás, y solo abre direcciones https.</summary>
public class EnlacesMusicaServiceTests
{
    private static readonly AnimeItem Anime = new() { AniListId = 1, Titulo = "Frieren" };
    private readonly List<string> _abiertas = new();

    private static IProveedorEnlacesMusica Proveedor(string id, EnlaceMusica? enlace)
    {
        var mock = new Mock<IProveedorEnlacesMusica>();
        mock.SetupGet(p => p.Id).Returns(id);
        mock.Setup(p => p.ObtenerEnlace(It.IsAny<AnimeItem>())).Returns(enlace);
        return mock.Object;
    }

    private static EnlaceMusica Enlace(string id, string url = "https://ejemplo.com/x") => new(id, id.ToUpperInvariant(), url, "Det_MusicaAniPlaylistTip");

    private EnlacesMusicaService Crear(params IProveedorEnlacesMusica[] proveedores) => new(proveedores, _abiertas.Add);

    [Fact]
    public void ObtenerEnlaces_JuntaLosDeTodosLosProveedoresEnOrden()
    {
        var sut = Crear(Proveedor("a", Enlace("a")), Proveedor("b", Enlace("b")));

        sut.ObtenerEnlaces(Anime).Should().SatisfyRespectively(e => e.ProveedorId.Should().Be("a"), e => e.ProveedorId.Should().Be("b"));
    }

    [Fact]
    public void ObtenerEnlaces_OmiteLosProveedoresQueNoTienenEnlace()
    {
        var sut = Crear(Proveedor("a", null), Proveedor("b", Enlace("b")));

        sut.ObtenerEnlaces(Anime).Should().ContainSingle().Which.ProveedorId.Should().Be("b");
    }

    [Fact]
    public void ObtenerEnlaces_UnProveedorQueFalla_NoAfectaALosDemas()
    {
        var roto = new Mock<IProveedorEnlacesMusica>();
        roto.SetupGet(p => p.Id).Returns("roto");
        roto.Setup(p => p.ObtenerEnlace(It.IsAny<AnimeItem>())).Throws(new InvalidOperationException("boom"));
        var sut = Crear(roto.Object, Proveedor("b", Enlace("b")));

        sut.ObtenerEnlaces(Anime).Should().ContainSingle().Which.ProveedorId.Should().Be("b");
    }

    [Theory]
    [InlineData("http://ejemplo.com/x")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("calc.exe")]
    [InlineData("https://usuario:clave@ejemplo.com/x")]
    [InlineData("")]
    public void ObtenerEnlaces_DescartaLasUrlsQueNoSonHttpsSeguras(string url)
    {
        var sut = Crear(Proveedor("a", Enlace("a", url)));

        sut.ObtenerEnlaces(Anime).Should().BeEmpty();
    }

    [Fact]
    public void ObtenerEnlaces_ConDosProveedoresConElMismoId_DejaSoloElPrimero()
    {
        var sut = Crear(Proveedor("a", Enlace("a", "https://uno.com")), Proveedor("a", Enlace("a", "https://dos.com")));

        sut.ObtenerEnlaces(Anime).Should().ContainSingle().Which.Url.Should().Be("https://uno.com");
    }

    [Fact]
    public void ObtenerEnlaces_SinProveedoresONuloAnime_DevuelveVacio()
    {
        Crear().ObtenerEnlaces(Anime).Should().BeEmpty();
        Crear(Proveedor("a", Enlace("a"))).ObtenerEnlaces(null!).Should().BeEmpty();
    }

    // === Abrir ===

    [Fact]
    public void Abrir_ConUrlSegura_LaAbreEnElNavegador()
    {
        var sut = Crear();

        sut.Abrir(Enlace("a", "https://aniplaylist.com/Attack-on-Titan")).Should().BeTrue();

        _abiertas.Should().Equal("https://aniplaylist.com/Attack-on-Titan");
    }

    [Theory]
    [InlineData("http://aniplaylist.com/x")]
    [InlineData("file:///C:/x.exe")]
    [InlineData("no es una url")]
    public void Abrir_ConUrlNoSegura_NoHaceNada(string url)
    {
        var sut = Crear();

        sut.Abrir(Enlace("a", url)).Should().BeFalse();

        _abiertas.Should().BeEmpty();
    }

    [Fact]
    public void Abrir_SiElNavegadorFalla_DevuelveFalsoSinLanzar()
    {
        var sut = new EnlacesMusicaService([], _ => throw new System.ComponentModel.Win32Exception("sin navegador"));

        sut.Abrir(Enlace("a")).Should().BeFalse();
    }

    [Fact]
    public void Abrir_ConEnlaceNulo_DevuelveFalso() =>
        Crear().Abrir(null!).Should().BeFalse();
}
