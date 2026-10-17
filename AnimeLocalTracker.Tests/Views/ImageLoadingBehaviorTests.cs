using System;
using System.Windows;
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

    /// <summary>
    /// Una imagen que se está descargando, sin red. No es un BitmapImage a propósito: una Source enlazada a una URL en texto
    /// llega como BitmapFrame, y también hay que esperarla.
    /// </summary>
    private sealed class DescargaFalsa : BitmapSource
    {
        private bool _descargando = true;
        private EventHandler? _completada;
        private EventHandler<ExceptionEventArgs>? _fallida;

        public override bool IsDownloading => _descargando;
        /// <summary>Cuántas veces se suscribió alguien al fin de la descarga (también la propia Image).</summary>
        public int Suscripciones { get; private set; }

        public override event EventHandler DownloadCompleted { add { Suscripciones++; _completada += value; } remove => _completada -= value; }
        public override event EventHandler<ExceptionEventArgs> DownloadFailed { add => _fallida += value; remove => _fallida -= value; }
        // La Image también se suscribe a este y el de la clase base lanza NotImplementedException en las derivadas.
        public override event EventHandler<ExceptionEventArgs> DecodeFailed { add { } remove { } }

        public void Terminar(bool conError = false)
        {
            _descargando = false;
            if (conError) _fallida?.Invoke(this, FalloDeRed());
            else _completada?.Invoke(this, EventArgs.Empty);
        }

        // WPF no deja crear sus argumentos de fallo (el constructor es interno).
        private static ExceptionEventArgs FalloDeRed() => (ExceptionEventArgs)Activator.CreateInstance(typeof(ExceptionEventArgs),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [new System.IO.IOException("sin red")], null)!;

        protected override Freezable CreateInstanceCore() => new DescargaFalsa();
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnaDescargaEnCurso_SeEsperaHastaQueTermine(bool conError)
    {
        _host.Ejecutar(() =>
        {
            var imagen = new Image();
            ImageLoadingBehavior.SetTrack(imagen, true);
            var descarga = new DescargaFalsa();

            imagen.Source = descarga;
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeFalse("el brillo sigue mientras llega la portada");

            descarga.Terminar(conError);
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue("si la descarga falla tampoco debe quedar el brillo");
        });
    }

    [Fact]
    public void SiLaImagenSeRecicla_ElFinDeLaDescargaAnteriorNoOcultaElBrilloDeLaNueva()
    {
        _host.Ejecutar(() =>
        {
            var imagen = new Image();
            ImageLoadingBehavior.SetTrack(imagen, true);
            var anterior = new DescargaFalsa();
            var nueva = new DescargaFalsa();

            imagen.Source = anterior;
            imagen.Source = nueva;
            anterior.Terminar();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeFalse("la portada que se ve aún no ha llegado");

            nueva.Terminar();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue();
        });
    }

    [Fact]
    public void SinSeguirla_NoReacciona_YAlVolverASeguirlaSeEvaluaConLaMismaSource()
    {
        _host.Ejecutar(() =>
        {
            var imagen = new Image { Source = Imagen() };
            ImageLoadingBehavior.SetTrack(imagen, true);
            ImageLoadingBehavior.SetTrack(imagen, false);

            ImageLoadingBehavior.SetIsLoaded(imagen, false);
            imagen.Source = Imagen();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeFalse("con Track=False ya no se sigue la Source");

            ImageLoadingBehavior.SetTrack(imagen, true);
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue("la Source no cambió al volver a seguirla, pero hay que evaluarla");
        });
    }

    [Fact]
    public void AlEmpezarASeguirla_ConUnaDescargaYaPuesta_SeEvaluaUnaSolaVez()
    {
        _host.Ejecutar(() =>
        {
            var descarga = new DescargaFalsa();
            var imagen = new Image { Source = descarga };
            int antes = descarga.Suscripciones;

            ImageLoadingBehavior.SetTrack(imagen, true);

            (descarga.Suscripciones - antes).Should().Be(1, "cada evaluación crea un temporizador y se suscribe a la descarga");
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeFalse();
            descarga.Terminar();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue();
        });
    }

    [Fact]
    public void AlDejarDeSeguirla_ConUnaDescargaEnCurso_ElBrilloNoSeQuedaParaSiempre()
    {
        _host.Ejecutar(() =>
        {
            var imagen = new Image();
            ImageLoadingBehavior.SetTrack(imagen, true);
            imagen.Source = new DescargaFalsa();
            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeFalse();

            ImageLoadingBehavior.SetTrack(imagen, false);

            ImageLoadingBehavior.GetIsLoaded(imagen).Should().BeTrue("ya nadie va a avisar del fin de la descarga");
        });
    }
}
