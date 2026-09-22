using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeLocalTracker.Models;

public class AppSettings
{
    public string RutaBaseAnimes { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
    public bool AutoPlaySiguiente { get; set; } = true;
    public bool AutoSkipIntroOutro { get; set; } = false;
    public bool SubtitulosPorDefecto { get; set; } = true;
    public int DescargasSimultaneas { get; set; } = 3;
    public int IntervaloSincronizacionMinutos { get; set; } = 5;
    public bool BuscarActualizacionesAlIniciar { get; set; } = true;
    /// <summary>Porcentaje reproducido a partir del cual un episodio se marca como visto (1-100).
    /// FUN-003: este valor ES el que gobierna el auto-marcado (el producto se anunció al 90%
    /// en el README; antes el ajuste era ignorado y el disparo estaba fijo en 90%).</summary>
    public int UmbralMarcadoVisto { get; set; } = 90;
    /// <summary>Notifica episodios nuevos detectados en las carpetas de la biblioteca.</summary>
    public bool NotificarNuevosEpisodios { get; set; } = true;
    /// <summary>Idioma de la interfaz: "es" o "en".</summary>
    public string Idioma { get; set; } = "es";
    /// <summary>Velocidad de reproducción aplicada al abrir un video (0.5 a 2.0).</summary>
    public double VelocidadReproduccionDefecto { get; set; } = 1.0;
    /// <summary>Compresor de rango dinámico de audio ("modo noche") activo por defecto al abrir un video.</summary>
    public bool ModoNocheActivo { get; set; } = false;
    /// <summary>Al cerrar/minimizar la ventana, se oculta a la bandeja del sistema en vez de salir.
    /// Desactivado por defecto: no cambia el comportamiento del botón cerrar para quien no lo pida.</summary>
    public bool MinimizarABandejaAlCerrar { get; set; } = false;
    /// <summary>Los avisos de episodios nuevos/descargas usan siempre el globo nativo de Windows,
    /// aunque la ventana esté visible y activa (por defecto solo se usa si está oculta/minimizada).
    /// Independiente de <see cref="MinimizarABandejaAlCerrar"/>: activarlo mantiene un icono en la
    /// bandeja mientras la app está abierta aunque esa otra opción esté desactivada.</summary>
    public bool NotificarConBandejaSiempre { get; set; } = false;

    /// <summary>
    /// Atajos de teclado configurables del reproductor: acción → tecla.
    /// Claves: PlayPausa, PantallaCompleta, Silenciar, SubirVolumen, BajarVolumen,
    /// Adelantar10, Retroceder10, SaltarIntro, SiguienteEpisodio, AnteriorEpisodio, ModoMini, Cerrar.
    /// PIP-01: AnteriorEpisodio se movió de "P" a "B" — "P" queda para Modo Mini/PiP (los
    /// tooltips de esa función ya prometían "(P)" desde antes de que existiera este atajo).
    /// </summary>
    public Dictionary<string, string> Atajos { get; set; } = new()
    {
        ["PlayPausa"] = "Space",
        ["PantallaCompleta"] = "F11",
        ["Silenciar"] = "M",
        ["SubirVolumen"] = "Up",
        ["BajarVolumen"] = "Down",
        ["Adelantar10"] = "Right",
        ["Retroceder10"] = "Left",
        ["SaltarIntro"] = "S",
        ["SiguienteEpisodio"] = "N",
        ["AnteriorEpisodio"] = "B",
        ["ModoMini"] = "P",
        ["Cerrar"] = "Escape",
        ["CapturarFrame"] = "C"
    };

    /// <summary>Devuelve la tecla configurada para una acción (con fallback al valor por defecto).</summary>
    public string ObtenerTecla(string accion, string defecto)
    {
        if (Atajos != null && Atajos.TryGetValue(accion, out var tecla) && !string.IsNullOrWhiteSpace(tecla))
            return tecla;
        return defecto;
    }
}
