using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Una opción del desplegable de filtro de episodios de la ficha: la clave fija con la que se filtra
/// (<see cref="Core.EpisodiosOrganizador"/>) y el texto traducido que se ve.
/// </summary>
public sealed class OpcionFiltroEpisodios : ObservableObject
{
    private readonly string _claveTexto;

    public OpcionFiltroEpisodios(string clave, string claveTexto)
    {
        Clave = clave;
        _claveTexto = claveTexto;
    }

    public string Clave { get; }

    public string Texto => LocalizationService.T(_claveTexto);

    /// <summary>Es el nombre que leen los lectores de pantalla para cada opción (sin esto leían el nombre de la clase).</summary>
    public override string ToString() => Texto;

    /// <summary>Tras cambiar el idioma de la app con la ficha abierta.</summary>
    internal void RefrescarTexto() => OnPropertyChanged(nameof(Texto));
}
