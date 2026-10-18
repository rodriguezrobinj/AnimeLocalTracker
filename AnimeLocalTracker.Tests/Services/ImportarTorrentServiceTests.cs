using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Torrents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Cuadro "Descargar torrent": preselección, recálculo, qué se encola y cuándo se ignora una entrada.</summary>
public sealed class ImportarTorrentServiceTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "alt_imp_srv_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<ITorrentDownloadService> _motor = new();
    private readonly AnimeItem _frieren, _dandadan;

    public ImportarTorrentServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "Frieren"));
        _frieren = new AnimeItem { AniListId = 1, Titulo = "Sousou no Frieren", RutaCarpeta = Path.Combine(_raiz, "Frieren") };
        _dandadan = new AnimeItem { AniListId = 4, Titulo = "Dandadan", RutaCarpeta = "" };
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(() => new List<AnimeItem> { _frieren, _dandadan });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_raiz, true); } catch { }
    }

    private ImportarTorrentService CrearSut() =>
        new(new ImportadorTorrent(_motor.Object, Path.Combine(_raiz, "importados")), _db.Object, _descargas.Object, _dialogos.Object);

    private string TorrentEnDisco(string nombre, params string[] archivos)
    {
        static string S(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var sb = new StringBuilder("d4:infod5:filesl");
        foreach (var a in archivos) sb.Append($"d6:lengthi100e4:pathl{S(a)}ee");
        sb.Append($"e4:name{S(nombre)}12:piece lengthi16384e6:pieces20:");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString()).Concat(new byte[20]).Concat(Encoding.ASCII.GetBytes("ee")).ToArray();
        string ruta = Path.Combine(_raiz, Guid.NewGuid().ToString("N") + ".torrent");
        File.WriteAllBytes(ruta, bytes);
        return ruta;
    }

    private string LoteFrieren() => TorrentEnDisco("[SubsPlease] Sousou no Frieren (01-03) (1080p)",
        "Sousou no Frieren - 01 (1080p).mkv", "Sousou no Frieren - 02 (1080p).mkv", "Sousou no Frieren - 03 (1080p).mkv");

    [Fact]
    public async Task Importar_ReconoceElAnime_YCuentaLosEpisodios()
    {
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.Visible.Should().BeTrue();
        sut.AnimeElegido.Should().BeSameAs(_frieren);
        sut.TextoEpisodios.Should().Be(string.Format(LocalizationService.T("Desc_ImportarEpisodiosFormato"), "1–3", 3));
        sut.PuedeDescargar.Should().BeTrue();
    }

    [Fact]
    public async Task YaLoTiene_EnBaseEnDescargaOArchivoDeOtraExtension_Cuenta()
    {
        string ep1 = Path.Combine(_frieren.RutaCarpeta, "cualquiera.mkv");
        File.WriteAllText(ep1, "x");
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(1)).ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 1, NumeroEpisodio = 1, RutaArchivo = ep1 } });
        double p;
        _descargas.Setup(d => d.EstaDescargando(1, 2, out p)).Returns(true);
        File.WriteAllText(Path.Combine(_frieren.RutaCarpeta, "Episodio 03.mkv"), "x");
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.TextoDetalle.Should().Contain(string.Format(LocalizationService.T("Desc_ImportarYaTienesFormato"), 3));
        sut.TextoDetalle.Should().Contain(LocalizationService.T("Desc_ImportarNingunoNuevo"));
        sut.PuedeDescargar.Should().BeFalse();
    }

    [Fact]
    public async Task SinAnimeOAnimeSinCarpeta_NoDejaDescargar()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(TorrentEnDisco("[X] Algo que no esta - 01", "Algo - 01.mkv"));
        await sut.Recalculo;
        sut.AnimeElegido.Should().BeNull();
        sut.PuedeDescargar.Should().BeFalse();

        sut.AnimeElegido = _dandadan;
        await sut.Recalculo;
        sut.TextoDetalle.Should().Contain(LocalizationService.T("Desc_ImportarSinCarpeta"));
        sut.PuedeDescargar.Should().BeFalse();
    }

    [Fact]
    public async Task Descargar_EncolaUnoPorEpisodioNuevo_YCierra()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        await sut.DescargarCommand.ExecuteAsync(null);

        foreach (int ep in new[] { 1, 2, 3 })
            _descargas.Verify(d => d.IniciarDescargaTorrentManualAsync(1, "Sousou no Frieren", _frieren.RutaCarpeta, ep,
                It.Is<CandidatoTorrent>(c => c.TorrentUrl.StartsWith("file:") && c.EsBatch), It.IsAny<IEnumerable<string>?>()), Times.Once);
        sut.Visible.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelar_NoEncolaNada()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.CancelarCommand.Execute(null);

        sut.Visible.Should().BeFalse();
        _descargas.Verify(d => d.IniciarDescargaTorrentManualAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CandidatoTorrent>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Importar_ConElCuadroAbierto_SeIgnora()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        await sut.ImportarArchivoAsync(TorrentEnDisco("[SubsPlease] Dandadan - 01", "Dandadan - 01.mkv"));

        sut.AnimeElegido.Should().BeSameAs(_frieren);
    }

    [Fact]
    public async Task ArchivoNoValido_AvisaYNoAbre()
    {
        string basura = Path.Combine(_raiz, "basura.torrent");
        File.WriteAllText(basura, "hola");
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(basura);

        sut.Visible.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Desc_ImportarErrorTitulo"), LocalizationService.T("Desc_ImportarNoValido"),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TorrentSinVideos_AvisaYNoAbre()
    {
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(TorrentEnDisco("OST", "01.flac"));

        sut.Visible.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), LocalizationService.T("Desc_ImportarSinVideos"), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // Un fallo que no es del torrent (la base, un título raro de la biblioteca) también se le dice al usuario: antes solo quedaba
    // en el registro y el cuadro no aparecía sin explicación.
    [Fact]
    public async Task ErrorInesperado_AvisaYNoAbre()
    {
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ThrowsAsync(new System.FormatException("roto"));
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(LoteFrieren());

        sut.Visible.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Desc_ImportarErrorTitulo"), LocalizationService.T("Desc_ImportarErrorInesperado"),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
