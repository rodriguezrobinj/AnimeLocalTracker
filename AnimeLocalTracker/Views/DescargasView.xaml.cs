using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AnimeLocalTracker.Views;

public partial class DescargasView : UserControl
{
    // Con StaysOpen=False el popup se cierra solo al pulsar fuera, botón incluido, ANTES de que llegue el Click:
    // sin este guardián el mismo clic que lo cierra lo volvería a abrir.
    private DateTime _cerradoEn = DateTime.MinValue;

    public DescargasView()
    {
        InitializeComponent();
        MenuOpcionesPopup.Closed += (_, _) => _cerradoEn = DateTime.UtcNow;
    }

    private void MenuOpcionesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MenuOpcionesPopup.IsOpen || (DateTime.UtcNow - _cerradoEn).TotalMilliseconds < 300) return;
        MenuOpcionesPopup.IsOpen = true;
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
