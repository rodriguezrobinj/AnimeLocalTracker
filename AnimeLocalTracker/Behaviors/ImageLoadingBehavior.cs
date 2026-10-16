using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AnimeLocalTracker.Behaviors;

/// <summary>
/// PERF-06: expone si una <see cref="Image"/> ya terminó de cargar su fuente actual, para que
/// un Border de "hueso" (shimmer) pueda ocultarse con Visibility en cuanto la imagen real esté
/// lista — ni "para siempre" (Calendario antes de esto: ~40 tarjetas sin virtualizar, shimmer
/// SIEMPRE visible, animando sin parar) ni con un tiempo fijo arbitrario (se probó y rompía
/// Galería: si la portada tardaba más que ese tiempo, el shimmer quedaba "congelado" en vez de
/// desaparecer o seguir animando). Se re-arma solo cuando el binding de Source cambia — necesario
/// en listas virtualizadas, donde el mismo Image se recicla para distintos items al hacer scroll.
/// </summary>
public static class ImageLoadingBehavior
{
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.RegisterAttached("Track", typeof(bool), typeof(ImageLoadingBehavior),
            new PropertyMetadata(false, OnTrackChanged));

    public static void SetTrack(Image element, bool value) => element.SetValue(TrackProperty, value);
    public static bool GetTrack(Image element) => (bool)element.GetValue(TrackProperty);

    public static readonly DependencyProperty IsLoadedProperty =
        DependencyProperty.RegisterAttached("IsLoaded", typeof(bool), typeof(ImageLoadingBehavior),
            new PropertyMetadata(false));

    public static void SetIsLoaded(Image element, bool value) => element.SetValue(IsLoadedProperty, value);
    public static bool GetIsLoaded(Image element) => (bool)element.GetValue(IsLoadedProperty);

    // Salvaguarda: si por lo que sea nunca llega DownloadCompleted/DownloadFailed (URL rara,
    // decodificador atascado), el shimmer no debe quedar animando para siempre.
    private static readonly TimeSpan TiempoMaximoEspera = TimeSpan.FromSeconds(8);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image || e.NewValue is not true) return;

        // Hay que enterarse de cada cambio de Source: en listas virtualizadas el mismo Image se recicla con otra Source al
        // desplazarse, sin recrearse (Track solo cambia una vez). Un enlace de la propia imagen a su Source vive y muere con
        // ella; AddValueChanged la dejaba registrada para siempre en una tabla global, con su imagen en memoria, cada vez que
        // una lista rehacía sus filas.
        BindingOperations.SetBinding(image, FuenteProperty, new Binding(nameof(Image.Source)) { RelativeSource = RelativeSource.Self });
        if (image.Source is null) ReaccionarACambioDeSource(image); // con Source ya puesta, el enlace acaba de avisar
    }

    private static readonly DependencyProperty FuenteProperty =
        DependencyProperty.RegisterAttached("Fuente", typeof(ImageSource), typeof(ImageLoadingBehavior),
            new PropertyMetadata(null, (d, _) => ReaccionarACambioDeSource((Image)d)));

    private static void ReaccionarACambioDeSource(Image image)
    {
        SetIsLoaded(image, false);

        if (image.Source is not BitmapImage bitmap)
        {
            // Sin nada que cargar (Source null u otro tipo de ImageSource ya resuelto): no hay
            // por qué esperar.
            SetIsLoaded(image, true);
            return;
        }

        if (!bitmap.IsDownloading)
        {
            // Ya decodificada — típico de archivos locales (miniaturas/portadas en disco), que
            // no pasan por el pipeline de descarga async: mostrarla ya, sin shimmer de por medio.
            SetIsLoaded(image, true);
            return;
        }

        var watchdog = new DispatcherTimer { Interval = TiempoMaximoEspera };

        void Completar()
        {
            watchdog.Stop();
            bitmap.DownloadCompleted -= OnCompleted;
            bitmap.DownloadFailed -= OnFailed;
            // Si entretanto la imagen se recicló con otra Source, este aviso es de la anterior: no oculta el brillo de la nueva.
            if (ReferenceEquals(image.Source, bitmap)) SetIsLoaded(image, true);
        }
        void OnCompleted(object? s, EventArgs e) => Completar();
        void OnFailed(object? s, ExceptionEventArgs e) => Completar();

        bitmap.DownloadCompleted += OnCompleted;
        bitmap.DownloadFailed += OnFailed;
        watchdog.Tick += (_, _) => Completar();
        watchdog.Start();
    }
}
