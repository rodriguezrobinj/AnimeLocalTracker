
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AnimeLocalTracker.Avalonia.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Arrastre de la barra de título custom + doble clic para maximizar/restaurar
    /// (equivalente al WindowChrome de WPF con CaptionHeight=0).
    /// </summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && VisualRoot is Window window)
        {
            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                window.BeginMoveDrag(e);
            }
        }
    }

    private void BtnPantallaCompleta_Click(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window window)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Maximized
                : WindowState.FullScreen;
        }
    }

    private void BtnMinimizar_Click(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void BtnMaximizar_Click(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void BtnCerrar_Click(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window window)
            window.Close();
    }
}
