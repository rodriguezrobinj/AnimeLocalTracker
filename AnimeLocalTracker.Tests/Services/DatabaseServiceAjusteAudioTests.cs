using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Sonido del reproductor guardado por anime o por capítulo (tabla AjusteAudio, migración v20).</summary>
public class DatabaseServiceAjusteAudioTests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Audio_{Guid.NewGuid():N}.db");
    private readonly DatabaseService _sut;

    public DatabaseServiceAjusteAudioTests() => _sut = new DatabaseService(_rutaDb);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static AjusteAudio Ajuste(int anime, int episodio, int volumen) => new()
    {
        Clave = AjusteAudio.ClaveDe(anime, episodio), AniListId = anime, NumeroEpisodio = episodio, Volumen = volumen,
        EcualizadorActivo = true, Ganancias = [6, 5.5, -3, 0, 0, 0, 0, 0, 0, 2], ModoNoche = true,
    };

    [Fact]
    public async Task SinNadaGuardado_DevuelveNull()
    {
        await _sut.InicializarBaseDatosAsync();

        (await _sut.ObtenerAjusteAudioAsync(101, 0)).Should().BeNull();
    }

    [Fact]
    public async Task GuardaYRecuperaTodosLosValores()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarAjusteAudioAsync(Ajuste(101, 3, 35));

        var leido = await _sut.ObtenerAjusteAudioAsync(101, 3);
        leido.Should().NotBeNull();
        leido!.Volumen.Should().Be(35);
        leido.EcualizadorActivo.Should().BeTrue();
        leido.ModoNoche.Should().BeTrue();
        leido.Ganancias.Should().Equal(6, 5.5, -3, 0, 0, 0, 0, 0, 0, 2);
    }

    [Fact]
    public async Task ElAnimeYCadaCapitulo_SeGuardanPorSeparado()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarAjusteAudioAsync(Ajuste(101, 0, 30));
        await _sut.GuardarAjusteAudioAsync(Ajuste(101, 3, 55));
        await _sut.GuardarAjusteAudioAsync(Ajuste(202, 0, 80));

        (await _sut.ObtenerAjusteAudioAsync(101, 0))!.Volumen.Should().Be(30);
        (await _sut.ObtenerAjusteAudioAsync(101, 3))!.Volumen.Should().Be(55);
        (await _sut.ObtenerAjusteAudioAsync(202, 0))!.Volumen.Should().Be(80);
        (await _sut.ObtenerAjusteAudioAsync(101, 4)).Should().BeNull();
    }

    [Fact]
    public async Task GuardarDeNuevo_SustituyeLoAnterior()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAjusteAudioAsync(Ajuste(101, 0, 30));

        await _sut.GuardarAjusteAudioAsync(Ajuste(101, 0, 70));

        (await _sut.ObtenerAjusteAudioAsync(101, 0))!.Volumen.Should().Be(70);
    }
}
