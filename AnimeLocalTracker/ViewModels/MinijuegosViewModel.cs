using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Pestaña "Minijuegos": un menú con los juegos disponibles. Cada juego tiene su propio ViewModel (singleton, así que una
/// partida en curso sobrevive al cambiar de pestaña o volver al menú); aquí solo se decide cuál está abierto.
/// <para>
/// Añadir un juego: su ViewModel (hereda de <see cref="MinijuegoViewModelBase"/>), su vista con una plantilla en
/// <c>MinijuegosView.xaml</c>, registrarlo en el contenedor y sumarlo a <see cref="Juegos"/>. El menú, los récords, la
/// biblioteca compartida y el precalentado salen de esa lista.
/// </para>
/// </summary>
public sealed partial class MinijuegosViewModel : ObservableObject
{
    public AdivinaAnimeViewModel AdivinaAnime { get; }
    public AdivinaOpEdViewModel AdivinaOpEd { get; }
    public AdivinaPersonajeViewModel AdivinaPersonaje { get; }

    /// <summary>Todos los juegos, en el orden en que salen en el menú.</summary>
    public IReadOnlyList<MinijuegoViewModelBase> Juegos { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnMenu))]
    private MinijuegoViewModelBase? _juegoActivo;

    public bool EnMenu => JuegoActivo == null;

    private readonly ISettingsService? _settings;

    /// <summary>Jugar solo con animes que has visto o estás viendo. Opción apagada por defecto; se recuerda entre sesiones.</summary>
    [ObservableProperty] private bool _soloVistos;

    public MinijuegosViewModel(AdivinaAnimeViewModel adivinaAnime, AdivinaOpEdViewModel adivinaOpEd, AdivinaPersonajeViewModel adivinaPersonaje,
        ISettingsService? settings = null)
    {
        _settings = settings;
        AdivinaAnime = adivinaAnime;
        AdivinaOpEd = adivinaOpEd;
        AdivinaPersonaje = adivinaPersonaje;
        Juegos = [adivinaAnime, adivinaOpEd, adivinaPersonaje];

        _soloVistos = settings?.ObtenerConfiguracion()?.MinijuegosSoloVistos ?? false;
        AplicarSoloVistos();
    }

    private void AplicarSoloVistos()
    {
        foreach (var juego in Juegos) juego.SoloVistos = SoloVistos;
    }

    partial void OnSoloVistosChanged(bool value)
    {
        AplicarSoloVistos();
        _ = GuardarSoloVistosAsync(value);
    }

    private async Task GuardarSoloVistosAsync(bool valor)
    {
        try
        {
            // Un juego abierto (sin partida en marcha) recuenta ya con qué animes puede jugar.
            if (JuegoActivo != null) await JuegoActivo.PrepararAsync();

            if (_settings != null) await _settings.ActualizarAsync(c => c.MinijuegosSoloVistos = valor);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MinijuegosViewModel", $"No se pudo guardar la opción de minijuegos: {ex.Message}");
        }
    }

    /// <summary>De dónde sacan los juegos la biblioteca (la que la Galería ya tiene cargada) en vez de leerla cada uno de la base de datos.</summary>
    public void UsarBiblioteca(Func<IReadOnlyList<AnimeItem>> fuente)
    {
        foreach (var juego in Juegos) juego.FuenteBiblioteca = fuente;
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

        await Task.WhenAll(Juegos.Select(j => j.RefrescarRecordsAsync()));
    }

    /// <summary>Abre el juego elegido en el menú.</summary>
    [RelayCommand]
    private async Task AbrirJuegoAsync(MinijuegoViewModelBase? juego)
    {
        if (juego == null) return;

        MedidorRendimiento.MedirCambio($"Minijuegos: menú → {juego.GetType().Name.Replace("ViewModel", string.Empty)}");
        JuegoActivo = juego;
        await juego.PrepararAsync();
    }

    [RelayCommand]
    private Task AbrirAdivinaAnimeAsync() => AbrirJuegoAsync(AdivinaAnime);

    [RelayCommand]
    private Task AbrirAdivinaOpEdAsync() => AbrirJuegoAsync(AdivinaOpEd);

    [RelayCommand]
    private Task AbrirAdivinaPersonajeAsync() => AbrirJuegoAsync(AdivinaPersonaje);

    /// <summary>Corta lo que esté sonando o preparándose en el juego abierto (al salir de la sección), sin cerrarlo.</summary>
    public void DetenerJuegoActivo() => JuegoActivo?.Detener();

    [RelayCommand]
    private void VolverAlMenu()
    {
        DetenerJuegoActivo();
        MedidorRendimiento.MedirCambio("Minijuegos: juego → menú");
        JuegoActivo = null;
    }
}
