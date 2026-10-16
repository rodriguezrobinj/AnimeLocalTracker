using System.Windows;
using System.Windows.Controls;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// El aro de "descargando" de toda la app (episodios en la ficha y en Actualizaciones, canciones en el panel de música): un
/// solo dibujo y una sola regla. Quien lo usa decide cuándo se ve (<c>Visibility</c>) y le pasa el avance de 0 a 100.
/// </summary>
public partial class IndicadorDescarga : UserControl
{
    public static readonly DependencyProperty ProgresoProperty = DependencyProperty.Register(
        nameof(Progreso), typeof(double), typeof(IndicadorDescarga),
        new PropertyMetadata(0.0, (d, _) => ((IndicadorDescarga)d).Actualizar()));

    /// <summary>Avance de la descarga, de 0 a 100.</summary>
    public double Progreso
    {
        get => (double)GetValue(ProgresoProperty);
        set => SetValue(ProgresoProperty, value);
    }

    private bool _construido;

    /// <summary>
    /// El dibujo se construye la primera vez que se ve, no al crearse: cada fila de la ficha y de Actualizaciones lleva uno y
    /// casi nunca se está descargando nada (construirlo en todas costaba ~12 ms por ficha, medido 2026-10-15).
    /// </summary>
    public IndicadorDescarga() => IsVisibleChanged += AlVerse;

    private void AlVerse(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        IsVisibleChanged -= AlVerse;
        InitializeComponent();
        _construido = true;
        Actualizar();
    }

    /// <summary>Sin avance todavía (en cola, buscando el enlace) o ya al 100 % pero sin terminar (descifrando, convirtiendo,
    /// moviendo el archivo): giro de espera. Entre medias, el aro con el porcentaje.</summary>
    internal static bool EnEspera(double progreso) => progreso <= 0 || progreso >= 100;

    private void Actualizar()
    {
        if (!_construido) return;
        bool espera = EnEspera(Progreso);
        Espera.Visibility = espera ? Visibility.Visible : Visibility.Collapsed;
        Avance.Visibility = espera ? Visibility.Collapsed : Visibility.Visible;
        Avance.Value = Progreso;
    }
}
