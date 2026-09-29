using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.Models;

/// <summary>Una pista de audio del episodio en el menú del reproductor (solo se muestra el menú si hay más de una).</summary>
public sealed partial class OpcionPistaAudio : ObservableObject
{
    public OpcionPistaAudio(object pista, string nombre, string? idioma, bool esActual)
    {
        Pista = pista;
        Nombre = nombre;
        Idioma = idioma;
        _esActual = esActual;
    }

    /// <summary>El AudioStream de Flyleaf (object para no atar el modelo a la librería).</summary>
    public object Pista { get; }

    /// <summary>"Japonés · Original", "Inglés", "Pista 2"…</summary>
    public string Nombre { get; }

    /// <summary>Código de dos letras ("ja", "en"…), o null si el archivo no lo indica.</summary>
    public string? Idioma { get; }

    [ObservableProperty] private bool _esActual;

    /// <summary>Nombre legible: idioma en el idioma de Windows (p. ej. "japonés") más el título de la pista si lo trae.</summary>
    public static string ConstruirNombre(string? idioma, string? titulo, int numero)
    {
        string? nombreIdioma = null;
        if (!string.IsNullOrWhiteSpace(idioma))
        {
            try
            {
                var cultura = CultureInfo.GetCultureInfo(idioma);
                string texto = cultura.DisplayName;
                nombreIdioma = texto.Length > 0 ? char.ToUpper(texto[0], CultureInfo.CurrentCulture) + texto[1..] : texto;
            }
            catch (CultureNotFoundException)
            {
                nombreIdioma = idioma;
            }
        }

        var partes = new[] { nombreIdioma, string.IsNullOrWhiteSpace(titulo) ? null : titulo!.Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return partes.Count > 0 ? string.Join(" · ", partes) : $"#{numero}";
    }

    /// <summary>La pista del idioma que el usuario eligió la última vez (null = dejar la del archivo).</summary>
    public static OpcionPistaAudio? ElegirPreferida(IReadOnlyList<OpcionPistaAudio> opciones, string? idiomaPreferido)
    {
        if (string.IsNullOrWhiteSpace(idiomaPreferido) || opciones.Count < 2) return null;
        return opciones.FirstOrDefault(o => string.Equals(o.Idioma, idiomaPreferido, StringComparison.OrdinalIgnoreCase));
    }
}
