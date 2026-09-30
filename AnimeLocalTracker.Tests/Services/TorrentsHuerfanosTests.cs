using System;
using System.Collections.Generic;
using System.IO;
using AnimeLocalTracker.Services;
using FluentAssertions;
using MonoTorrent;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Restos de torrents: carpetas temporales abandonadas (5 GB en el equipo del usuario) y el torrent
/// "ya registrado" en el motor que impedía borrarlas.
/// </summary>
public sealed class TorrentsHuerfanosTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "TorrentsHuerfanos_" + Guid.NewGuid().ToString("N"));

    public TorrentsHuerfanosTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* temporal */ }
    }

    private string CrearCarpeta(string nombre, int bytes)
    {
        string ruta = Path.Combine(_base, nombre);
        Directory.CreateDirectory(Path.Combine(ruta, "sub"));
        File.WriteAllBytes(Path.Combine(ruta, "sub", "video.mkv"), new byte[bytes]);
        return ruta;
    }

    [Fact]
    public void InfoHashes_IgualesPorValor()
    {
        // El arreglo de "A manager for this torrent has already been registered" compara con Equals: tiene
        // que ser por valor (dos lecturas del mismo .torrent), no por referencia.
        const string hex = "0123456789abcdef0123456789abcdef01234567";
        var a = new InfoHashes(InfoHash.FromHex(hex), null);
        var b = new InfoHashes(InfoHash.FromHex(hex), null);

        Equals(a, b).Should().BeTrue();
    }

    [Fact]
    public void Limpiar_BorraLasCarpetasQueNoEstanEnLaCola_YConservaLasDeLaCola()
    {
        CrearCarpeta("141902_1", 3000);
        CrearCarpeta("198946_12", 2000);
        string enCola = CrearCarpeta("200000_5", 1000);

        var resultado = LimpiezaTorrentsHuerfanos.Limpiar(_base, new HashSet<string> { "200000_5" });

        Directory.Exists(Path.Combine(_base, "141902_1")).Should().BeFalse();
        Directory.Exists(Path.Combine(_base, "198946_12")).Should().BeFalse();
        Directory.Exists(enCola).Should().BeTrue("es una descarga en pausa o pendiente: reanudarla retoma esas piezas");
        resultado.Carpetas.Should().Be(2);
        resultado.Bytes.Should().Be(5000);
    }

    [Fact]
    public void Limpiar_CarpetaEnUso_SeSaltaSinFallar()
    {
        string ruta = CrearCarpeta("300000_1", 100);
        using var abierto = new FileStream(Path.Combine(ruta, "sub", "video.mkv"), FileMode.Open, FileAccess.Read, FileShare.None);

        var resultado = LimpiezaTorrentsHuerfanos.Limpiar(_base, new HashSet<string>());

        resultado.Carpetas.Should().Be(0);
        Directory.Exists(ruta).Should().BeTrue();
    }

    [Fact]
    public void Limpiar_SinCarpeta_NoHaceNada()
    {
        var resultado = LimpiezaTorrentsHuerfanos.Limpiar(Path.Combine(_base, "no-existe"), new HashSet<string>());

        resultado.Carpetas.Should().Be(0);
    }
}
