using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Una fila de la pestaña Configuración → Plugins: el archivo, su huella SHA-256 y si el usuario confía en él.
/// Los textos se traducen aquí (no en el XAML) y se refrescan al cambiar de idioma (LOC-08).
/// </summary>
public sealed class PluginItemViewModel : ObservableObject
{
    public PluginItemViewModel(PluginInfo info)
    {
        Nombre = info.Nombre;
        Tipo = info.Tipo == ".py" ? "Python" : "C#";
        HashCompleto = info.Sha256;
        Estado = info.Estado;
    }

    public string Nombre { get; }

    /// <summary>"C#" (.dll) o "Python" (.py).</summary>
    public string Tipo { get; }

    public string HashCompleto { get; }

    /// <summary>Primeros 12 caracteres de la huella, suficientes para reconocerla de un vistazo.</summary>
    public string HashCorto => HashCompleto.Length > 12 ? HashCompleto[..12] + "…" : HashCompleto;

    public EstadoConfianzaPlugin Estado { get; }

    public bool EsConfiable => Estado == EstadoConfianzaPlugin.Confiable;

    public string TextoEstado => LocalizationService.T(Estado switch
    {
        EstadoConfianzaPlugin.Confiable => "Cfg_PluginEstado_Confiable",
        EstadoConfianzaPlugin.Modificado => "Cfg_PluginEstado_Modificado",
        _ => "Cfg_PluginEstado_SinConfiar"
    });

    /// <summary>Texto del botón: "Confiar" o, si ya es confiable, "Quitar confianza".</summary>
    public string TextoAccion => LocalizationService.T(EsConfiable ? "Cfg_PluginQuitarConfianza" : "Cfg_PluginConfiar");

    public void RefrescarTextos()
    {
        OnPropertyChanged(nameof(TextoEstado));
        OnPropertyChanged(nameof(TextoAccion));
    }
}
