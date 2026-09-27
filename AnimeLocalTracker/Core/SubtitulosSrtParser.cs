using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Parseo puro (sin E/S) del formato SRT: es el formato al que se normaliza cualquier pista de subtítulos antes de
/// leerla (ffmpeg convierte ASS/SSA/VTT/lo que sea a SRT al extraerla), así solo hace falta un parser.
/// </summary>
public static class SubtitulosSrtParser
{
    private static readonly Regex Marca = new(
        @"(\d{2}):(\d{2}):(\d{2})[.,](\d{1,3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[.,](\d{1,3})",
        RegexOptions.Compiled);

    public static List<SubtitleCue> Parsear(string? contenidoSrt)
    {
        var cues = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(contenidoSrt)) return cues;

        // Bloques separados por línea(s) en blanco. El número de índice (si está) se ignora: no hace falta para nada.
        var lineas = contenidoSrt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int i = 0;
        while (i < lineas.Length)
        {
            // Saltar líneas vacías entre bloques.
            while (i < lineas.Length && lineas[i].Trim().Length == 0) i++;
            if (i >= lineas.Length) break;

            // Línea de índice opcional (un número solo): si la línea actual no es una marca de tiempo, se asume
            // índice y se avanza; si tampoco la siguiente es marca de tiempo, el bloque es basura y se descarta.
            Match match = Marca.Match(lineas[i]);
            if (!match.Success)
            {
                i++;
                if (i >= lineas.Length) break;
                match = Marca.Match(lineas[i]);
            }

            if (!match.Success)
            {
                // Bloque irreconocible: se avanza hasta la próxima línea en blanco y se sigue con el siguiente.
                while (i < lineas.Length && lineas[i].Trim().Length != 0) i++;
                continue;
            }

            i++; // consumir la línea de la marca de tiempo

            var texto = new StringBuilder();
            while (i < lineas.Length && lineas[i].Trim().Length != 0)
            {
                if (texto.Length > 0) texto.Append('\n');
                texto.Append(LimpiarEtiquetas(lineas[i]));
                i++;
            }

            var inicio = LeerTiempo(match, 1);
            var fin = LeerTiempo(match, 5);
            string textoLimpio = texto.ToString().Trim();

            if (fin > inicio && textoLimpio.Length > 0)
            {
                cues.Add(new SubtitleCue(inicio, fin, textoLimpio));
            }
        }

        return cues;
    }

    private static TimeSpan LeerTiempo(Match m, int baseGroup)
    {
        int horas = int.Parse(m.Groups[baseGroup].Value, CultureInfo.InvariantCulture);
        int minutos = int.Parse(m.Groups[baseGroup + 1].Value, CultureInfo.InvariantCulture);
        int segundos = int.Parse(m.Groups[baseGroup + 2].Value, CultureInfo.InvariantCulture);
        string msTexto = m.Groups[baseGroup + 3].Value.PadRight(3, '0');
        int milisegundos = int.Parse(msTexto, CultureInfo.InvariantCulture);
        return new TimeSpan(0, horas, minutos, segundos, milisegundos);
    }

    /// <summary>Quita etiquetas HTML/SSA simples (&lt;i&gt;, {\an8}...) que a veces sobreviven a la conversión a SRT:
    /// aquí solo se necesita el texto, el estilo lo decide la app (ver EstiloSubtitulos).</summary>
    private static string LimpiarEtiquetas(string linea)
    {
        linea = Regex.Replace(linea, "<[^>]+>", "");
        linea = Regex.Replace(linea, @"\{[^}]*\}", "");
        return linea.Trim();
    }
}
