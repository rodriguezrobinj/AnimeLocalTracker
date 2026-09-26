using System.Windows.Controls;

namespace AnimeLocalTracker.Views;

public partial class AdivinaAnimeView : UserControl
{
    private readonly MinijuegoFocoHelper _foco;

    public AdivinaAnimeView()
    {
        InitializeComponent();
        _foco = new MinijuegoFocoHelper(this, ScrollPartida);
    }
}
