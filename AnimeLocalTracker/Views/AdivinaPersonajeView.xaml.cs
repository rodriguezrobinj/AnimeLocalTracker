using System.Windows.Controls;

namespace AnimeLocalTracker.Views;

public partial class AdivinaPersonajeView : UserControl
{
    private readonly MinijuegoFocoHelper _foco;

    public AdivinaPersonajeView()
    {
        InitializeComponent();
        _foco = new MinijuegoFocoHelper(this, ScrollPartida);
    }
}
