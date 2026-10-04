using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.EnlacesMusica;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Un enlace externo del panel de música, listo para mostrar. La descripción (tooltip) se traduce al idioma actual y se
/// vuelve a traducir al cambiarlo, aunque el panel esté abierto.
/// </summary>
public sealed partial class EnlaceMusicaItem : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    public EnlaceMusica Enlace { get; }

    public string Nombre => Enlace.Nombre;
    public string Url => Enlace.Url;
    public string Descripcion => LocalizationService.T(Enlace.ClaveDescripcion);

    public EnlaceMusicaItem(EnlaceMusica enlace)
    {
        Enlace = enlace;
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(IdiomaCambiadoMensaje message) => Core.HiloUi.Ejecutar(RefrescarTextos);

    private void RefrescarTextos() => OnPropertyChanged(nameof(Descripcion));
}
