using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AnimeLocalTracker.Views;

public partial class DescargasView : UserControl
{
    public DescargasView()
    {
        InitializeComponent();
    }

    private void MenuOpcionesBtn_Click(object sender, RoutedEventArgs e)
    {
        MenuOpcionesPopup.IsOpen = !MenuOpcionesPopup.IsOpen;
    }

    private void MenuOpcionesBtn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MenuOpcionesPopup.IsOpen)
        {
            MenuOpcionesPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void CerrarMenuOpciones_Click(object sender, RoutedEventArgs e)
    {
        MenuOpcionesPopup.IsOpen = false;
    }
}
