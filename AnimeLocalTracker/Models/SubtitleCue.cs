using System;

namespace AnimeLocalTracker.Models;

/// <summary>Una línea de subtítulo con su ventana de tiempo, ya extraída y parseada (independiente de Flyleaf).</summary>
public sealed record SubtitleCue(TimeSpan Inicio, TimeSpan Fin, string Texto);
