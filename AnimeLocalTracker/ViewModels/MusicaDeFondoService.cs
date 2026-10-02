using System;
using System.ComponentModel;
using AnimeLocalTracker.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// La música de una ficha que sigue sonando después de salir de ella (solo si el usuario activó "Seguir sonando fuera de la
/// ficha"). La ficha, al cerrarse, entrega aquí su música en vez de cortarla; la ventana principal muestra una barra pequeña
/// para controlarla, y al volver a la ficha de ese anime la música se le devuelve tal como iba.
/// </summary>
public interface IMusicaDeFondoService : INotifyPropertyChanged
{
    /// <summary>La música que sigue sonando sin su ficha en pantalla, o null.</summary>
    MusicaFichaViewModel? Activa { get; }

    bool HayMusica { get; }

    /// <summary>
    /// La ficha se cierra. Si la opción está activa y hay un tema sonando, la música se queda aquí y devuelve true (la ficha
    /// no debe liberarla). Si no, devuelve false y la ficha la corta como siempre.
    /// </summary>
    bool Conservar(MusicaFichaViewModel musica);

    /// <summary>Se abre la ficha de ese anime: si su música seguía sonando, se le devuelve (y deja de estar "de fondo").</summary>
    MusicaFichaViewModel? Recuperar(int aniListId);

    /// <summary>Otra ficha empieza a reproducir música: la que sonaba de fondo se corta (nunca suenan dos a la vez).</summary>
    void AlEmpezarASonar(MusicaFichaViewModel musica);

    /// <summary>Corta la música de fondo y libera su reproductor. Seguro de llamar siempre (abrir un video, un minijuego…).</summary>
    void Detener();
}

public sealed partial class MusicaDeFondoService : ObservableObject, IMusicaDeFondoService, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayMusica))]
    [NotifyPropertyChangedFor(nameof(TituloAnime))]
    private MusicaFichaViewModel? _activa;

    public bool HayMusica => Activa != null;

    public string TituloAnime => Activa?.Anime?.Titulo ?? string.Empty;

    /// <summary>Se pulsó el título en la barra: al llegar a la ficha, su ventana de música se abre sola.</summary>
    private bool _abrirVentanaAlVolver;

    public bool Conservar(MusicaFichaViewModel musica)
    {
        if (!musica.SeguirFueraDeLaFicha || !musica.MusicaSonando || musica.TemaActual == null) return false;

        if (Activa != null && !ReferenceEquals(Activa, musica)) Detener();

        musica.PasarAFondo();
        musica.PropertyChanged += AlCambiarLaMusica;
        Activa = musica;
        return true;
    }

    public MusicaFichaViewModel? Recuperar(int aniListId)
    {
        var musica = Activa;
        if (musica?.Anime?.AniListId != aniListId) return null;

        bool abrirVentana = _abrirVentanaAlVolver;
        Soltar();
        musica.VolverALaFicha(abrirVentana);
        return musica;
    }

    public void AlEmpezarASonar(MusicaFichaViewModel musica)
    {
        if (Activa != null && !ReferenceEquals(Activa, musica)) Detener();
    }

    public void Detener()
    {
        var musica = Activa;
        if (musica == null) return;

        Soltar();
        musica.DetenerMusica();
        musica.Dispose();
    }

    private void Soltar()
    {
        if (Activa != null) Activa.PropertyChanged -= AlCambiarLaMusica;
        _abrirVentanaAlVolver = false;
        Activa = null;
    }

    /// <summary>Si el reproductor se queda sin tema (falló el archivo, se borró…), ya no hay nada que controlar: la barra se va.</summary>
    private void AlCambiarLaMusica(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicaFichaViewModel.TemaActual) && sender is MusicaFichaViewModel { TemaActual: null } musica && ReferenceEquals(musica, Activa))
            Detener();
    }

    [RelayCommand]
    private void DetenerMusica() => Detener();

    /// <summary>Vuelve a la ficha del anime que suena, con su ventana de música abierta.</summary>
    [RelayCommand]
    private void IrALaFicha()
    {
        var anime = Activa?.Anime;
        if (anime == null) return;

        _abrirVentanaAlVolver = true;
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
    }

    public void Dispose() => Detener();
}
