using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Converters;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>Imagen de "Adivina el personaje": reducida a N píxeles de ancho (bloques) o entera con 0.</summary>
public sealed class PersonajePixeladoConverterTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Pixelado_{Guid.NewGuid():N}");
    private readonly string _ruta;

    public PersonajePixeladoConverterTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ruta = Path.Combine(_carpeta, "personaje.png");
        GuardarPng(_ruta, 230, 345);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        ArchivosTemporales.BorrarCarpeta(_carpeta);  // WPF suelta el archivo que no pudo decodificar un instante después
    }

    private static void GuardarPng(string ruta, int ancho, int alto)
    {
        var pixeles = new byte[ancho * alto * 3];
        for (int i = 0; i < pixeles.Length; i++) pixeles[i] = (byte)(i % 251);
        var fuente = BitmapSource.Create(ancho, alto, 96, 96, PixelFormats.Rgb24, null, pixeles, ancho * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(fuente));
        using var stream = File.Create(ruta);
        encoder.Save(stream);
    }

    private static object? Convertir(params object[] valores) =>
        new PersonajePixeladoConverter().Convert(valores, typeof(ImageSource), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void ConLado_ReduceLaImagenAEsteAncho()
    {
        var imagen = Convertir(_ruta, 10).Should().BeAssignableTo<BitmapSource>().Subject;

        imagen.PixelWidth.Should().Be(10);
        imagen.IsFrozen.Should().BeTrue("se comparte entre hilos y no se vuelve a modificar");
    }

    [Fact]
    public void ConLadoCero_DevuelveLaImagenEntera()
    {
        var imagen = Convertir(_ruta, 0).Should().BeAssignableTo<BitmapSource>().Subject;

        imagen.PixelWidth.Should().Be(230);
        imagen.PixelHeight.Should().Be(345);
    }

    [Fact]
    public void NoBloqueaElArchivo_PuedeBorrarseTrasCargarlo()
    {
        Convertir(_ruta, 10);

        Action borrar = () => File.Delete(_ruta);

        borrar.Should().NotThrow("la imagen se lee completa y se suelta el archivo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\no\\existe\\personaje.png")]
    public void SinRutaOConArchivoInexistente_DevuelveNull(string? ruta) =>
        Convertir(ruta!, 10).Should().BeNull();

    [Fact]
    public void ConArchivoQueNoEsImagen_DevuelveNullEnLugarDeLanzar()
    {
        string falso = Path.Combine(_carpeta, "falso.png");
        File.WriteAllText(falso, "esto no es una imagen");

        Convertir(falso, 10).Should().BeNull();
    }

    [Fact]
    public void ConMenosValoresDeLosNecesarios_DevuelveNull() =>
        Convertir(_ruta).Should().BeNull();
}
