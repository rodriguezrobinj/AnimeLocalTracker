using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class GaleriaView : UserControl
{
    public GaleriaView()
    {
        InitializeComponent();
        IsVisibleChanged += GaleriaView_IsVisibleChanged;
    }

    /// <summary>
    /// La vista se conserva al cambiar de pestaña (ver AnfitrionVistas), así que al salir no llega Unloaded: lo que no debe
    /// quedarse abierto o a medias (el panel "Qué veo hoy", el menú del usuario) se cierra al dejar de verse.
    /// </summary>
    private void GaleriaView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false) return;

        if (DataContext is GaleriaViewModel vm)
        {
            vm.QueVeoHoy.CerrarQueVerHoyCommand.Execute(null);
            vm.CerrarMenuUsuarioCommand.Execute(null);
        }
    }

    /// <summary>
    /// Deja construida, sin mostrarla, la sección de minijuegos: primero el menú y, en llamadas sucesivas, un juego cada
    /// vez. False cuando ya no queda nada por construir.
    /// </summary>
    public bool PrecalentarMinijuegos()
    {
        if (DataContext is not GaleriaViewModel vm || !vm.HayMinijuegos) return false;

        if (vm.ContenidoMinijuegos == null)
        {
            vm.PrepararVistaMinijuegos();
            SeccionMinijuegos.UpdateLayout();
            return true;
        }

        var menu = FindVisualChild<MinijuegosView>(SeccionMinijuegos);
        if (menu == null || !menu.PrecalentarSiguienteJuego()) return false;
        SeccionMinijuegos.UpdateLayout();
        return true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
    }
}
