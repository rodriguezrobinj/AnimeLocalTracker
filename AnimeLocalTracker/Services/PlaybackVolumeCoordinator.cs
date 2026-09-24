using System;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

public class PlaybackVolumeCoordinator : IPlaybackVolumeCoordinator
{
    public string CalcularIcono(int volumen, bool isMuted)
    {
        if (isMuted || volumen == 0) return "VolumeOff";
        if (volumen < 35) return "VolumeLow";
        if (volumen < 70) return "VolumeMedium";
        return "VolumeHigh";
    }

    public bool AplicarVolumen(Player? player, int volumen, bool estabaMuteado)
    {
        if (player?.Audio == null) return false;

        try
        {
            player.Audio.Volume = volumen;
            if (volumen > 0 && estabaMuteado)
            {
                player.Audio.Mute = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PlaybackVolumeCoordinator", $"Error asignando volumen: {ex.Message}");
        }

        return false;
    }

    public void Desmutear(Player? player, int volumenActual)
    {
        if (player?.Audio == null) return;
        try
        {
            player.Audio.Mute = false;
            player.Audio.Volume = volumenActual;
        }
        catch { }
    }

    public void Mutear(Player? player)
    {
        if (player?.Audio == null) return;
        try { player.Audio.Mute = true; }
        catch { }
    }
}
