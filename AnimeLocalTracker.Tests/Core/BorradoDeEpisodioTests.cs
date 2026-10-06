using System;
using System.IO;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>El borrado de un episodio del disco (video + miniatura), compartido por el borrado manual y "Eliminar tras ver".</summary>
public sealed class BorradoDeEpisodioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt-borrado-" + Guid.NewGuid().ToString("N"));

    public BorradoDeEpisodioTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); }
        catch (IOException) { /* limpieza de una carpeta temporal */ }
    }

    private string CrearVideo(int bytes)
    {
        string ruta = Path.Combine(_carpeta, "Episodio 01.mp4");
        File.WriteAllBytes(ruta, new byte[bytes]);
        return ruta;
    }

    private static string CrearMiniatura(string rutaVideo)
    {
        string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideo);
        Directory.CreateDirectory(Path.GetDirectoryName(miniatura)!);
        File.WriteAllBytes(miniatura, [1, 2, 3]);
        return miniatura;
    }

    [Fact]
    public void BorraElVideoYDevuelveLosBytesLiberados()
    {
        string video = CrearVideo(2048);

        long bytes = BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        bytes.Should().Be(2048);
        File.Exists(video).Should().BeFalse();
    }

    [Fact]
    public void BorraTambienLaMiniatura()
    {
        string video = CrearVideo(10);
        string miniatura = CrearMiniatura(video);

        BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        File.Exists(miniatura).Should().BeFalse();
    }

    [Fact]
    public void VideoInexistente_DevuelveCeroSinLanzar()
    {
        string video = Path.Combine(_carpeta, "no-existe.mp4");

        BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1).Should().Be(0);
    }

    [Fact]
    public void VideoEnUso_LanzaYNoTocaLaMiniatura()
    {
        string video = CrearVideo(10);
        string miniatura = CrearMiniatura(video);
        using var abierto = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None);

        Action borrar = () => BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        borrar.Should().Throw<IOException>();
        File.Exists(video).Should().BeTrue();
        File.Exists(miniatura).Should().BeTrue("si el video no se pudo borrar, el episodio sigue como estaba");
    }
}
