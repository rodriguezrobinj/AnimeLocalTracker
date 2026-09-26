using System;
using System.Collections.Generic;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Aviso de "la pista llegó sola al final" (base de la reproducción continua) y duración conocida del archivo.</summary>
public class ControlReproduccionTemasContinuaTests : IDisposable
{
    private readonly FakeAudioTrackPlayer _player = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private ControlReproduccionTemas CrearSut() => new(_player, usarTemporizador: false);

    private static TemaAnimeItem Tema(string slug = "OP1") => new()
    {
        Info = new AnimeThemeInfo { Slug = slug, Tipo = "OP", AudioUrlOgg = "https://a.animethemes.moe/x.ogg" }
    };

    [Fact]
    public void PistaTerminada_SeDisparaAlLlegarAlFinalConElTemaQueSonaba()
    {
        var sut = CrearSut();
        var tema = Tema();
        var avisados = new List<TemaAnimeItem>();
        sut.PistaTerminada += (_, t) => avisados.Add(t);
        sut.Alternar(tema, "a.mp3");

        _player.DispararTerminado();

        avisados.Should().ContainSingle().Which.Should().BeSameAs(tema);
    }

    [Fact]
    public void AlTerminar_DeberiaPausarElReproductorAntesDeVolverAlPrincipio_ParaQueNoVuelvaASonar()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        _player.Llamadas.Clear();

        _player.DispararTerminado();

        _player.Llamadas.Should().Contain("Pausar", "MediaPlayer sigue en estado reproduciendo al llegar al final y volvería a sonar al mover la posición");
        _player.Posicion.Should().Be(TimeSpan.Zero);
        tema.Reproduciendo.Should().BeFalse();
    }

    [Fact]
    public void PistaTerminada_NoSeDisparaAlPausarNiAlDetener()
    {
        var sut = CrearSut();
        var tema = Tema();
        int avisos = 0;
        sut.PistaTerminada += (_, _) => avisos++;
        sut.Alternar(tema, "a.mp3");

        sut.Alternar(tema, "a.mp3"); // pausa
        sut.Detener();

        avisos.Should().Be(0, "solo cuando la canción termina por sí sola");
    }

    [Fact]
    public void PistaTerminada_SinPistaActual_NoHaceNada()
    {
        var sut = CrearSut();
        int avisos = 0;
        sut.PistaTerminada += (_, _) => avisos++;

        _player.DispararTerminado();

        avisos.Should().Be(0);
    }

    [Fact]
    public void PistaTerminada_ElReceptorPuedePonerOtraPistaSinRomperElControl()
    {
        var sut = CrearSut();
        var primero = Tema("OP1");
        var segundo = Tema("ED1");
        sut.PistaTerminada += (_, _) => sut.Alternar(segundo, "b.mp3");
        sut.Alternar(primero, "a.mp3");

        _player.DispararTerminado();

        sut.Actual.Should().BeSameAs(segundo);
        primero.EsPistaActual.Should().BeFalse();
        segundo.Reproduciendo.Should().BeTrue();
        _player.RutaAbierta.Should().Be("b.mp3");
    }

    [Fact]
    public void DuracionDelArchivo_SiNoSeConocia_SeCompletaAlAbrirLaPista_YSiSeConociaNoSePisa()
    {
        var sut = CrearSut();
        var desconocida = Tema("OP1");
        var conocida = Tema("ED1");
        conocida.DuracionArchivoSegundos = 88.5;

        sut.Alternar(desconocida, "a.mp3");
        desconocida.DuracionArchivoSegundos.Should().Be(90);

        sut.Alternar(conocida, "b.mp3");
        conocida.DuracionArchivoSegundos.Should().Be(88.5, "el valor leído con ffprobe se respeta");
    }
}

public class TemaAnimeItemDuracionTests
{
    private static TemaAnimeItem Nuevo() => new() { Info = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a/x.ogg" } };

    [Fact]
    public void SinDuracion_NoDeberiaMostrarNada()
    {
        var t = Nuevo();

        t.TieneDuracionArchivo.Should().BeFalse();
        t.DuracionArchivoTexto.Should().BeEmpty();
    }

    [Theory]
    [InlineData(92.136, "1:32")]
    [InlineData(59.4, "0:59")]
    [InlineData(600, "10:00")]
    public void ConDuracion_DeberiaMostrarMinutosYSegundos(double segundos, string esperado)
    {
        var t = Nuevo();

        t.DuracionArchivoSegundos = segundos;

        t.TieneDuracionArchivo.Should().BeTrue();
        t.DuracionArchivoTexto.Should().Be(esperado);
    }

    [Fact]
    public void AlCambiarLaDuracion_DeberiaNotificarLosCamposDerivados()
    {
        var t = Nuevo();
        var avisadas = new List<string?>();
        t.PropertyChanged += (_, e) => avisadas.Add(e.PropertyName);

        t.DuracionArchivoSegundos = 75;

        avisadas.Should().Contain([nameof(TemaAnimeItem.TieneDuracionArchivo), nameof(TemaAnimeItem.DuracionArchivoTexto)]);
    }

    [Fact]
    public void LaDuracionDelArchivo_EsIndependienteDeLaPosicionDeLaBarra()
    {
        var t = Nuevo();

        t.DuracionArchivoSegundos = 92;
        t.DuracionSegundos = 0;
        t.PosicionSegundos = 0;

        t.DuracionArchivoTexto.Should().Be("1:32");
    }
}
