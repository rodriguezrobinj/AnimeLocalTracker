using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>"Deshacer" tras marcar episodios como vistos / no vistos: vuelve a dejar cada episodio como estaba, también el punto
/// donde se había quedado (marcar lo pone a cero).</summary>
public class DetalleDeshacerMarcadoTests
{
    private const int Id = 33;

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly List<List<RegistroEpisodio>> _guardados = new();

    private async Task<EpisodiosFichaViewModel> AbrirAsync(int total, int[] vistos, (int Ep, double Progreso)[]? aMedias = null)
    {
        var anime = new AnimeItem { AniListId = Id, Titulo = "Dandadan", TotalEpisodios = total, RutaCarpeta = @"C:\Anime\Dandadan" };
        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(new List<EpisodioItem>());

        var registros = vistos.Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList();
        foreach (var (ep, progreso) in aMedias ?? [])
            registros.Add(new RegistroEpisodio { AniListId = Id, NumeroEpisodio = ep, ProgresoSegundos = progreso, TotalSegundos = 1400, UltimaReproduccion = new DateTime(2026, 9, 20) });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id)).ReturnsAsync(registros);
        _db.Setup(d => d.GuardarRegistrosEpisodioBulkAsync(It.IsAny<IEnumerable<RegistroEpisodio>>()))
            .Returns((IEnumerable<RegistroEpisodio> r) => { _guardados.Add(r.ToList()); return Task.CompletedTask; });
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var ficha = new DetalleViewModel(Mock.Of<IAnimeTrackingService>(), _db.Object, Mock.Of<IAuthService>(), _escaner.Object, _dialogos.Object, _descargas.Object);
        await ficha.InicializarAsync(anime);
        ficha.Episodios.DuracionAvisoDeshacer = TimeSpan.FromSeconds(30);
        return ficha.Episodios;
    }

    private static EpisodioItem Ep(EpisodiosFichaViewModel sut, int n) => sut.Todos.First(e => e.NumeroEpisodio == n);

    [Fact]
    public async Task TrasMarcarTodosVistos_SeOfreceDeshacer_YDejaCadaEpisodioComoEstaba()
    {
        var sut = await AbrirAsync(total: 5, vistos: [1], aMedias: [(3, 420)]);

        await sut.MarcarTemporadaCompletaCommand.ExecuteAsync(null);
        sut.Todos.Should().OnlyContain(e => e.Visto);
        sut.PuedeDeshacerMarcado.Should().BeTrue();
        sut.DeshacerMarcadoTexto.Should().Be(string.Format(LocalizationService.T("Det_DeshacerVistosFormato"), 4));

        await sut.DeshacerMarcadoCommand.ExecuteAsync(null);

        sut.Todos.Where(e => e.Visto).Select(e => e.NumeroEpisodio).Should().Equal(1);
        Ep(sut, 3).ProgresoSegundos.Should().Be(420, "marcar lo puso a cero: deshacer devuelve el punto donde te quedaste");
        sut.PuedeDeshacerMarcado.Should().BeFalse();
        sut.Anime!.EpisodiosVistos.Should().Be(1);

        var ultimo = _guardados.Last();
        ultimo.Should().HaveCount(4).And.OnlyContain(r => !r.VistoLocal);
        ultimo.Single(r => r.NumeroEpisodio == 3).ProgresoSegundos.Should().Be(420);
    }

    [Fact]
    public async Task TrasMarcarNoVistos_DeshacerLosVuelveAMarcar()
    {
        var sut = await AbrirAsync(total: 3, vistos: [1, 2, 3]);

        await sut.MarcarNoVistosCommand.ExecuteAsync(null);
        sut.Todos.Should().OnlyContain(e => !e.Visto);
        sut.DeshacerMarcadoTexto.Should().Be(string.Format(LocalizationService.T("Det_DeshacerNoVistosFormato"), 3));

        await sut.DeshacerMarcadoCommand.ExecuteAsync(null);

        sut.Todos.Should().OnlyContain(e => e.Visto);
        sut.Anime!.EpisodiosVistos.Should().Be(3);
    }

    [Fact]
    public async Task ElAviso_SeVaSolo_YSePuedeCerrar()
    {
        var sut = await AbrirAsync(total: 3, vistos: []);
        sut.DuracionAvisoDeshacer = TimeSpan.FromMilliseconds(60);

        await sut.MarcarTemporadaCompletaCommand.ExecuteAsync(null);
        sut.PuedeDeshacerMarcado.Should().BeTrue();

        var limite = DateTime.UtcNow.AddSeconds(3);
        while (sut.PuedeDeshacerMarcado && DateTime.UtcNow < limite) await Task.Delay(10);
        sut.PuedeDeshacerMarcado.Should().BeFalse("pasado un rato ya no se ofrece");

        // Sin nada que deshacer, el comando no toca nada.
        int antes = _guardados.Count;
        await sut.DeshacerMarcadoCommand.ExecuteAsync(null);
        _guardados.Should().HaveCount(antes);
        sut.Todos.Should().OnlyContain(e => e.Visto);
    }

    [Fact]
    public async Task UnSegundoMarcado_SoloSeDeshaceElUltimo()
    {
        var sut = await AbrirAsync(total: 4, vistos: []);

        await sut.MarcarAnterioresVistosCommand.ExecuteAsync(Ep(sut, 2)); // 1 y 2
        await sut.MarcarTemporadaCompletaCommand.ExecuteAsync(null);      // 3 y 4

        await sut.DeshacerMarcadoCommand.ExecuteAsync(null);

        sut.Todos.Where(e => e.Visto).Select(e => e.NumeroEpisodio).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task CerrarElAviso_NoCambiaNada()
    {
        var sut = await AbrirAsync(total: 2, vistos: []);
        await sut.MarcarTemporadaCompletaCommand.ExecuteAsync(null);

        sut.OlvidarMarcadoAnteriorCommand.Execute(null);

        sut.PuedeDeshacerMarcado.Should().BeFalse();
        sut.Todos.Should().OnlyContain(e => e.Visto);
    }
}
