using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Pasar de un capítulo a otro: la reanudación no deja ver el 0:00, una consulta que llega tarde no pisa al episodio más nuevo
/// (pulsar "siguiente" varias veces seguidas) y el cajón de episodios no se rehace en cada cambio.
/// </summary>
public class ReproductorCambioEpisodioTests
{
    private readonly Mock<IPlaybackStateService> _estado = new();

    private ReproductorViewModel CrearSut() => new(
        Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), playbackStateService: _estado.Object);

    private static List<EpisodioItem> Lista(int cuantos)
    {
        var lista = new List<EpisodioItem>();
        for (int i = 1; i <= cuantos; i++) lista.Add(new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep{i:00}.mkv" });
        return lista;
    }

    [Fact]
    public async Task AlReanudar_ElVideoQuedaTapado_YLaBarraYaMarcaElPuntoGuardado()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        using var sut = CrearSut();

        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);

        sut.OcultarVideoInicio.Should().BeTrue("se destapa cuando el salto al punto guardado se asienta, no antes");
        sut.CurrentSeconds.Should().Be(300);
        sut.TiempoCombinadoTexto.Should().Be("05:00 / 23:40");
    }

    [Fact]
    public async Task SinPuntoGuardado_ElVideoTambienEmpiezaTapado_HastaElPrimerFotograma()
    {
        using var sut = CrearSut();

        await sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);

        sut.OcultarVideoInicio.Should().BeTrue();
        sut.CurrentSeconds.Should().Be(0);
    }

    [Fact]
    public async Task UnaConsultaDeReanudacionQueLlegaTarde_NoPisaAlEpisodioMasNuevo()
    {
        // Episodio 5: la base de datos tarda; mientras, el usuario pulsa "siguiente" y el 6 se abre y termina antes.
        var lenta = new TaskCompletionSource<(double, double)?>();
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).Returns(lenta.Task);
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 6, It.IsAny<string?>())).ReturnsAsync(((double, double)?)null);
        using var sut = CrearSut();

        var cargaDel5 = sut.CargarVideoAsync("C:\\Anime\\Ep05.mkv", 101, "Frieren", 5);
        await sut.CargarVideoAsync("C:\\Anime\\Ep06.mkv", 101, "Frieren", 6);
        lenta.SetResult((900d, 1420d)); // el 5 tenía progreso en el 15:00
        await cargaDel5;

        sut.Episodio.Should().Be(6);
        sut.CurrentSeconds.Should().Be(0, "la posición guardada del 5 no debe aparecer en la barra del 6");
        sut.ResumingPositionSeconds.Should().Be(0);
    }

    [Fact]
    public void PasarAlSiguiente_NoRehaceLaListaDelCajon()
    {
        using var sut = CrearSut();
        sut.CargarVideo("C:\\Anime\\Ep01.mkv", 101, "One Piece", 1, Lista(50));
        var listaAntes = sut.EpisodiosDelCajon;
        int avisos = 0;
        sut.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReproductorViewModel.EpisodiosDelCajon)) avisos++; };

        sut.SiguienteEpisodio();
        sut.SiguienteEpisodio();
        sut.AnteriorEpisodio();

        sut.Episodio.Should().Be(2);
        sut.EpisodiosDelCajon.Should().BeSameAs(listaAntes, "antes se copiaba la lista y el cajón rehacía todas sus filas en cada cambio");
        avisos.Should().Be(0);
    }
}
