using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

public class EntradaSeguraTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "alt_base");

    [Theory]
    [InlineData("video.mkv", true)]
    [InlineData("sub/carpeta/video.mkv", true)]
    [InlineData("sub/../video.mkv", true)]              // se normaliza y sigue dentro
    [InlineData("../fuera.mkv", false)]                 // sale de la carpeta
    [InlineData("sub/../../fuera.mkv", false)]
    [InlineData("../alt_base2/video.mkv", false)]       // hermana con prefijo parecido
    [InlineData("", false)]
    public void EstaDentroDe_DeberiaJuzgarLaRutaNormalizada(string relativa, bool esperado)
    {
        string ruta = relativa.Length == 0 ? "" : Path.Combine(Base, relativa);

        EntradaSegura.EstaDentroDe(Base, ruta).Should().Be(esperado);
    }

    [Fact]
    public void EstaDentroDe_LaPropiaCarpeta_DeberiaContarComoDentro()
    {
        EntradaSegura.EstaDentroDe(Base, Base).Should().BeTrue();
        EntradaSegura.EstaDentroDe(Base, Base + Path.DirectorySeparatorChar).Should().BeTrue();
    }

    [Fact]
    public void EstaDentroDe_ConOtraRutaAbsoluta_DeberiaRechazar()
    {
        string otra = Path.Combine(Path.GetTempPath(), "otra_carpeta", "video.mkv");

        EntradaSegura.EstaDentroDe(Base, otra).Should().BeFalse();
    }

    [Fact]
    public void EstaDentroDe_IgnoraMayusculasComoWindows()
    {
        EntradaSegura.EstaDentroDe(Base.ToUpperInvariant(), Path.Combine(Base.ToLowerInvariant(), "v.mkv")).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void EstaDentroDe_ConBaseVacia_DeberiaRechazar(string? baseVacia)
    {
        EntradaSegura.EstaDentroDe(baseVacia!, Path.Combine(Base, "v.mkv")).Should().BeFalse();
    }

    [Fact]
    public void EstaDentroDe_ConCaracteresInvalidos_NoDeberiaLanzar()
    {
        // "\0" no es válido en rutas: debe rechazarse, no reventar
        Action accion = () => EntradaSegura.EstaDentroDe(Base, "C:\\a\0b").Should().BeFalse();

        accion.Should().NotThrow();
    }

    // === LeerAcotadoAsync ===

    [Fact]
    public async Task LeerAcotadoAsync_ConCuerpoDentroDelTope_DeberiaDevolverLosBytes()
    {
        byte[] datos = Encoding.UTF8.GetBytes("d8:announce...");
        using var contenido = new ByteArrayContent(datos);

        var leidos = await EntradaSegura.LeerAcotadoAsync(contenido, 1024);

        leidos.Should().Equal(datos);
    }

    [Fact]
    public async Task LeerAcotadoAsync_ConContentLengthMayorQueElTope_DeberiaRechazarSinLeer()
    {
        using var contenido = new ByteArrayContent(new byte[2048]);

        Func<Task> accion = () => EntradaSegura.LeerAcotadoAsync(contenido, 1024);

        await accion.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LeerAcotadoAsync_SiElServidorNoDeclaraLongitudYSeExcede_DeberiaCortar()
    {
        // Flujo sin Content-Length (chunked): solo se detecta leyendo
        using var contenido = new StreamContent(new SinLongitudStream(new byte[5000]));

        Func<Task> accion = () => EntradaSegura.LeerAcotadoAsync(contenido, 1024);

        await accion.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LeerAcotadoAsync_ConExactamenteElTope_DeberiaAceptar()
    {
        using var contenido = new ByteArrayContent(new byte[1024]);

        var leidos = await EntradaSegura.LeerAcotadoAsync(contenido, 1024);

        leidos.Length.Should().Be(1024);
    }

    /// <summary>Stream que no anuncia su longitud (como una respuesta chunked).</summary>
    private sealed class SinLongitudStream : MemoryStream
    {
        public SinLongitudStream(byte[] datos) : base(datos) { }

        // No seekable => StreamContent no puede calcular Content-Length, igual que una respuesta chunked
        public override bool CanSeek => false;
    }
}
