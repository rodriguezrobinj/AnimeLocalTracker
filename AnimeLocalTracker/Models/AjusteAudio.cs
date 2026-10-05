using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SQLite;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Sonido del reproductor recordado para un anime entero (<see cref="NumeroEpisodio"/> = 0) o para un capítulo concreto:
/// volumen, ecualizador y Modo Noche. Solo se usa cuando <see cref="AppSettings.AmbitoAjustesAudio"/> no es global.
/// Tabla creada por la migración v20.
/// </summary>
public class AjusteAudio
{
    /// <summary>"{AniListId}_{NumeroEpisodio}" (sqlite-net no admite claves primarias compuestas).</summary>
    [PrimaryKey]
    public string Clave { get; set; } = string.Empty;

    public int AniListId { get; set; }

    /// <summary>0 = vale para todos los capítulos del anime.</summary>
    public int NumeroEpisodio { get; set; }

    public int Volumen { get; set; } = 100;
    public bool EcualizadorActivo { get; set; }

    /// <summary>Las ganancias (dB) de las bandas, separadas por ';'. Ver <see cref="Ganancias"/>.</summary>
    public string EcualizadorGanancias { get; set; } = string.Empty;

    public bool ModoNoche { get; set; }

    public static string ClaveDe(int aniListId, int numeroEpisodio) => $"{aniListId}_{numeroEpisodio}";

    [Ignore]
    public List<double> Ganancias
    {
        get => EcualizadorGanancias.Split(';', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ? g : 0)
            .ToList();
        set => EcualizadorGanancias = string.Join(";", (value ?? new List<double>()).Select(g => g.ToString("0.##", CultureInfo.InvariantCulture)));
    }
}
