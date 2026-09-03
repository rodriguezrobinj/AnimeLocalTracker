using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Gráfico de donut sin dependencias externas: construye los arcos con geometría WPF
/// (Path + ArcSegment). Usa <see cref="DonutDato"/> como ítems (Label, Valor, Color).
/// </summary>
public partial class DonutChart : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<DonutDato>), typeof(DonutChart),
            new PropertyMetadata(null, (d, e) => ((DonutChart)d).Redibujar()));

    public static readonly DependencyProperty CentroTextoProperty =
        DependencyProperty.Register(nameof(CentroTexto), typeof(string), typeof(DonutChart),
            new PropertyMetadata(string.Empty, (d, e) => ((DonutChart)d).CentroTextoBlock.Text = (string)e.NewValue));

    public static readonly DependencyProperty CentroSubtextoProperty =
        DependencyProperty.Register(nameof(CentroSubtexto), typeof(string), typeof(DonutChart),
            new PropertyMetadata(string.Empty, (d, e) => ((DonutChart)d).CentroSubtextoBlock.Text = (string)e.NewValue));

    public IEnumerable<DonutDato>? ItemsSource
    {
        get => (IEnumerable<DonutDato>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string CentroTexto
    {
        get => (string)GetValue(CentroTextoProperty);
        set => SetValue(CentroTextoProperty, value);
    }

    public string CentroSubtexto
    {
        get => (string)GetValue(CentroSubtextoProperty);
        set => SetValue(CentroSubtextoProperty, value);
    }

    public DonutChart()
    {
        InitializeComponent();
    }

    private void Redibujar()
    {
        DonutCanvas.Children.Clear();
        Leyenda.ItemsSource = null;

        var datos = ItemsSource?.Where(d => d.Valor > 0).ToList() ?? new List<DonutDato>();
        if (datos.Count == 0) return;

        double total = datos.Sum(d => d.Valor);
        if (total <= 0) return;

        // Marco de referencia: centro en (85,85), radio exterior 72, agujero 48
        const double cx = 85, cy = 85, radioExterior = 72, radioInterior = 48;

        double anguloActual = -90.0; // empezar arriba (12 en punto)

        foreach (var dato in datos)
        {
            double sweep = dato.Valor / total * 360.0;
            // Pequeña separación entre rebanadas para el look "dashboard"
            double sweepDibujo = Math.Max(1.0, sweep - 1.2);

            var brush = ConvertirColor(dato.Color);

            var path = new Path
            {
                Fill = brush,
                Data = CrearRebanada(cx, cy, radioExterior, radioInterior, anguloActual, anguloActual + sweepDibujo)
            };
            Canvas.SetLeft(path, 0);
            Canvas.SetTop(path, 0);
            DonutCanvas.Children.Add(path);

            anguloActual += sweep;
        }

        // Leyenda con porcentajes
        var leyenda = datos.Select(d => new DonutDato(d.Label, d.Valor, d.Color)
        {
            PorcentajeTexto = $"{d.Valor / total * 100.0:F0}%"
        }).ToList();
        Leyenda.ItemsSource = leyenda;
    }

    private static Geometry CrearRebanada(double cx, double cy, double rExt, double rInt, double a1, double a2)
    {
        double rad1 = GradosARadianes(a1);
        double rad2 = GradosARadianes(a2);

        var p1Ext = new Point(cx + rExt * Math.Cos(rad1), cy + rExt * Math.Sin(rad1));
        var p2Ext = new Point(cx + rExt * Math.Cos(rad2), cy + rExt * Math.Sin(rad2));
        var p1Int = new Point(cx + rInt * Math.Cos(rad1), cy + rInt * Math.Sin(rad1));
        var p2Int = new Point(cx + rInt * Math.Cos(rad2), cy + rInt * Math.Sin(rad2));

        bool granArco = (a2 - a1) > 180.0;

        var fig = new PathFigure { StartPoint = p1Ext, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(p2Ext, new Size(rExt, rExt), 0, granArco, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(p2Int, true));
        fig.Segments.Add(new ArcSegment(p1Int, new Size(rInt, rInt), 0, granArco, SweepDirection.Counterclockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private static double GradosARadianes(double grados) => grados * Math.PI / 180.0;

    private static SolidColorBrush ConvertirColor(string color)
    {
        try
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }
}

/// <summary>Ítem de un gráfico de donut.</summary>
public class DonutDato
{
    public string Label { get; }
    public double Valor { get; }
    public string Color { get; }
    public string PorcentajeTexto { get; set; } = string.Empty;

    public SolidColorBrush Brush
    {
        get
        {
            try
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(Color)!;
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Gray; }
        }
    }

    public DonutDato(string label, double valor, string color)
    {
        Label = label;
        Valor = valor;
        Color = color;
    }
}
