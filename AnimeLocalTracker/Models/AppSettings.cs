using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeLocalTracker.Models;

public class AppSettings
{
    public string RutaBaseAnimes { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
    public bool AutoSkipIntroOutro { get; set; } = false;
    public bool SubtitulosPorDefecto { get; set; } = true;
    /// <summary>Apariencia de los subtítulos (tipografía, tamaño, color, contorno, caja). Los valores
    /// por defecto conservan el aspecto anterior a que fuera configurable.</summary>
    public EstiloSubtitulos EstiloSubtitulos { get; set; } = new();
    /// <summary>SEC-01: interruptor general de los plugins de terceros (.dll y .py de la carpeta Plugins). Apagado por
    /// defecto: un plugin corre con TODOS los permisos de la app, así que solo se ejecuta si el usuario lo permite
    /// aquí Y ha marcado ese archivo concreto como confiable (<see cref="PluginsConfiables"/>).</summary>
    public bool PluginsHabilitados { get; set; } = false;

    /// <summary>SEC-01: archivos de plugin en los que el usuario confió: nombre de archivo → huella SHA-256 (hex
    /// en mayúsculas) del contenido en el momento de confiar. Si el archivo cambia, la huella ya no coincide y deja de
    /// ser confiable hasta que el usuario lo confirme otra vez.</summary>
    public Dictionary<string, string> PluginsConfiables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Segundos que saltan los botones/atajos de retroceder y adelantar (5, 10, 30 o 60).</summary>
    public int PasosSaltoSegundos { get; set; } = 10;

    /// <summary>Evita que Windows apague la pantalla o active el protector mientras hay un video
    /// reproduciéndose (SetThreadExecutionState). No afecta el suspendido manual del usuario.</summary>
    public bool EvitarSuspensionPantalla { get; set; } = true;

    /// <summary>Qué hacer al terminar un episodio: uno de los valores de <see cref="AccionFinEpisodioValores"/>.</summary>
    public string AccionFinEpisodio { get; set; } = AccionFinEpisodioValores.AutoPlayCuentaAtras;

    /// <summary>Pista de audio preferida al descargar de AnimeAV1: uno de los valores de
    /// <see cref="PreferenciaAudioValores"/>, o null/vacío = sin preferencia (comportamiento
    /// de siempre: se toma la primera pista que el sitio publique). Es una preferencia con
    /// fallback, no un filtro estricto: si el episodio no tiene esa pista, se descarga en la otra.</summary>
    public string? PreferenciaAudioAnimeAv1 { get; set; }

    /// <summary>Servidor preferido al descargar de AnimeAV1: uno de los valores de
    /// <see cref="ServidorPreferidoValores"/>, o null/vacío = sin preferencia (orden de
    /// siempre: MP4Upload primero). Es una preferencia con fallback, no un filtro estricto:
    /// si el servidor pedido no resuelve el episodio, se sigue probando el resto en el
    /// orden habitual en vez de fallar la descarga.</summary>
    public string? ServidorPreferidoAnimeAv1 { get; set; }

    /// <summary>Busca y descarga por torrent en Nyaa.si (MonoTorrent) como último recurso,
    /// solo cuando ninguna fuente HTTP encontró el episodio. Apagado por defecto: es una
    /// superficie de red nueva (escucha peers entrantes, puede disparar el aviso de
    /// Firewall de Windows la primera vez) que el usuario debe activar a propósito.</summary>
    public bool BusquedaTorrentHabilitada { get; set; } = false;

    /// <summary>Grupo de fansub preferido al elegir candidato de torrent (Fase 2b, ej.
    /// "SubsPlease"). Texto libre: los nombres de grupo varían demasiado para una lista
    /// cerrada. Preferencia con fallback — si ningún candidato es de este grupo, se elige
    /// igual el de más semillas entre el resto.</summary>
    public string? GrupoFansubPreferidoTorrent { get; set; }

    /// <summary>Resolución preferida al elegir candidato de torrent (Fase 2b): uno de los
    /// valores de <see cref="ResolucionTorrentValores"/>, o null = sin preferencia. Misma
    /// preferencia con fallback que <see cref="GrupoFansubPreferidoTorrent"/>.</summary>
    public string? ResolucionPreferidaTorrent { get; set; }

    /// <summary>Fase 2c: al completar una descarga por torrent, seguir sembrando (subiendo
    /// piezas a otros peers) en vez de detenerse de inmediato. Apagado por defecto — tiene
    /// un costo real de ancho de banda de subida mientras la app esté abierta, y duplica
    /// temporalmente el espacio en disco del episodio (queda una copia en la carpeta interna
    /// del torrent además de la copia final en la biblioteca). Se detiene todo el sembrado
    /// activo al cerrar la app — no hay sembrado persistente entre sesiones.</summary>
    public bool SeguirSembrandoTorrents { get; set; } = false;

    /// <summary>Activa un atajo de teclado GLOBAL (funciona aunque la app no tenga el foco) que silencia
    /// el video y minimiza la app a la bandeja del sistema al instante ("boss key").</summary>
    public bool TeclaPanicoActiva { get; set; } = false;

    /// <summary>Tecla del atajo de pánico: "F12" o "Escape". Escape como atajo GLOBAL intercepta esa
    /// tecla en TODAS las aplicaciones mientras esté activo, no solo en AnimeLocalTracker.</summary>
    public string TeclaPanico { get; set; } = "F12";

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

/// <summary>Valores válidos de <see cref="AppSettings.AccionFinEpisodio"/>.</summary>
public static class AccionFinEpisodioValores
{
    /// <summary>Muestra una cuenta atrás de 5s (cancelable) y luego reproduce el siguiente episodio.</summary>
    public const string AutoPlayCuentaAtras = "AutoPlayCuentaAtras";
    /// <summary>Reproduce el siguiente episodio de inmediato, sin cuenta atrás.</summary>
    public const string AutoPlayInmediato = "AutoPlayInmediato";
    /// <summary>Sale del reproductor y vuelve a la ficha del anime.</summary>
    public const string PausarYSalirFicha = "PausarYSalirFicha";
    /// <summary>Se queda en pantalla completa, pausado en el último fotograma.</summary>
    public const string PermanecerPausado = "PermanecerPausado";
}

/// <summary>Valores válidos de <see cref="AppSettings.PreferenciaAudioAnimeAv1"/> — coinciden
/// tal cual con las claves que publica el sitio (embeds:{SUB:[...],DUB:[...]}).</summary>
public static class PreferenciaAudioValores
{
    /// <summary>Japonés con subtítulos.</summary>
    public const string Subtitulado = "SUB";
    /// <summary>Doblaje latino.</summary>
    public const string Latino = "DUB";
}

/// <summary>Valores válidos de <see cref="AppSettings.ServidorPreferidoAnimeAv1"/> — coinciden
/// tal cual con los nombres de servidor que publica el sitio. Hoy solo <see cref="Mp4Upload"/>
/// resuelve de forma fiable (ver comentario de OrdenarEmbedsPorPreferencia); los demás quedan
/// disponibles para cuando el sitio o el daemon ganen un extractor real.</summary>
public static class ServidorPreferidoValores
{
    public const string Mp4Upload = "MP4Upload";
    public const string Hls = "HLS";
    public const string Voe = "Voe";
    public const string UpnShare = "UPNShare";
    public const string Byse = "Byse";
}

/// <summary>Valores válidos de <see cref="AppSettings.ResolucionPreferidaTorrent"/> (Fase 2b).
/// Coinciden tal cual con cómo los títulos de Nyaa anuncian la resolución
/// (ej. "(1080p)"), para poder buscarlos como substring sin parsing adicional.</summary>
public static class ResolucionTorrentValores
{
    public const string Res1080p = "1080p";
    public const string Res720p = "720p";
    public const string Res480p = "480p";
}
