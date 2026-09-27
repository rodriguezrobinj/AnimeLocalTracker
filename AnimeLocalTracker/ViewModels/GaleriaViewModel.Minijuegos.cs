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

    /// <summary>
    /// El ViewModel de los minijuegos solo mientras se ve esa sección; con la biblioteca visible es null y la vista de los juegos
    /// se descarta (así un clip de audio de "Adivina el OP/ED" se corta al cambiar de sección).
    /// </summary>
    public MinijuegosViewModel? ContenidoMinijuegos => MostrandoMinijuegos ? _minijuegos : null;

    [RelayCommand]
    private void MostrarBiblioteca()
    {
        if (!MostrandoMinijuegos) return;

        _minijuegos?.DetenerJuegoActivo();
        MostrandoMinijuegos = false;
    }

    [RelayCommand]
    private async Task MostrarMinijuegosAsync()
    {
        if (_minijuegos == null || MostrandoMinijuegos) return;

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
