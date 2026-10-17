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
        if (d is not Image image) return;

        if (e.NewValue is not true)
        {
            BindingOperations.ClearBinding(image, FuenteProperty);
            CancelarEspera(image);
            // Sin seguimiento ya nadie avisará del fin de la descarga: el brillo no puede quedarse esperándolo.
            SetIsLoaded(image, true);
            return;
        }

        // Hay que enterarse de cada cambio de Source: en listas virtualizadas el mismo Image se recicla con otra Source al
        // desplazarse, sin recrearse (Track solo cambia una vez). Un enlace de la propia imagen a su Source vive y muere con
        // ella; AddValueChanged la dejaba registrada para siempre en una tabla global, con su imagen en memoria, cada vez que
        // una lista rehacía sus filas.
        var antes = image.GetValue(FuenteProperty);
        BindingOperations.SetBinding(image, FuenteProperty, new Binding(nameof(Image.Source)) { RelativeSource = RelativeSource.Self });
        // Si al enlazar la Source no cambió (null) no hubo aviso: se evalúa aquí. Si cambió, ya se evaluó una vez.
        if (ReferenceEquals(image.GetValue(FuenteProperty), antes)) ReaccionarACambioDeSource(image);
    }

    private static readonly DependencyProperty FuenteProperty =
        DependencyProperty.RegisterAttached("Fuente", typeof(ImageSource), typeof(ImageLoadingBehavior),
            new PropertyMetadata(null, (d, _) => { if (GetTrack((Image)d)) ReaccionarACambioDeSource((Image)d); }));

    /// <summary>Suelta la espera de la descarga de la Source anterior (temporizador y avisos del bitmap), si la hay.</summary>
    private static readonly DependencyProperty CancelarEsperaProperty =
        DependencyProperty.RegisterAttached("CancelarEspera", typeof(Action), typeof(ImageLoadingBehavior), new PropertyMetadata(null));

    private static void CancelarEspera(Image image) => (image.GetValue(CancelarEsperaProperty) as Action)?.Invoke();

    private static void ReaccionarACambioDeSource(Image image)
    {
        // Si la imagen se recicló con otra Source, la espera de la anterior ya no sirve: soltarla para que su temporizador y
        // sus avisos no retengan la imagen ni oculten el brillo de la nueva al terminar.
        CancelarEspera(image);
        SetIsLoaded(image, false);

        // BitmapSource y no BitmapImage: una Source enlazada a una ruta o URL en texto llega como BitmapFrame, que también se
        // descarga (portadas de AniList en Agregar anime).
        if (image.Source is not BitmapSource { IsDownloading: true } bitmap)
        {
            // Nada que esperar: sin Source, otro tipo de ImageSource o ya decodificada (archivos locales, que no pasan por la
            // descarga asíncrona). Mostrarla ya, sin brillo de por medio.
            SetIsLoaded(image, true);
            return;
        }

        var watchdog = new DispatcherTimer { Interval = TiempoMaximoEspera };

        void Soltar()
        {
            watchdog.Stop();
            bitmap.DownloadCompleted -= OnCompleted;
            bitmap.DownloadFailed -= OnFailed;
            image.ClearValue(CancelarEsperaProperty);
        }
        void Completar()
        {
            Soltar();
            SetIsLoaded(image, true);
        }
        void OnCompleted(object? s, EventArgs e) => Completar();
        void OnFailed(object? s, ExceptionEventArgs e) => Completar();

        bitmap.DownloadCompleted += OnCompleted;
        bitmap.DownloadFailed += OnFailed;
        watchdog.Tick += (_, _) => Completar();
        image.SetValue(CancelarEsperaProperty, (Action)Soltar);
        watchdog.Start();
    }
}
