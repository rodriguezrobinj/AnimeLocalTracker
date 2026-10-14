using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

public enum EstadoMotor { Detenido, Abriendo, Reproduciendo, EnPausa, Terminado, Fallido }

/// <summary>Qué terminó de abrirse: el archivo (o una pista de audio suya) o una pista de subtítulos que dibuja el propio motor.</summary>
public enum ClaseApertura { Video, Subtitulos }

public sealed record AperturaTerminada(ClaseApertura Clase, bool Exito, string? Error);

public enum FabricanteGrafica { Desconocido, Intel, Nvidia, Amd }

public enum NivelEscalado { Ligero, Medio }

/// <summary>Una pista de audio del archivo abierto. <paramref name="Idioma"/> en dos letras, o null si no se sabe.</summary>
public sealed record PistaAudioMotor(int Indice, string? Idioma, string? Titulo);

/// <summary>Una pista de subtítulos del archivo abierto, o un archivo suelto junto al video (<paramref name="RutaExterna"/>).</summary>
public sealed record PistaSubtitulosMotor(int Indice, string? Idioma, string? Titulo, bool EsImagen, bool EsAss, string? RutaExterna);

/// <summary>Lo que el motor necesita saber antes de abrir el primer archivo.</summary>
/// <param name="VideoPorSoftware">Modo de compatibilidad: pintar sin OpenGL, aunque el equipo lo tenga.</param>
/// <param name="ColorLigero">Interpolar el color de la forma más barata para la tarjeta, a cambio de bordes de color algo menos nítidos.</param>
/// <param name="CarpetaShaders">Dónde guardar los shaders ya compilados para no compilarlos en cada apertura; null, sin guardar (pruebas).</param>
public sealed record AjustesInicialesMotor(bool AceleracionPorHardware, IReadOnlyList<FiltroAudio> FiltrosAudio, bool VideoPorSoftware = false,
    bool ColorLigero = false, string? CarpetaShaders = null);

/// <summary>
/// El motor de video del reproductor de episodios, sin tipos de ninguna biblioteca. Lo implementan <c>MotorMpv</c> (libmpv) y el
/// motor de mentira de las pruebas; la coreografía (arrancar en silencio hasta el primer fotograma, reanudar, reintentar) sigue en
/// el reproductor.
/// Salvo que se diga otra cosa, los miembros no lanzan: si el motor no puede, no hace nada.
/// </summary>
public interface IMotorVideo : IDisposable
{
    bool Desechado { get; }
    EstadoMotor Estado { get; }
    TimeSpan Posicion { get; }
    TimeSpan Duracion { get; }

    /// <summary>Puede llegar desde un hilo del motor, no desde el de la interfaz.</summary>
    event EventHandler<AperturaTerminada>? AperturaTerminada;

    void Abrir(string ruta);
    void Reproducir();
    void Pausar();
    void Detener();
    void Saltar(TimeSpan destino, bool exacto);
    double Velocidad { set; }
    int Volumen { set; }
    bool Silencio { set; }

    /// <summary>Fotogramas presentados desde que se abrió el archivo, reproduciendo o en pausa.</summary>
    long FotogramasMostrados { get; }
    bool DecodificaPorHardware { get; }
    bool AceleracionPorHardware { set; }
    string? CodecVideo { get; }
    (int Ancho, int Alto) TamanoVideo { get; }
    /// <summary>Ancho entre alto del video abierto; 0 si aún no se sabe.</summary>
    double ProporcionVideo { get; }
    event EventHandler? ProporcionVideoCambiada;
    TarjetaGrafica? TarjetaEnUso { get; }
    bool EscaladoInteligente { get; set; }
    /// <summary>Fotogramas que el motor no llegó a pintar a tiempo desde que abrió el archivo. 0 si el motor no lo sabe.</summary>
    long FotogramasPerdidos { get; }
    BitmapSource? CapturarFotograma();

    IReadOnlyList<PistaAudioMotor> PistasAudio { get; }
    int IndicePistaAudioActual { get; }
    void ElegirAudio(PistaAudioMotor pista);

    IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos { get; }
    /// <summary>Índice de la pista que el propio motor tiene abierta, o -1.</summary>
    int IndicePistaSubtitulosDelMotor { get; }
    void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista);
    void ApagarSubtitulosDelMotor();
    /// <summary>Aspecto de los subtítulos que dibuja el motor. <paramref name="tambienEnAss"/>: usarlo también en las pistas ASS, en vez de su estilo original.</summary>
    void AplicarEstiloSubtitulos(EstiloSubtitulos estilo, bool tambienEnAss);

    /// <returns>False si el motor no pudo montar la cadena.</returns>
    bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros);
    /// <returns>False si ese filtro no admite el cambio sin remontar la cadena.</returns>
    bool CambiarFiltroEnVivo(string id, string clave, string valor);
    /// <summary>Deja apuntada la cadena para el siguiente archivo sin remontar la que suena (después de un cambio en vivo).</summary>
    void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros);
}
