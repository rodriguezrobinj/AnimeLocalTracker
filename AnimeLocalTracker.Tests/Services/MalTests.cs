using System;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Mal;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class MalTests
{
    // ---- Conversión ----

    [Theory]
    [InlineData("CURRENT", 3, "watching")]
    [InlineData("REPEATING", 3, "watching")]
    [InlineData("COMPLETED", 28, "completed")]
    [InlineData("PAUSED", 3, "on_hold")]
    [InlineData("DROPPED", 3, "dropped")]
    [InlineData("PLANNING", 0, "plan_to_watch")]
    [InlineData("", 3, "watching")]
    [InlineData(null, 3, "watching")]
    [InlineData("", 0, null)]
    public void Estado_DeAniListAMal(string? estado, int progreso, string? esperado)
        => MapeoMal.Estado(estado, progreso).Should().Be(esperado);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(85f, 9)]
    [InlineData(84f, 8)]
    [InlineData(100f, 10)]
    [InlineData(4f, 0)]
    [InlineData(5f, 1)]
    public void Nota_De100A10(float puntaje, int esperada) => MapeoMal.Nota(puntaje).Should().Be(esperada);

    [Theory]
    [InlineData(5, 7, 24, 7)]
    [InlineData(9, 7, 24, 9)]
    [InlineData(30, 0, 24, 24)]
    [InlineData(30, 0, 0, 30)]
    public void Progreso_ElMayorSinPasarDelTotal(int vistos, int editor, int total, int esperado)
        => MapeoMal.Progreso(vistos, editor, total).Should().Be(esperado);

    [Fact]
    public void Deseado_UsaElSeguimientoLocalSiExiste()
    {
        var anime = new AnimeItem { AniListId = 1, MalId = 52991, EstadoUsuario = "PLANNING", TotalEpisodios = 28 };
        var seguimiento = new SeguimientoLocal { AniListId = 1, Estado = "CURRENT", Progreso = 10, Puntaje = 85 };

        MapeoMal.Deseado(anime, seguimiento, vistos: 14).Should().Be(new EstadoMal("watching", 14, 9));
    }

    [Fact]
    public void Deseado_CompletadoSinEpisodiosEnLocal_ProgresoEsElTotal()
    {
        // Terminado en AniList sin episodios en el disco: con progreso 0, MAL "viendo 5/12" bloquearía marcarlo completado.
        var anime = new AnimeItem { AniListId = 1, MalId = 31043, EstadoUsuario = "COMPLETED", TotalEpisodios = 12 };

        MapeoMal.Deseado(anime, null, vistos: 0).Should().Be(new EstadoMal("completed", 12, 0));
        ReglaMal.QueEnviar(new EstadoMal("completed", 12, 0), new EstadoMal("watching", 5, 7)).Should().Be(new EstadoMal("completed", 12, 7));
    }

    [Fact]
    public void Deseado_SinNadaQueEnviar_Null()
        => MapeoMal.Deseado(new AnimeItem { AniListId = 1, MalId = 5 }, null, vistos: 0).Should().BeNull();

    [Fact]
    public void Deseado_SinMalId_Null()
        => MapeoMal.Deseado(new AnimeItem { AniListId = 1, MalId = null, EstadoUsuario = "CURRENT" }, null, vistos: 3).Should().BeNull();

    // ---- Regla "no bajar nada" ----

    [Fact]
    public void NoEstaEnMal_SeEnviaLoDeseado()
        => ReglaMal.QueEnviar(new EstadoMal("watching", 14, 9), enMal: null).Should().Be(new EstadoMal("watching", 14, 9));

    [Fact]
    public void MalVaMasAdelantado_NoSeToca()
        => ReglaMal.QueEnviar(new EstadoMal("watching", 14, 9), new EstadoMal("watching", 20, 0)).Should().BeNull();

    [Fact]
    public void MalCompletadoYLaAppNo_NoSeToca()
        => ReglaMal.QueEnviar(new EstadoMal("watching", 28, 0), new EstadoMal("completed", 28, 8)).Should().BeNull();

    [Fact]
    public void LaAppVaMasAdelantada_SeEnvia()
        => ReglaMal.QueEnviar(new EstadoMal("watching", 14, 0), new EstadoMal("watching", 10, 0)).Should().Be(new EstadoMal("watching", 14, 0));

    [Fact]
    public void SinNotaEnLaApp_NoBorraLaDeMal()
        => ReglaMal.QueEnviar(new EstadoMal("completed", 28, 0), new EstadoMal("watching", 20, 8)).Should().Be(new EstadoMal("completed", 28, 8));

    [Fact]
    public void IgualQueEnMal_NoSeEnvia()
        => ReglaMal.QueEnviar(new EstadoMal("watching", 14, 9), new EstadoMal("watching", 14, 9)).Should().BeNull();

    [Fact]
    public void PlaneadoEnLaAppYEnMalYaLoEmpezo_NoSeToca()
        => ReglaMal.QueEnviar(new EstadoMal("plan_to_watch", 0, 0), new EstadoMal("watching", 3, 0)).Should().BeNull();

    // ---- PKCE ----

    [Fact]
    public void Verificador_LongitudYAlfabetoValidos()
    {
        string v = Pkce.NuevoVerificador();

        v.Length.Should().BeInRange(43, 128);
        v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~').Should().BeTrue();
        Pkce.NuevoVerificador().Should().NotBe(v);
    }

    [Fact]
    public void UrlDeAutorizacion_ConPkcePlainYStateYRedireccion()
    {
        var url = new Uri(Pkce.UrlAutorizacion("cliente123", "verif_ABC", "estado9", "http://localhost:5051/callback"));
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);

        url.GetLeftPart(UriPartial.Path).Should().Be("https://myanimelist.net/v1/oauth2/authorize");
        q["response_type"].Should().Be("code");
        q["client_id"].Should().Be("cliente123");
        q["code_challenge"].Should().Be("verif_ABC");
        q["code_challenge_method"].Should().Be("plain");
        q["state"].Should().Be("estado9");
        q["redirect_uri"].Should().Be("http://localhost:5051/callback");
    }
}
