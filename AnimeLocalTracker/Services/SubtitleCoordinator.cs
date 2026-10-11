using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Services;

public class SubtitleCoordinator : ISubtitleCoordinator
{
    public bool DebenHabilitarsePorDefecto(IMotorVideo? motor, bool permitirSubtitulosConfig) =>
        permitirSubtitulosConfig && motor != null && motor.PistasSubtitulos.Count > 0;

    public void Deshabilitar(IMotorVideo? motor) => motor?.ApagarSubtitulosDelMotor();

    public PistaSubtitulosMotor? PistaPorDefecto(IMotorVideo? motor, string idiomaPreferido)
    {
        if (motor == null || motor.IndicePistaSubtitulosDelMotor >= 0) return null;
        var pistas = motor.PistasSubtitulos;
        int i = ElegirPista(pistas.Select(s => new PistaSubtitulos(s.EsImagen, s.Idioma, s.Titulo)).ToList(), idiomaPreferido);
        return i >= 0 ? pistas[i] : null;
    }

    public void SeleccionarPista(IMotorVideo? motor, PistaSubtitulosMotor pista) => motor?.MostrarSubtitulosDelMotor(pista);

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
}
