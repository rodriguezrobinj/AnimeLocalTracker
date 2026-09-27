using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Dibuja la línea de la barra de progreso del reproductor con el opening, el ending y el resumen marcados: cada tramo es una franja
/// de color del mismo grosor que la barra (crece igual al pasar el cursor, ver <see cref="Grosor"/>); la parte ya reproducida es
/// blanca y, dentro de un tramo, toma su color; y la bolita del deslizador se tiñe del color del tramo en el que está.
/// No recibe clics: la barra y su bolita siguen funcionando debajo. Las posiciones se calculan igual que el deslizador (el centro de
/// la bolita recorre el ancho menos su propia mitad por cada lado).
/// </summary>
public sealed class MarcadoresLineaTiempo : FrameworkElement
{
    public static readonly DependencyProperty SegmentosProperty = DependencyProperty.Register(
        nameof(Segmentos), typeof(IEnumerable<SegmentoLineaTiempo>), typeof(MarcadoresLineaTiempo),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MarcadoresLineaTiempo)d).ColorearBolita()));

    public static readonly DependencyProperty DuracionProperty = DependencyProperty.Register(
        nameof(Duracion), typeof(double), typeof(MarcadoresLineaTiempo),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Posición actual en segundos (el Value del deslizador).</summary>
    public static readonly DependencyProperty ValorProperty = DependencyProperty.Register(
        nameof(Valor), typeof(double), typeof(MarcadoresLineaTiempo),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MarcadoresLineaTiempo)d).ColorearBolita()));

    /// <summary>Mitad del ancho de la bolita del deslizador (20 px en el reproductor): margen a cada lado donde no llega el centro.</summary>
    public static readonly DependencyProperty MargenLateralProperty = DependencyProperty.Register(
        nameof(MargenLateral), typeof(double), typeof(MarcadoresLineaTiempo),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Grosor de la línea: 3 px en reposo y 5 px con el cursor encima (lo anima la plantilla del deslizador, igual que la pista de fondo).</summary>
    public static readonly DependencyProperty GrosorProperty = DependencyProperty.Register(
        nameof(Grosor), typeof(double), typeof(MarcadoresLineaTiempo),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Opacidad de la parte de un tramo que aún no se ha reproducido (la reproducida es opaca).</summary>
    private const byte AlfaTramoPendiente = 0xB0;

    private static readonly Color Blanco = Colors.White;

    public IEnumerable<SegmentoLineaTiempo>? Segmentos
    {
        get => (IEnumerable<SegmentoLineaTiempo>?)GetValue(SegmentosProperty);
        set => SetValue(SegmentosProperty, value);
    }

    /// <summary>Duración del episodio en segundos (el Maximum del deslizador).</summary>
    public double Duracion
    {
        get => (double)GetValue(DuracionProperty);
        set => SetValue(DuracionProperty, value);
    }

    public double Valor
    {
        get => (double)GetValue(ValorProperty);
        set => SetValue(ValorProperty, value);
    }

    public double MargenLateral
    {
        get => (double)GetValue(MargenLateralProperty);
        set => SetValue(MargenLateralProperty, value);
    }

    public double Grosor
    {
        get => (double)GetValue(GrosorProperty);
        set => SetValue(GrosorProperty, value);
    }

    public MarcadoresLineaTiempo()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    // === Cálculos puros (compartidos con las pruebas) ===

    /// <summary>Posición horizontal (px) de un instante.</summary>
    internal static double PosicionX(double segundos, double duracion, double anchoTotal, double margen)
    {
        if (duracion <= 0) return margen;
        double util = Math.Max(0, anchoTotal - 2 * margen);
        return margen + Math.Clamp(segundos / duracion, 0, 1) * util;
    }

    /// <summary>El tramo en el que está la reproducción (inicio incluido, fin excluido), o null si está fuera de todos.</summary>
    internal static SegmentoLineaTiempo? TramoEn(IEnumerable<SegmentoLineaTiempo>? segmentos, double segundos) =>
        segmentos?.FirstOrDefault(s => segundos >= s.Inicio && segundos < s.Fin);

    /// <summary>Colores de la paleta de la app (Brush.Accent = opening, Brush.Warning = ending, Brush.TextTertiary = resumen).</summary>
    private Color ColorDe(TipoSegmentoLineaTiempo tipo)
    {
        string clave = tipo switch
        {
            TipoSegmentoLineaTiempo.Opening => "Brush.Accent",
            TipoSegmentoLineaTiempo.Ending => "Brush.Warning",
            _ => "Brush.TextTertiary"
        };
        if (TryFindResource(clave) is SolidColorBrush pincel) return pincel.Color;

        return tipo switch
        {
            TipoSegmentoLineaTiempo.Opening => Color.FromRgb(0x60, 0xA5, 0xFA),
            TipoSegmentoLineaTiempo.Ending => Color.FromRgb(0xFB, 0xBF, 0x24),
            _ => Color.FromRgb(0x94, 0xA3, 0xB8)
        };
    }

    private static SolidColorBrush Pincel(Color color)
    {
        var p = new SolidColorBrush(color);
        p.Freeze();
        return p;
    }

    // === Bolita del deslizador ===

    /// <summary>
    /// La bolita es el Thumb del deslizador (otra plantilla): su relleno sale del Foreground del deslizador, que aquí se pone al color
    /// del tramo actual (o blanco fuera de los tramos).
    /// </summary>
    private void ColorearBolita()
    {
        if (TemplatedParent is not Control deslizador) return;

        var tramo = TramoEn(Segmentos, Valor);
        deslizador.SetCurrentValue(Control.ForegroundProperty, Pincel(tramo == null ? Blanco : ColorDe(tramo.Tipo)));
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => ColorearBolita();
    }

    // === Dibujo ===

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double duracion = Duracion;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double grosor = Math.Max(1, Grosor);
        double y = (ActualHeight - grosor) / 2;
        double radio = grosor / 2;
        double xActual = PosicionX(Valor, duracion, ActualWidth, MargenLateral);
        var segmentos = duracion > 0 ? Segmentos?.ToList() : null;

        // 1) Tramos por reproducir: el color con transparencia, sobre la pista de fondo translúcida
        if (segmentos != null)
        {
            foreach (var s in segmentos)
            {
                double x1 = PosicionX(s.Inicio, duracion, ActualWidth, MargenLateral);
                double x2 = PosicionX(s.Fin, duracion, ActualWidth, MargenLateral);
                if (x2 - x1 < 1) continue;

                var c = ColorDe(s.Tipo);
                drawingContext.DrawRoundedRectangle(Pincel(Color.FromArgb(AlfaTramoPendiente, c.R, c.G, c.B)), null, new Rect(x1, y, x2 - x1, grosor), radio, radio);
            }
        }

        // 2) Lo reproducido: blanco desde el borde hasta el centro de la bolita (como la línea de siempre)…
        if (xActual > 0)
            drawingContext.DrawRoundedRectangle(Pincel(Blanco), null, new Rect(0, y, xActual, grosor), radio, radio);

        // 3) …y, dentro de cada tramo ya alcanzado, del color del tramo (opaco)
        if (segmentos != null)
        {
            foreach (var s in segmentos)
            {
                double x1 = PosicionX(s.Inicio, duracion, ActualWidth, MargenLateral);
                double x2 = Math.Min(PosicionX(s.Fin, duracion, ActualWidth, MargenLateral), xActual);
                if (x2 - x1 < 0.5) continue;

                drawingContext.DrawRoundedRectangle(Pincel(ColorDe(s.Tipo)), null, new Rect(x1, y, x2 - x1, grosor), radio, radio);
            }
        }
    }
}
