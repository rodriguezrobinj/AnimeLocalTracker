using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Un color de la paleta de estilos de subtítulos (muestra + nombre). El nombre se traduce al idioma
/// activo y se puede refrescar en caliente con <see cref="Refrescar"/> cuando cambia el idioma
/// (los ComboBox no se re-traducen solos; ver LOC-08).
/// </summary>
public sealed class ColorEstiloOpcion : ObservableObject
{
    public ColorEstiloOpcion(string clave, string hex)
    {
        Clave = clave;
        Hex = hex;
    }

    public string Clave { get; }

    /// <summary>Valor #RRGGBB: es lo que se guarda en la configuración.</summary>
    public string Hex { get; }

    public string Nombre => LocalizationService.T(Clave);

    public void Refrescar() => OnPropertyChanged(nameof(Nombre));
}
