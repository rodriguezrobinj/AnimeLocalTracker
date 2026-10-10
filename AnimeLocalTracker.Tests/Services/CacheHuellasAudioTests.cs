using System;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed class CacheHuellasAudioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());
    private readonly string _huellas;

    public CacheHuellasAudioTests()
    {
        Directory.CreateDirectory(_carpeta);
        _huellas = Path.Combine(_carpeta, "huellas");
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private string Tema(string nombre, int bytes, int semilla)
    {
        string ruta = Path.Combine(_carpeta, nombre);
        var contenido = new byte[bytes];
        new Random(semilla).NextBytes(contenido);
        File.WriteAllBytes(ruta, contenido);
        return ruta;
    }

    [Fact]
    public void LaClave_DependeDelContenido_NoDelNombre()
    {
        string original = Tema("OP_OP1_v1_ep1-.mp3", 200_000, 1);
        string renombrado = Path.Combine(_carpeta, "OP1 - Canción.mp3");
        File.Copy(original, renombrado);
        string otro = Tema("ED1.mp3", 200_000, 2);

        CacheHuellasAudio.Clave(renombrado).Should().Be(CacheHuellasAudio.Clave(original), "renombrar el mp3 no obliga a recalcular");
        CacheHuellasAudio.Clave(otro).Should().NotBe(CacheHuellasAudio.Clave(original));
    }

    [Fact]
    public void LaClave_DeUnArchivoPequeno_TambienSeCalcula()
    {
        string a = Tema("corto.ogg", 1000, 1);
        string b = Tema("corto2.ogg", 1000, 2);

        CacheHuellasAudio.Clave(a).Should().NotBe(CacheHuellasAudio.Clave(b));
    }

    [Fact]
    public void LoGuardadoSeLeeIgual()
    {
        var cache = new CacheHuellasAudio(_huellas);
        float[] huella = AudioSintetico.Ruido(120, 5);

        cache.Guardar("abc", huella);

        cache.Leer("abc").Should().Equal(huella);
        cache.Leer("otra").Should().BeNull();
        Directory.GetFiles(_huellas).Should().ContainSingle().Which.Should().EndWith("abc.huella", "no queda el archivo temporal");
    }

    [Fact]
    public void UnArchivoDeCacheDanado_SeIgnora()
    {
        // Foco de revisión 3: un archivo vacío o cortado a medias (la app se cerró escribiendo) no puede dar una huella.
        var cache = new CacheHuellasAudio(_huellas);
        Directory.CreateDirectory(_huellas);
        File.WriteAllBytes(Path.Combine(_huellas, "vacia.huella"), []);
        File.WriteAllBytes(Path.Combine(_huellas, "cortada.huella"), new byte[33]);

        cache.Leer("vacia").Should().BeNull();
        cache.Leer("cortada").Should().BeNull("33 bytes no son un número entero de fotogramas (32 bytes cada uno)");
    }

    [Fact]
    public void AlGuardar_SeBorranLasHuellasDelFormatoAnterior()
    {
        Directory.CreateDirectory(_huellas);
        File.WriteAllBytes(Path.Combine(_huellas, "vieja.npy"), new byte[64]);
        var cache = new CacheHuellasAudio(_huellas);

        cache.Guardar("nueva", AudioSintetico.Ruido(10, 1));

        Directory.GetFiles(_huellas).Select(Path.GetFileName).Should().Equal("nueva.huella");
    }

    [Fact]
    public void AlPasarElTope_SeBorranLasMenosUsadas()
    {
        var cache = new CacheHuellasAudio(_huellas);
        float[] huella = AudioSintetico.Ruido(1, 1);
        var ahora = DateTime.UtcNow;
        for (int i = 0; i < CacheHuellasAudio.MaximoArchivos; i++)
        {
            cache.Guardar($"h{i:D4}", huella);
            // Cuanto mayor el número, más antigua la última vez que se usó.
            File.SetLastWriteTimeUtc(Path.Combine(_huellas, $"h{i:D4}.huella"), ahora.AddMinutes(-i));
        }

        cache.Guardar("reciente", huella);

        var quedan = Directory.GetFiles(_huellas).Select(Path.GetFileNameWithoutExtension).ToList();
        quedan.Should().HaveCount(CacheHuellasAudio.ArchivosTrasPoda);
        quedan.Should().Contain("reciente").And.Contain("h0000");
        quedan.Should().NotContain($"h{CacheHuellasAudio.MaximoArchivos - 1:D4}", "era la que más tiempo llevaba sin usarse");
    }

    [Fact]
    public void Leer_MarcaLaHuellaComoRecienUsada()
    {
        var cache = new CacheHuellasAudio(_huellas);
        cache.Guardar("abc", AudioSintetico.Ruido(10, 1));
        string archivo = Path.Combine(_huellas, "abc.huella");
        File.SetLastWriteTimeUtc(archivo, DateTime.UtcNow.AddDays(-30));

        cache.Leer("abc");

        File.GetLastWriteTimeUtc(archivo).Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
    }
}
