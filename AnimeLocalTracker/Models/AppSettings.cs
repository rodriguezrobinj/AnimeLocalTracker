using System;
using System.IO;

namespace AnimeLocalTracker.Models;

public class AppSettings
{
    public string RutaBaseAnimes { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
    public bool AutoPlaySiguiente { get; set; } = true;
    public bool SubtitulosPorDefecto { get; set; } = true;
    public int DescargasSimultaneas { get; set; } = 3;
    public int IntervaloSincronizacionMinutos { get; set; } = 5;
    public bool BuscarActualizacionesAlIniciar { get; set; } = true;
}
