using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

public class SubtitleCoordinator : ISubtitleCoordinator
{
    public bool DebenHabilitarsePorDefecto(Player? player, bool permitirSubtitulosConfig)
    {
        if (!permitirSubtitulosConfig) return false;
        if (player?.Subtitles?.Streams == null || player.Subtitles.Streams.Count == 0) return false;
        return true;
    }

    public void Deshabilitar(Player? player)
    {
        if (player?.Config?.Subtitles != null)
        {
            player.Config.Subtitles.Enabled = false;
        }
    }

    public void Habilitar(Player? player)
    {
        if (player?.Config?.Subtitles != null)
        {
            player.Config.Subtitles.Enabled = true;
        }
    }

    public void SeleccionarPista(Player? player, object stream)
    {
        if (player == null || stream == null) return;

        Habilitar(player);
        player.OpenAsync((dynamic)stream);
    }
}
