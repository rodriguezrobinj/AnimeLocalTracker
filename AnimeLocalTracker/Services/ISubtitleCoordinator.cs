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

    /// <summary>Habilita subtítulos y reabre el Player con el stream elegido.</summary>
    void SeleccionarPista(Player? player, object stream);

    /// <summary>
    /// Pista a activar cuando el archivo trae subtítulos pero ninguno viene marcado por defecto (Flyleaf entonces no muestra
    /// nada). Null si ya hay una activa o no hay pistas de texto.
    /// </summary>
    object? PistaPorDefecto(Player? player, string idiomaPreferido);
}
