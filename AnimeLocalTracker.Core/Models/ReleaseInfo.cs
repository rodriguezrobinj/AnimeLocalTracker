using System;

namespace AnimeLocalTracker.Core.Models;

public class ReleaseInfo
{
    public string Version { get; set; } = "v1.0.0";
    public string Titulo { get; set; } = "Versión Inicial";
    public DateTime? FechaPublicacion { get; set; }
    public string NotasVersion { get; set; } = "• Gestor y reproductor nativo multimedia para colecciones de anime locales.\n• Auto-tracking local y sincronización bidireccional con AniList.\n• Motor multimedia acelerado por hardware con Flyleaf y DirectX.\n• Sistema de actualizaciones automáticas integrado con GitHub Releases.";
    public string UrlRelease { get; set; } = "https://github.com/rodriguezrobinj/AnimeLocalTracker/releases";
}
