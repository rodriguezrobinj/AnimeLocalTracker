using System;
using System.Collections.Generic;
using System.Linq;
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

    public object? PistaPorDefecto(Player? player, string idiomaPreferido)
    {
        var subtitulos = player?.Subtitles;
        if (subtitulos?.Streams == null || subtitulos.Streams.Count == 0 || subtitulos.StreamIndex >= 0) return null;

        var pistas = subtitulos.Streams.ToList();
        int i = ElegirPista(pistas.Select(s => new PistaSubtitulos(s.IsBitmap, IdiomaDosLetras(s), s.Title)).ToList(), idiomaPreferido);
        return i >= 0 ? pistas[i] : null;
    }

    private static string? IdiomaDosLetras(FlyleafLib.MediaFramework.MediaStream.SubtitlesStream s)
    {
        try { return s.Language?.TopCulture?.TwoLetterISOLanguageName; }
        catch { return null; }
    }

    internal sealed record PistaSubtitulos(bool EsImagen, string? Idioma, string? Titulo);

    /// <summary>
    /// Índice de la pista a mostrar, o -1: primero el idioma de la app, luego inglés, luego cualquiera. Dentro de cada idioma se
    /// evitan las pistas de solo carteles/canciones ("Signs &amp; Songs", "Forced"), que casi no muestran diálogo, y se prefieren
    /// las de texto a las de imagen (PGS/VobSub). Caso real: un archivo con 11 pistas y ninguna marcada no mostraba nada.
    /// </summary>
    internal static int ElegirPista(IReadOnlyList<PistaSubtitulos> pistas, string idiomaPreferido)
    {
        if (pistas.Count == 0) return -1;

        int Puntos(PistaSubtitulos p)
        {
            int puntos = 0;
            if (string.Equals(p.Idioma, idiomaPreferido, StringComparison.OrdinalIgnoreCase)) puntos += 100;
            else if (string.Equals(p.Idioma, "en", StringComparison.OrdinalIgnoreCase)) puntos += 50;
            if (!EsSoloCarteles(p.Titulo)) puntos += 20;
            if (!p.EsImagen) puntos += 5;
            return puntos;
        }

        int mejor = 0;
        for (int i = 1; i < pistas.Count; i++)
        {
            if (Puntos(pistas[i]) > Puntos(pistas[mejor])) mejor = i; // a igualdad, la primera (el orden del archivo)
        }
        return mejor;
    }

    private static readonly string[] MarcasSoloCarteles = { "sign", "song", "forced", "forzad", "cartel", "karaoke" };

    private static bool EsSoloCarteles(string? titulo) =>
        !string.IsNullOrWhiteSpace(titulo) && MarcasSoloCarteles.Any(m => titulo.Contains(m, StringComparison.OrdinalIgnoreCase));

    public void SeleccionarPista(Player? player, object stream)
    {
        if (player == null || stream == null) return;

        Habilitar(player);
        player.OpenAsync((dynamic)stream);
    }
}
