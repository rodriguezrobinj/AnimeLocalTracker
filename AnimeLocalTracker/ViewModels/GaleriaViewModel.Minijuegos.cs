using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Sección "Minijuegos" de la Galería: un selector Biblioteca | Minijuegos cambia lo que muestra la página. Los juegos son
/// singleton, así que una partida en curso sobrevive a cambiar de sección o de pestaña.
/// </summary>
public partial class GaleriaViewModel
{
    private readonly MinijuegosViewModel? _minijuegos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrandoBiblioteca))]
    [NotifyPropertyChangedFor(nameof(ContenidoMinijuegos))]
    private bool _mostrandoMinijuegos;

    /// <summary>True si hay minijuegos disponibles (sin el ViewModel no se ofrece el selector).</summary>
    public bool HayMinijuegos => _minijuegos != null;

    public bool MostrandoBiblioteca => !MostrandoMinijuegos;

    private bool _vistaMinijuegosCreada;

    /// <summary>
    /// El ViewModel de los minijuegos desde la primera vez que hace falta su vista (al abrir la sección o al prepararla por
    /// adelantado); antes es null y la vista no existe. Después se conserva: volver a la sección no la reconstruye. El audio
    /// de "Adivina el OP/ED" lo corta <see cref="MostrarBiblioteca"/> (y la propia vista al dejar de verse).
    /// </summary>
    public MinijuegosViewModel? ContenidoMinijuegos => _vistaMinijuegosCreada ? _minijuegos : null;

    /// <summary>Crea ya la vista de los minijuegos (oculta) para que abrir la sección por primera vez no tenga que construirla.</summary>
    public void PrepararVistaMinijuegos()
    {
        if (_minijuegos == null || _vistaMinijuegosCreada) return;

        _vistaMinijuegosCreada = true;
        OnPropertyChanged(nameof(ContenidoMinijuegos));
    }

    [RelayCommand]
    private void MostrarBiblioteca()
    {
        if (!MostrandoMinijuegos) return;

        _minijuegos?.DetenerJuegoActivo();
        MedidorRendimiento.MedirCambio("Galería: Minijuegos → Biblioteca");
        MostrandoMinijuegos = false;
    }

    [RelayCommand]
    private async Task MostrarMinijuegosAsync()
    {
        if (_minijuegos == null || MostrandoMinijuegos) return;

        MedidorRendimiento.MedirCambio("Galería: Biblioteca → Minijuegos");
        _vistaMinijuegosCreada = true;
        MostrandoMinijuegos = true;
        await PrepararMinijuegosAsync();
    }

    /// <summary>Al volver a la pestaña Galería con la sección de minijuegos abierta: recuenta los animes y refresca los récords.</summary>
    public async Task AlEntrarAsync()
    {
        if (MostrandoMinijuegos) await PrepararMinijuegosAsync();
    }

    private async Task PrepararMinijuegosAsync()
    {
        try
        {
            // No interrumpe una partida en curso (ver MinijuegosViewModel.PrepararAsync).
            if (_minijuegos != null) await _minijuegos.PrepararAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error preparando los minijuegos", ex);
        }
    }
}
