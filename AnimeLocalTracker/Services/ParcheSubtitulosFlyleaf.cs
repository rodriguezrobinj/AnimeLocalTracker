using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FlyleafLib.MediaFramework.MediaFrame;
using HarmonyLib;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Parche en caliente (Harmony) sobre el lector de estilos ASS de FlyleafLib. <c>ParseSubtitles.SSAtoSubStyles</c> convierte los
/// colores con <c>int.Parse</c> sin protegerse y, con un bloque como <c>{\c&amp;HA96974&amp;\fad(..)..\3a&amp;H37&amp;..}</c> (el cartel
/// del título de Re:Zero 4th Season), lanza <see cref="FormatException"/> en un hilo propio de Flyleaf: la app entera se cerraba.
/// La app ya lee por su cuenta las pistas de texto (ver <c>ReproductorViewModel.AbrirPistaSubtitulos</c>); esto cubre los casos
/// en que Flyleaf sí decodifica una (respaldo si falla la extracción, o la pista que él mismo abre al encenderle los subtítulos).
/// FlyleafLib 3.11.11 trae el mismo fallo: revisar si sigue haciendo falta al actualizar la librería.
/// </summary>
public static partial class ParcheSubtitulosFlyleaf
{
    private const string IdHarmony = "AnimeLocalTracker.ParcheSubtitulosFlyleaf";

    private static readonly object _candado = new();
    private static bool _aplicado;
    private static bool _avisado;

    /// <summary>Aplica el parche una sola vez por proceso. False si no se pudo (queda registrado; la app sigue como antes).</summary>
    public static bool Aplicar()
    {
        lock (_candado)
        {
            if (_aplicado) return true;

            try
            {
                var original = AccessTools.Method(typeof(ParseSubtitles), nameof(ParseSubtitles.SSAtoSubStyles));
                if (original == null)
                {
                    AppLogger.Warn("ParcheSubtitulosFlyleaf", "No se encontró SSAtoSubStyles en FlyleafLib: el parche de subtítulos no se aplica.");
                    return false;
                }

                new Harmony(IdHarmony).Patch(original, finalizer: new HarmonyMethod(typeof(ParcheSubtitulosFlyleaf), nameof(AlFallar)));
                _aplicado = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ParcheSubtitulosFlyleaf", $"No se pudo aplicar el parche de subtítulos de Flyleaf: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Finalizador de Harmony: se ejecuta al salir de <c>SSAtoSubStyles</c>. Si la función falló, se traga la excepción y devuelve
    /// la línea como texto plano sin estilos (se pierde la cursiva o el color de ESA línea, no la aplicación).
    /// </summary>
    private static Exception? AlFallar(Exception? __exception, string s, ref List<SubStyle> styles, ref string __result)
    {
        if (__exception == null) return null;

        styles = new List<SubStyle>();
        __result = TextoSinEtiquetas(s);

        if (!_avisado)
        {
            _avisado = true;
            AppLogger.Warn("ParcheSubtitulosFlyleaf", $"Flyleaf no supo leer el estilo de una línea de subtítulos ({__exception.GetType().Name}: {__exception.Message}); se muestra como texto plano.");
        }
        return null;
    }

    /// <summary>Texto de una línea de diálogo ASS sin sus bloques de etiquetas, con los saltos de línea ya convertidos.</summary>
    internal static string TextoSinEtiquetas(string? linea)
    {
        if (string.IsNullOrEmpty(linea)) return string.Empty;

        // Igual que Flyleaf: el texto es lo que sigue al último ",," (tras los campos de estilo/actor/márgenes/efecto).
        int inicio = linea.LastIndexOf(",,", StringComparison.Ordinal);
        string texto = inicio >= 0 ? linea[(inicio + 2)..] : linea;

        texto = BloqueEtiquetas().Replace(texto, string.Empty);
        return texto.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex BloqueEtiquetas();
}
