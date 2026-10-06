using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Botón "Descargar temporada" de la ficha (docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md, parte 1): cuándo
/// aparece, qué dice, qué encola y cómo confirma.
/// </summary>
public sealed class DescargarTemporadaTests : IDisposable
{
    private const int Id = 21;

    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt-temporada-" + Guid.NewGuid().ToString("N"));

    public DescargarTemporadaTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); }
        catch (IOException) { /* limpieza de una carpeta temporal: si no se puede, el sistema la borrará */ }
    }

    /// <summary>Abre una ficha de <paramref name="total"/> episodios con esos archivos (de esos tamaños) en la carpeta temporal.</summary>
    private async Task<DetalleViewModel> AbrirFichaAsync(int total, Dictionary<int, int> enDisco, int[]? vistos = null, string? carpeta = null)
    {
        var anime = new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = carpeta ?? _carpeta };
        var encontrados = new List<EpisodioItem>();
        foreach (var (numero, bytes) in enDisco)
        {
            string ruta = Path.Combine(_carpeta, $"Episodio {numero}.mp4");
            File.WriteAllBytes(ruta, new byte[bytes]);
            encontrados.Add(new EpisodioItem { NumeroEpisodio = numero, RutaCompleta = ruta });
        }

        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(encontrados);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id))
            .ReturnsAsync((vistos ?? []).Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, _auth.Object, _escaner.Object, _dialogos.Object, _descargas.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    private List<int> CapturarDescargas()
    {
        var numeros = new List<int>();
        _descargas.Setup(d => d.IniciarDescargaEpisodioAsync(Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<int, string, string, int, IEnumerable<string>?>((_, _, _, n, _) => numeros.Add(n))
            .Returns(Task.CompletedTask);
        return numeros;
    }

    private void ResponderConfirmacion(bool respuesta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(respuesta);

    // ── El botón y su texto ──

    [Fact]
    public async Task Boton_CarpetaVacia_OfreceLaTemporadaCompleta()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: []);

        sut.Episodios.UltimoEmitido = 12;

        sut.Episodios.HayPendientesTemporada.Should().BeTrue();
        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaCompletaFormato"), 12));
    }

    [Fact]
    public async Task Boton_ConAlgunosEnDisco_OfreceLosQueFaltan()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: new() { [1] = 10, [2] = 10 });

        sut.Episodios.UltimoEmitido = 12;

        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaFaltanFormato"), 10));
    }

    [Fact]
    public async Task Boton_SerieEnEmision_SoloCuentaLosYaEmitidos()
    {
        var sut = await AbrirFichaAsync(total: 24, enDisco: []);

        sut.Episodios.UltimoEmitido = 9;

        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaCompletaFormato"), 9));
    }

    [Fact]
    public async Task Boton_TotalDesconocido_NoAparece()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: []);

        sut.Episodios.UltimoEmitido = 0;

        sut.Episodios.HayPendientesTemporada.Should().BeFalse();
        sut.Episodios.DescargarTemporadaTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task Boton_TodoVistoYSoloElUltimoEnDisco_NoAparece()
    {
        var sut = await AbrirFichaAsync(total: 50, enDisco: new() { [50] = 10 }, vistos: Enumerable.Range(1, 50).ToArray());

        sut.Episodios.UltimoEmitido = 50;

        sut.Episodios.HayPendientesTemporada.Should().BeFalse("lo visto y liberado no se vuelve a ofrecer");
    }

    // ── El comando ──

    [Fact]
    public async Task Comando_Pocos_EncolaEnOrdenSinPreguntar()
    {
        var sut = await AbrirFichaAsync(total: 6, enDisco: []);
        sut.Episodios.UltimoEmitido = 3;
        var numeros = CapturarDescargas();

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().Equal(1, 2, 3);
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Comando_AlEncolar_ElBotonDesapareceYUnSegundoClicNoDuplica()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: []);
        sut.Episodios.UltimoEmitido = 3;
        var numeros = CapturarDescargas();

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);
        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().Equal(1, 2, 3);
        sut.Episodios.HayPendientesTemporada.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 10)]
    public async Task Comando_Muchos_PreguntaAntesDePonerLaColaEnMarcha(bool confirma, int esperadas)
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: []);
        sut.Episodios.UltimoEmitido = 10;
        var numeros = CapturarDescargas();
        ResponderConfirmacion(confirma);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        numeros.Should().HaveCount(esperadas);
    }

    [Fact]
    public async Task Comando_SinArchivosDeReferencia_ConfirmaSoloConLaCantidad()
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: []);
        sut.Episodios.UltimoEmitido = 10;
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(false);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Be(string.Format(LocalizationService.T("Det_DescargarFaltantesConfirmacionFormato"), 10));
    }

    [Fact]
    public async Task Comando_ConArchivosDeReferencia_EstimaConElTamanoMedio()
    {
        // 2 archivos de 1000 y 3000 bytes → media 2000; 8 pendientes → 16000 bytes.
        var sut = await AbrirFichaAsync(total: 10, enDisco: new() { [1] = 1000, [2] = 3000 });
        sut.Episodios.UltimoEmitido = 10;
        sut.Episodios.EspacioLibreDe = _ => long.MaxValue;
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(false);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Be(string.Format(LocalizationService.T("Det_TemporadaConfirmacionFormato"), 8, AnimeLocalTracker.Core.Formato.Tamano(16000)));
    }

    [Fact]
    public async Task Comando_SiNoCabeEnElDisco_AvisaPeroNoBloquea()
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: new() { [1] = 1000, [2] = 3000 });
        sut.Episodios.UltimoEmitido = 10;
        sut.Episodios.EspacioLibreDe = _ => 5000;
        var numeros = CapturarDescargas();
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(true);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Contain(string.Format(LocalizationService.T("Det_TemporadaAvisoEspacioFormato"),
            AnimeLocalTracker.Core.Formato.Tamano(16000), AnimeLocalTracker.Core.Formato.Tamano(5000)));
        numeros.Should().HaveCount(8, "el aviso no impide descargar si el usuario acepta");
    }

    [Fact]
    public async Task Comando_SinCarpetaAsignada_AvisaYNoEncolaNada()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [], carpeta: "");
        sut.Episodios.UltimoEmitido = 4;
        var numeros = CapturarDescargas();
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().BeEmpty();
        _dialogos.Verify(d => d.MostrarDialogoAsync(
            LocalizationService.T("Det_AutoDescargaSinCarpetaTitulo"), LocalizationService.T("Det_TemporadaSinCarpetaMsj"),
            false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
