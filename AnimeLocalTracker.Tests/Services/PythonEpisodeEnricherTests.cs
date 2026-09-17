using System;
using System.IO;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class PythonEpisodeEnricherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());

    public PythonEpisodeEnricherTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string CrearArchivo(byte[] contenido)
    {
        string path = Path.Combine(_tempDir, Guid.NewGuid() + ".jpg");
        File.WriteAllBytes(path, contenido);
        return path;
    }

    private static byte[] JpegValidoFalso(int relleno = 2000)
    {
        // SOI (FFD8FF...) + relleno + EOI (FFD9) — suficiente para pasar la validación
        // sin necesitar un JPEG real decodificable.
        var bytes = new byte[3 + relleno + 2];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    [Fact]
    public void EsMiniaturaValida_ConJpegCompleto_DeberiaSerValida()
    {
        string path = CrearArchivo(JpegValidoFalso());
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeTrue();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoInexistente_DeberiaSerInvalida()
    {
        string path = Path.Combine(_tempDir, "no_existe.jpg");
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoVacio_DeberiaSerInvalida()
    {
        string path = CrearArchivo(Array.Empty<byte>());
        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConJpegTruncado_DeberiaSerInvalida()
    {
        // BUG-02: proceso ffmpeg interrumpido a medias — tiene el SOI inicial pero el
        // archivo se corta antes del marcador EOI final. Este es exactamente el patrón
        // observado en miniaturas de ~900-1500 bytes que quedaban "atascadas" sin
        // regenerarse nunca, ni siquiera reintentando desde "Actualizar".
        var truncado = new byte[900];
        truncado[0] = 0xFF; truncado[1] = 0xD8; truncado[2] = 0xFF;
        // El resto queda en 0x00 — sin marcador EOI válido al final.
        string path = CrearArchivo(truncado);

        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void EsMiniaturaValida_ConArchivoDemasiadoPequeno_DeberiaSerInvalida()
    {
        // Aunque tenga los marcadores correctos, una miniatura real de 320px nunca pesa
        // unos pocos bytes.
        var minusculo = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        string path = CrearArchivo(minusculo);

        PythonEpisodeEnricher.EsMiniaturaValida(path).Should().BeFalse();
    }

    [Fact]
    public void ObtenerRutaMiniaturaSiExiste_ConMiniaturaTruncadaCacheada_DeberiaEliminarlaYDevolverNull()
    {
        // Simula el escenario reportado: una miniatura corrupta ya escrita en el caché
        // determinista (SHA-256 de la ruta del video) debe descartarse automáticamente,
        // no quedarse "atascada" para siempre.
        string rutaVideoFalsa = @"C:\Anime\Falso\Episodio 07.mp4";
        string rutaCache = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideoFalsa);
        Directory.CreateDirectory(Path.GetDirectoryName(rutaCache)!);

        var truncado = new byte[900];
        truncado[0] = 0xFF; truncado[1] = 0xD8; truncado[2] = 0xFF;
        File.WriteAllBytes(rutaCache, truncado);

        try
        {
            var resultado = PythonEpisodeEnricher.ObtenerRutaMiniaturaSiExiste(rutaVideoFalsa);

            resultado.Should().BeNull();
            File.Exists(rutaCache).Should().BeFalse("la miniatura corrupta debe borrarse para que se regenere");
        }
        finally
        {
            try { File.Delete(rutaCache); } catch { }
        }
    }
}
