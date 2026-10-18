using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using AnimeLocalTracker.Services.Torrents;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed partial class ImportadorTorrentTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_importados_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
        try { Directory.Delete(_carpeta + "_origen", true); } catch { }
    }

    [Fact]
    public void RutaLocalPermitida_DentroDeLaCarpeta_DevuelveLaRuta()
    {
        string ruta = Path.Combine(_carpeta, "abc.torrent");

        ImportadorTorrent.RutaLocalPermitida(new Uri(ruta).AbsoluteUri, _carpeta).Should().Be(Path.GetFullPath(ruta));
    }

    [Theory]
    [InlineData("https://nyaa.si/download/1.torrent")]
    [InlineData("file:///C:/Windows/System32/x.torrent")]
    [InlineData("no es una url")]
    [InlineData(null)]
    public void RutaLocalPermitida_FueraDeLaCarpeta_Null(string? url)
    {
        ImportadorTorrent.RutaLocalPermitida(url, _carpeta).Should().BeNull();
    }

    [Fact]
    public void RutaLocalPermitida_ConPuntosPuntos_OOtraExtension_Null()
    {
        string escapada = Path.Combine(_carpeta, "..", "fuera.torrent");
        string otra = Path.Combine(_carpeta, "abc.exe");

        ImportadorTorrent.RutaLocalPermitida(new Uri(escapada).AbsoluteUri, _carpeta).Should().BeNull();
        ImportadorTorrent.RutaLocalPermitida(new Uri(otra).AbsoluteUri, _carpeta).Should().BeNull();
    }
}

public sealed partial class ImportadorTorrentTests
{
    private readonly Mock<ITorrentDownloadService> _motor = new();

    private ImportadorTorrent CrearSut() => new(_motor.Object, _carpeta);

    /// <summary>Un .torrent válido de varios archivos (bencode a mano: 1 pieza de 16 KiB, hash de pieza falso).</summary>
    private static byte[] TorrentDePrueba(string nombre, params (string Archivo, long Tamano)[] archivos)
    {
        static string S(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var sb = new StringBuilder("d4:infod5:filesl");
        foreach (var (archivo, tamano) in archivos) sb.Append($"d6:lengthi{tamano}e4:pathl{S(archivo)}ee");
        sb.Append($"e4:name{S(nombre)}12:piece lengthi16384e6:pieces20:");
        var cabecera = Encoding.UTF8.GetBytes(sb.ToString());
        return cabecera.Concat(new byte[20]).Concat(Encoding.ASCII.GetBytes("ee")).ToArray();
    }

    private string Escribir(byte[] datos, string nombre = "x.torrent")
    {
        Directory.CreateDirectory(_carpeta + "_origen");
        string ruta = Path.Combine(_carpeta + "_origen", nombre);
        File.WriteAllBytes(ruta, datos);
        return ruta;
    }

    [Fact]
    public async Task DesdeArchivo_Valido_SeGuardaEnLaCarpetaYDaSusArchivos()
    {
        string origen = Escribir(TorrentDePrueba("Frieren (01-02)", ("Frieren - 01.mkv", 1000), ("Frieren - 02.mkv", 2000)));

        var t = await CrearSut().DesdeArchivoAsync(origen, CancellationToken.None);

        t.Nombre.Should().Be("Frieren (01-02)");
        t.Archivos.Select(a => a.Tamano).Should().Equal(1000, 2000);
        File.Exists(t.RutaGuardada).Should().BeTrue();
        Path.GetDirectoryName(t.RutaGuardada).Should().Be(Path.GetFullPath(_carpeta));
        ImportadorTorrent.RutaLocalPermitida(t.Url, _carpeta).Should().Be(t.RutaGuardada);
        t.Tamano.Should().Be(3000);
    }

    [Fact]
    public async Task DesdeArchivo_NoEsTorrent_OEsEnorme_SeRechaza()
    {
        string basura = Escribir(Encoding.UTF8.GetBytes("hola"), "basura.torrent");
        string enorme = Escribir(new byte[6 * 1024 * 1024], "enorme.torrent");

        await FluentActions.Awaiting(() => CrearSut().DesdeArchivoAsync(basura, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        await FluentActions.Awaiting(() => CrearSut().DesdeArchivoAsync(enorme, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        Directory.Exists(_carpeta).Should().BeFalse("no se guarda nada si no es válido");
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Frieren", true)]
    [InlineData("  magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567  ", true)]
    [InlineData("magnet:?dn=sin-hash", false)]
    [InlineData("https://nyaa.si/view/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PareceMagnet(string? texto, bool esperado) => ImportadorTorrent.PareceMagnet(texto).Should().Be(esperado);

    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Frieren";

    [Fact]
    public async Task DesdeMagnet_ConMetadatos_SeGuardaIgual()
    {
        var datos = TorrentDePrueba("Frieren - 05", ("Frieren - 05.mkv", 500));
        _motor.Setup(m => m.ObtenerTorrentDeMagnetAsync(Magnet, It.IsAny<CancellationToken>())).ReturnsAsync(datos);

        var t = await CrearSut().DesdeMagnetAsync(Magnet, CancellationToken.None);

        t.Archivos.Should().ContainSingle();
        File.Exists(t.RutaGuardada).Should().BeTrue();
    }

    [Fact]
    public async Task DesdeMagnet_MalFormado_SinMetadatos_OCancelado_Lanza()
    {
        _motor.Setup(m => m.ObtenerTorrentDeMagnetAsync(Magnet, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await FluentActions.Awaiting(() => CrearSut().DesdeMagnetAsync("magnet:?dn=x", CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        await FluentActions.Awaiting(() => CrearSut().DesdeMagnetAsync(Magnet, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
    }

    [Fact]
    public void LimpiarHuerfanos_BorraSoloLosQueNoSeUsan()
    {
        Directory.CreateDirectory(_carpeta);
        string usado = Path.Combine(_carpeta, "a.torrent"), huerfano = Path.Combine(_carpeta, "b.torrent");
        File.WriteAllText(usado, "x");
        File.WriteAllText(huerfano, "x");

        int borrados = ImportadorTorrent.LimpiarHuerfanos(_carpeta, new HashSet<string>(new[] { Path.GetFullPath(usado) }, StringComparer.OrdinalIgnoreCase));

        borrados.Should().Be(1);
        File.Exists(usado).Should().BeTrue();
        File.Exists(huerfano).Should().BeFalse();
        ImportadorTorrent.LimpiarHuerfanos(Path.Combine(_carpeta, "no-existe"), new HashSet<string>()).Should().Be(0);
    }
}
