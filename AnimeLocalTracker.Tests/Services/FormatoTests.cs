using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class FormatoTests
{
    private const long Kb = 1024, Mb = 1024 * Kb, Gb = 1024 * Mb;

    /// <summary>El separador decimal es el del idioma de la app: "2,2 MB" en español, "2.2 MB" en inglés.</summary>
    private static string Decimal(string texto) => texto.Replace(".", LocalizationService.Cultura.NumberFormat.NumberDecimalSeparator);

    [Theory]
    [InlineData(-1L, "")]                    // tamaño desconocido
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "2 KB")]
    [InlineData(819L * Kb, "819 KB")]        // una canción muy corta
    [InlineData(2_354_761L, "2.2 MB")]       // un opening: con decimal, "2 MB" se leería igual para 1,6 que para 2,4
    [InlineData(10L * Mb, "10 MB")]
    [InlineData(350L * Mb, "350 MB")]        // un episodio: sin decimal
    [InlineData(3L * Gb, "3.0 GB")]
    [InlineData(1_503_238_553L, "1.4 GB")]
    public void Tamano_UsaLaUnidadYLosDecimalesQueTocan(long bytes, string esperado)
        => Formato.Tamano(bytes).Should().Be(Decimal(esperado));

    [Theory]
    [InlineData(1023L * Kb + 900, "1.0 MB")]         // no "1024 KB"
    [InlineData(9L * Mb + 990 * Kb, "10 MB")]        // no "10.0 MB"
    [InlineData(1023L * Mb + 900 * Kb, "1.0 GB")]    // no "1024 MB"
    public void Tamano_EnElLimiteDeUnaUnidad_PasaALaSiguiente(long bytes, string esperado)
        => Formato.Tamano(bytes).Should().Be(Decimal(esperado));
}
