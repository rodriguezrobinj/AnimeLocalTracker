using System;

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

    public bool AplicarVolumen(IMotorVideo? motor, int volumen, bool estabaMuteado)
    {
        if (motor == null) return false;
        try
        {
            motor.Volumen = volumen;
            if (volumen > 0 && estabaMuteado)
            {
                motor.Silencio = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PlaybackVolumeCoordinator", $"Error asignando volumen: {ex.Message}");
        }
        return false;
    }

    public void Desmutear(IMotorVideo? motor, int volumenActual)
    {
        if (motor == null) return;
        try
        {
            motor.Silencio = false;
            motor.Volumen = volumenActual;
        }
        catch { }
    }

    public void Mutear(IMotorVideo? motor)
    {
        if (motor == null) return;
        try { motor.Silencio = true; }
        catch { }
    }
}
