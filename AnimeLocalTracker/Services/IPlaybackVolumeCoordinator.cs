using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta volumen/mute contra el <see cref="Player"/> de Flyleaf. El ViewModel sigue dueño de
/// Volumen/IsMuted/VolumenIcon (propiedades observables); este colaborador solo toca el Player
/// y calcula el ícono correspondiente.
/// </summary>
public interface IPlaybackVolumeCoordinator
{
    /// <summary>Ícono correspondiente a un volumen/mute dados (función pura).</summary>
    string CalcularIcono(int volumen, bool isMuted);

    /// <summary>
    /// Aplica el volumen al Player; si <paramref name="estabaMuteado"/> y el volumen es &gt; 0,
    /// también desmutea. Devuelve true si desmuteó (el llamador debe actualizar su IsMuted).
    /// </summary>
    bool AplicarVolumen(Player? player, int volumen, bool estabaMuteado);

    /// <summary>Desmutea y re-aplica el volumen actual (fiel al ToggleMute original).</summary>
    void Desmutear(Player? player, int volumenActual);

    void Mutear(Player? player);
}
