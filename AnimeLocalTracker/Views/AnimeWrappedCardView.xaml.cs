using System.Windows.Controls;
using System.Windows.Media;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Views;

/// <summary>
/// Solo se instancia para renderizarse fuera de pantalla (ver EstadisticasViewModel.
/// RenderizarTarjetaWrapped) — nunca se agrega a la ventana ni se navega a ella.
/// </summary>
public partial class AnimeWrappedCardView : UserControl
{
    public AnimeWrappedCardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is WrappedCardData { Avatar: not null } datos)
            {
                AvatarEllipse.Fill = new ImageBrush(datos.Avatar) { Stretch = Stretch.UniformToFill };
            }
        };
    }
}
