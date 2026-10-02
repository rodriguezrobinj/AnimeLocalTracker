using System.Windows.Controls;

namespace AnimeLocalTracker.Views;

/// <summary>Menú de minijuegos: el foco y los atajos los gestiona cada juego (ver <see cref="MinijuegoFocoHelper"/>).</summary>
public partial class MinijuegosView : UserControl
{
    public MinijuegosView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Construye por adelantado la vista del siguiente juego que aún no la tenga (sin mostrarla). False cuando ya están
    /// todas: se llama de una en una para repartir el trabajo.
    /// </summary>
    public bool PrecalentarSiguienteJuego()
    {
        if (DataContext is not ViewModels.MinijuegosViewModel vm) return false;

        foreach (var juego in vm.Juegos)
        {
            if (AnfitrionJuegos.Precalentar(juego)) return true;
        }
        return false;
    }
}
