
namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta subtítulos contra el <see cref="Player"/> de Flyleaf. El ViewModel sigue dueño de
/// SubtitulosHabilitados/SubtitulosIcon (propiedades observables); este colaborador solo toca
/// el Player y decide qué corresponde por defecto.
/// </summary>
public interface ISubtitleCoordinator
{
    bool DebenHabilitarsePorDefecto(IMotorVideo? motor, bool permitirSubtitulosConfig);
    /// <summary>Apaga los subtítulos que esté mostrando el propio motor.</summary>
    void Deshabilitar(IMotorVideo? motor);
    /// <summary>Le pide al motor que muestre esa pista (las de imagen y las externas solo las sabe mostrar él).</summary>
    void SeleccionarPista(IMotorVideo? motor, PistaSubtitulosMotor pista);
    /// <summary>La pista a mostrar al abrir, o null si no hay ninguna o el motor ya tiene una abierta.</summary>
    PistaSubtitulosMotor? PistaPorDefecto(IMotorVideo? motor, string idiomaPreferido);
}
