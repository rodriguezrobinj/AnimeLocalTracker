using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Dibuja un subtítulo con el <see cref="EstiloSubtitulos"/> configurado: tipografía, tamaño, negrita/cursiva/
/// subrayado, color, contorno REAL alrededor de cada letra (no un texto duplicado y borroso) y, opcionalmente,
/// una caja de fondo. Es la vista previa de Configuración: en el reproductor los subtítulos los dibuja libmpv con
/// estas mismas medidas (<c>MotorMpv.OpcionesDeEstilo</c>).
///
/// Es un elemento propio (no un TextBlock) porque WPF no tiene contorno de texto nativo: se convierte el
/// texto a geometría y se dibuja primero el trazo y encima el relleno.
/// </summary>
public sealed class SubtitleTextView : FrameworkElement
{
    private const double RellenoCajaHorizontal = 14;
    private const double RellenoCajaVertical = 6;

    public static readonly DependencyProperty TextoProperty = DependencyProperty.Register(
        nameof(Texto), typeof(string), typeof(SubtitleTextView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EstiloProperty = DependencyProperty.Register(
        nameof(Estilo), typeof(EstiloSubtitulos), typeof(SubtitleTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private Geometry? _geometria;
    private EstiloSubtitulos _estiloEnUso = new();

    public SubtitleTextView()
    {
        // Sombra suave (la que ya tenían los subtítulos) para que despeguen del fondo aunque no haya contorno.
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
        IsHitTestVisible = false;
    }

    public string Texto
    {
        get => (string)GetValue(TextoProperty);
        set => SetValue(TextoProperty, value);
    }

    public EstiloSubtitulos? Estilo
    {
        get => (EstiloSubtitulos?)GetValue(EstiloProperty);
        set => SetValue(EstiloProperty, value);
    }

    private bool TieneCaja => _estiloEnUso.OpacidadFondo > 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        _geometria = null;

        string texto = Texto;
        if (string.IsNullOrEmpty(texto)) return default;

        _estiloEnUso = (Estilo ?? new EstiloSubtitulos()).Normalizar();
        var e = _estiloEnUso;

        // Espacio que ocupa todo lo que rodea al texto: contorno + relleno de la caja
        double contorno = e.GrosorContorno;
        double margenX = contorno + (TieneCaja ? RellenoCajaHorizontal : 0);
        double margenY = contorno + (TieneCaja ? RellenoCajaVertical : 0);

        double anchoMaxTexto = double.IsInfinity(availableSize.Width) ? 0 : Math.Max(1, availableSize.Width - 2 * margenX);

        var ft = Construir(texto, e, anchoMaxTexto);

        // Con el texto ya partido en líneas, se ajusta el ancho al de la línea más larga: así el centrado
        // queda bien y la caja no se estira al ancho máximo cuando el subtítulo es corto.
        ft.MaxTextWidth = Math.Max(1, Math.Ceiling(ft.Width) + 1);

        _geometria = ft.BuildGeometry(new Point(margenX, margenY));
        _geometria?.Freeze();

        return new Size(ft.Width + 2 * margenX, ft.Height + 2 * margenY);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometria == null) return;
        var e = _estiloEnUso;

        if (TieneCaja)
        {
            var fondo = new SolidColorBrush(Color.FromArgb((byte)Math.Round(e.OpacidadFondo * 255 / 100.0), 0, 0, 0));
            drawingContext.DrawRoundedRectangle(fondo, null, new Rect(RenderSize), 8, 8);
        }

        if (e.GrosorContorno > 0)
        {
            // El trazo se centra en el borde de la letra: al doble de grosor y con el relleno encima,
            // queda visible hacia afuera exactamente el grosor configurado.
            var lapizContorno = new Pen(Pincel(e.ColorContorno), e.GrosorContorno * 2)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawingContext.DrawGeometry(null, lapizContorno, _geometria);
        }

        drawingContext.DrawGeometry(Pincel(e.ColorTexto), null, _geometria);
    }

    private FormattedText Construir(string texto, EstiloSubtitulos e, double anchoMaximo)
    {
        var tipografia = new Typeface(
            new FontFamily(e.Fuente),
            e.Cursiva ? FontStyles.Italic : FontStyles.Normal,
            e.Negrita ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var ft = new FormattedText(
            texto,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            tipografia,
            e.Tamano,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center
        };

        if (anchoMaximo > 0) ft.MaxTextWidth = anchoMaximo;
        if (e.Subrayado) ft.SetTextDecorations(TextDecorations.Underline);
        return ft;
    }

    private static Brush Pincel(string hex)
    {
        try
        {
            var pincel = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            pincel.Freeze();
            return pincel;
        }
        catch (FormatException)
        {
            return Brushes.White;
        }
    }
}
