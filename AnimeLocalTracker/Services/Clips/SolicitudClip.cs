namespace AnimeLocalTracker.Services.Clips;

public enum FormatoClip { Mp4, Gif }

/// <summary>Subtítulos a incrustar: el archivo que los contiene (el propio video o uno externo) y, si es el video, cuál de sus
/// pistas de subtítulos es (contando solo subtítulos, como pide el filtro de ffmpeg).</summary>
public sealed record SubtitulosClip(string Archivo, int? IndiceRelativo);

/// <param name="IndiceAudio">Índice del flujo de audio dentro del archivo (el que se está oyendo); -1 si no se sabe.</param>
public sealed record SolicitudClip(string RutaVideo, double InicioSeg, double DuracionSeg, FormatoClip Formato, int IndiceAudio,
    SubtitulosClip? Subtitulos, string RutaSalida);

/// <summary>Tramo listo para crear el clip. <paramref name="Recortado"/>: el fin marcado quedaba más allá del máximo.</summary>
public sealed record RangoClip(double InicioSeg, double DuracionSeg, bool Recortado);
