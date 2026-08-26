using Avalonia.Controls;

namespace AnimeLocalTracker.Avalonia.Views;

public partial class ReproductorView : UserControl
{
    public ReproductorView()
    {
        InitializeComponent();
    }

    private void VideoViewControl_OnDoubleTapped(object? sender, global::Avalonia.Input.TappedEventArgs e)
    {
        e.Handled = true;
    }
}
