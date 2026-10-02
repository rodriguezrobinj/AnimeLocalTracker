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
    public AdivinaPersonajeViewModel AdivinaPersonaje { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnMenu))]
    private MinijuegoViewModelBase? _juegoActivo;

    public bool EnMenu => JuegoActivo == null;

    public MinijuegosViewModel(AdivinaAnimeViewModel adivinaAnime, AdivinaOpEdViewModel adivinaOpEd, AdivinaPersonajeViewModel adivinaPersonaje)
    {
        AdivinaAnime = adivinaAnime;
        AdivinaOpEd = adivinaOpEd;
        AdivinaPersonaje = adivinaPersonaje;
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

        await Task.WhenAll(AdivinaAnime.RefrescarRecordsAsync(), AdivinaOpEd.RefrescarRecordsAsync(), AdivinaPersonaje.RefrescarRecordsAsync());
    }

    [RelayCommand]
    private Task AbrirAdivinaAnimeAsync() => AbrirAsync(AdivinaAnime);

    [RelayCommand]
    private Task AbrirAdivinaOpEdAsync() => AbrirAsync(AdivinaOpEd);

    [RelayCommand]
    private Task AbrirAdivinaPersonajeAsync() => AbrirAsync(AdivinaPersonaje);

    /// <summary>Corta lo que esté sonando o preparándose en el juego abierto (al salir de la sección), sin cerrarlo.</summary>
    public void DetenerJuegoActivo() => JuegoActivo?.Detener();

    [RelayCommand]
    private void VolverAlMenu()
    {
        DetenerJuegoActivo();
        Services.MedidorRendimiento.MedirCambio("Minijuegos: juego → menú");
        JuegoActivo = null;
    }

    private async Task AbrirAsync(MinijuegoViewModelBase juego)
    {
        Services.MedidorRendimiento.MedirCambio($"Minijuegos: menú → {juego.GetType().Name.Replace("ViewModel", string.Empty)}");
        JuegoActivo = juego;
        await juego.PrepararAsync();
    }
}
