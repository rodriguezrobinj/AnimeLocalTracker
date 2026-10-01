using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta subtítulos contra el <see cref="Player"/> de Flyleaf. El ViewModel sigue dueño de
/// SubtitulosHabilitados/SubtitulosIcon (propiedades observables); este colaborador solo toca
/// el Player y decide qué corresponde por defecto.
/// </summary>
public interface ISubtitleCoordinator
{
    /// <summary>True si, según si el usuario permite subtítulos por defecto y si el archivo
    /// realmente trae pistas, deben quedar habilitados.</summary>
    bool DebenHabilitarsePorDefecto(Player? player, bool permitirSubtitulosConfig);

    void Habilitar(Player? player);
    void Deshabilitar(Player? player);

    /// <summary>Habilita los subtítulos de Flyleaf y reabre el Player con el stream elegido. Solo para pistas que la app no
    /// puede leer por su cuenta (de imagen, o de texto cuya extracción falló): las de texto no deben pasar por Flyleaf.</summary>
    void SeleccionarPista(Player? player, object stream);

    /// <summary>
    /// Pista a mostrar al abrir un episodio (Flyleaf arranca con sus subtítulos apagados y no elige ninguna).
    /// Null si Flyleaf ya tiene una abierta o el archivo no trae pistas.
    /// </summary>
    object? PistaPorDefecto(Player? player, string idiomaPreferido);
}
