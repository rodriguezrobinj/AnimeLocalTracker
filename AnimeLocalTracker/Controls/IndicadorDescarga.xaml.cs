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

    public IndicadorDescarga()
    {
        InitializeComponent();
        Actualizar();
    }

    /// <summary>Sin avance todavía (en cola, buscando el enlace) o ya al 100 % pero sin terminar (descifrando, convirtiendo,
    /// moviendo el archivo): giro de espera. Entre medias, el aro con el porcentaje.</summary>
    internal static bool EnEspera(double progreso) => progreso <= 0 || progreso >= 100;

    private void Actualizar()
    {
        bool espera = EnEspera(Progreso);
        Espera.Visibility = espera ? Visibility.Visible : Visibility.Collapsed;
        Avance.Visibility = espera ? Visibility.Collapsed : Visibility.Visible;
        Avance.Value = Progreso;
    }
}
