using System.Collections.Generic;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Models;

/// <summary>
/// El badge técnico ("1080p · AV1 · 10bit") se calcula a partir de varias propiedades: al borrar el archivo de un episodio la
/// fila debe enterarse de que cambió, o el texto se queda en pantalla hasta recargar la pestaña.
/// </summary>
public class EpisodioItemMetadatosTests
{
    [Fact]
    public void QuitarArchivo_AvisaDeQueElBadgeTecnicoCambio_YQuedaVacio()
    {
        var item = new EpisodioItem { Resolucion = "1080p", CodecVideo = "av1", Es10Bit = true, Fps = "24/1" };
        item.BadgeTecnico.Should().Be("1080p · AV1 · 10bit · 24fps");
        var avisados = new List<string?>();
        item.PropertyChanged += (_, e) => avisados.Add(e.PropertyName);

        item.QuitarArchivo();

        avisados.Should().Contain(nameof(EpisodioItem.BadgeTecnico));
        item.BadgeTecnico.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(EpisodioItem.Resolucion))]
    [InlineData(nameof(EpisodioItem.CodecVideo))]
    [InlineData(nameof(EpisodioItem.Fps))]
    [InlineData(nameof(EpisodioItem.Es10Bit))]
    public void CadaDatoTecnico_AvisaDeQueElBadgeCambio(string propiedad)
    {
        var item = new EpisodioItem();
        var avisados = new List<string?>();
        item.PropertyChanged += (_, e) => avisados.Add(e.PropertyName);

        switch (propiedad)
        {
            case nameof(EpisodioItem.Resolucion): item.Resolucion = "720p"; break;
            case nameof(EpisodioItem.CodecVideo): item.CodecVideo = "hevc"; break;
            case nameof(EpisodioItem.Fps): item.Fps = "24/1"; break;
            case nameof(EpisodioItem.Es10Bit): item.Es10Bit = true; break;
        }

        avisados.Should().Contain(nameof(EpisodioItem.BadgeTecnico));
    }
}
