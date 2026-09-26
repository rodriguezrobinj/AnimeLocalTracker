using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Pestaña "Minijuegos": un menú con los juegos disponibles. Cada juego tiene su propio ViewModel (singleton, así que una
/// partida en curso sobrevive al cambiar de pestaña o volver al menú); aquí solo se decide cuál está abierto.
/// </summary>
public sealed partial class MinijuegosViewModel : ObservableObject
{
    public AdivinaAnimeViewModel AdivinaAnime { get; }
    public AdivinaOpEdViewModel AdivinaOpEd { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnMenu))]
    private MinijuegoViewModelBase? _juegoActivo;

    public bool EnMenu => JuegoActivo == null;

    public MinijuegosViewModel(AdivinaAnimeViewModel adivinaAnime, AdivinaOpEdViewModel adivinaOpEd)
    {
        AdivinaAnime = adivinaAnime;
        AdivinaOpEd = adivinaOpEd;
    }

    /// <summary>
    /// Al entrar a la pestaña: si hay un juego abierto, lo prepara (recuenta animes y lee sus récords); en el menú solo
    /// refresca los récords que se ven en las tarjetas.
    /// </summary>
    public async Task PrepararAsync()
    {
        if (JuegoActivo != null)
        {
            await JuegoActivo.PrepararAsync();
            return;
        }

        await Task.WhenAll(AdivinaAnime.RefrescarRecordsAsync(), AdivinaOpEd.RefrescarRecordsAsync());
    }

    [RelayCommand]
    private Task AbrirAdivinaAnimeAsync() => AbrirAsync(AdivinaAnime);

    [RelayCommand]
    private Task AbrirAdivinaOpEdAsync() => AbrirAsync(AdivinaOpEd);

    [RelayCommand]
    private void VolverAlMenu()
    {
        JuegoActivo?.Detener();
        JuegoActivo = null;
    }

    private async Task AbrirAsync(MinijuegoViewModelBase juego)
    {
        JuegoActivo = juego;
        await juego.PrepararAsync();
    }
}
