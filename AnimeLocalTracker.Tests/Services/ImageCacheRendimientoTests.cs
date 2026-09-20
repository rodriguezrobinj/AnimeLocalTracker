using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Carga de portadas: sin trabajo duplicado y sin tocar la carpeta real de datos del usuario.</summary>
public sealed class ImageCacheRendimientoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"covers_{Guid.NewGuid():N}");

    public ImageCacheRendimientoTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* limpieza best-effort */ }
    }

    private ImageCacheService CrearServicio()
    {
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        return new ImageCacheService(fabrica.Object, _carpeta);
    }

    /// <summary>Escribe una imagen válida (PNG con nombre .jpg: el decodificador se guía por el contenido).</summary>
    private void CrearPortada(int animeId)
    {
        var pixeles = new byte[16 * 16 * 4];
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixeles, 16 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(Path.Combine(_carpeta, $"{animeId}.jpg"));
        encoder.Save(fs);
    }

    [Fact]
    public async Task ObtenerPortadaAsync_ConPeticionesSimultaneasDelMismoAnime_DeberiaDecodificarUnaSolaVez()
    {
        // Antes: galería, "Qué veo hoy" y calendario pedían la misma portada a la vez y cada uno la
        // leía y decodificaba por su cuenta.
        CrearPortada(7);
        var sut = CrearServicio();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => sut.ObtenerPortadaAsync(7, null)));

        resultados.Should().OnlyContain(r => r != null);
        resultados.Distinct().Should().HaveCount(1, "todas las peticiones comparten la misma carga");
    }

    [Fact]
    public async Task ObtenerPortadaAsync_TrasCargar_DeberiaServirseDesdeMemoria()
    {
        CrearPortada(3);
        var sut = CrearServicio();

        var primera = await sut.ObtenerPortadaAsync(3, null);
        File.Delete(Path.Combine(_carpeta, "3.jpg")); // si volviera a leer el disco, fallaría
        var segunda = await sut.ObtenerPortadaAsync(3, null);

        segunda.Should().BeSameAs(primera);
        sut.ObtenerPortadaEnMemoria(3).Should().BeSameAs(primera);
    }

    [Fact]
    public async Task ObtenerPortadaAsync_SiFallaLaPrimeraVez_NoDeberiaDejarLaCargaAtascada()
    {
        var sut = CrearServicio();

        (await sut.ObtenerPortadaAsync(9, null)).Should().BeNull("todavía no existe el archivo ni hay URL");

        // La entrada "en curso" se libera al terminar: un intento posterior (cuando ya existe la portada) debe funcionar.
        CrearPortada(9);
        (await sut.ObtenerPortadaAsync(9, null)).Should().NotBeNull();
    }

    [Fact]
    public async Task ObtenerPortadaAsync_ConAnimesDistintos_DeberiaCargarCadaUnoPorSeparado()
    {
        CrearPortada(1);
        CrearPortada(2);
        var sut = CrearServicio();

        var resultados = await Task.WhenAll(sut.ObtenerPortadaAsync(1, null), sut.ObtenerPortadaAsync(2, null));

        resultados[0].Should().NotBeNull();
        resultados[1].Should().NotBeNull();
        resultados[0].Should().NotBeSameAs(resultados[1]);
    }
}
