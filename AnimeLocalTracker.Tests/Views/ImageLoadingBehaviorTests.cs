using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Behaviors;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>El aviso de "imagen cargada" que oculta el brillo de carga en la ficha, Historial, Actualizaciones y Agregar.</summary>
[Collection("WpfSmoke")]
public class ImageLoadingBehaviorTests
{
    private readonly WpfHostFixture _host;

    public ImageLoadingBehaviorTests(WpfHostFixture host) => _host = host;

    private static BitmapSource Imagen() => new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);

    [Fact]
    public void CadaCambioDeSource_SeVuelveAEvaluar()
    {
        _host.Ejecutar(() =>
        {
            // En una lista virtualizada la misma Image se recicla con otra Source: hay que enterarse de cada cambio.
            var imagen = new Image();
            ImageLoadingBehavior.SetTrack(imagen, true);
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue("sin nada que cargar no hay que esperar");

            ImageLoadingBehavior.SetIsLoaded(imagen, false);
            imagen.Source = Imagen();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue();

            ImageLoadingBehavior.SetIsLoaded(imagen, false);
            imagen.Source = Imagen();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue("también el segundo cambio");
        });
    }

    [Fact]
    public void ConSourceYaPuesta_SeEvaluaAlEmpezarASeguirla()
    {
        _host.Ejecutar(() =>
        {
            var imagen = new Image { Source = Imagen() };
            ImageLoadingBehavior.SetIsLoaded(imagen, false);

            ImageLoadingBehavior.SetTrack(imagen, true);

            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue();
        });
    }
}
