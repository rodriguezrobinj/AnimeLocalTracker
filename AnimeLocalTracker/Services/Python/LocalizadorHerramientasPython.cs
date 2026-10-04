using System.Collections.Generic;
using System.IO;

namespace AnimeLocalTracker.Services.Python;

/// <summary>
/// Dónde están el daemon Python y el plugin de audio propio de la app. Lo que se encuentra aquí SE EJECUTA, así que en un
/// build publicado solo vale lo que viene dentro de la instalación. Buscar además "tools\python\…" en las carpetas
/// superiores (y en el directorio actual) es cómodo en desarrollo, pero en una app instalada dejaba que cualquiera capaz
/// de crear esa carpeta más arriba en el disco —otro programa, u otro usuario del PC si la app está fuera del perfil—
/// hiciera correr su script, sin pasar por la confianza de plugins.
/// </summary>
internal static class LocalizadorHerramientasPython
{
#if DEBUG
    internal const bool CompilacionDeDesarrollo = true;
#else
    internal const bool CompilacionDeDesarrollo = false;
#endif

    private const int NivelesHaciaArriba = 6;

    /// <summary>"tools\python\<paramref name="archivo"/>" subiendo desde <paramref name="baseDir"/> (el repositorio, en desarrollo).</summary>
    internal static string? BuscarEnRepositorio(string baseDir, string archivo)
    {
        var carpeta = new DirectoryInfo(baseDir);
        for (int i = 0; i < NivelesHaciaArriba && carpeta != null; i++, carpeta = carpeta.Parent)
        {
            string candidato = Path.Combine(carpeta.FullName, "tools", "python", archivo);
            if (File.Exists(candidato)) return candidato;
        }
        return null;
    }

    /// <summary>
    /// audio_skip_plugin.py: en desarrollo manda el de tools/python (única fuente de verdad); publicado, solo la copia
    /// empaquetada en PythonPlugins/ (la sincroniza el .csproj). Null si no hay ninguna utilizable.
    /// </summary>
    internal static string? PluginAudioSkip(string baseDir, bool desarrollo)
    {
        if (desarrollo && BuscarEnRepositorio(baseDir, "audio_skip_plugin.py") is { } delRepositorio) return delRepositorio;

        string empaquetado = Path.Combine(baseDir, "PythonPlugins", "audio_skip_plugin.py");
        return File.Exists(empaquetado) ? empaquetado : null;
    }

    /// <summary>
    /// El daemon: el ejecutable empaquetado (--onedir: en una subcarpeta con su nombre; se mantienen las rutas antiguas
    /// por compatibilidad con builds previos) o, solo en desarrollo, el script cli.py del repositorio.
    /// </summary>
    internal static (string? Ejecutable, string? Script) Daemon(string baseDir, string directorioActual, bool desarrollo)
    {
        var ejecutables = new List<string>
        {
            Path.Combine(baseDir, "Tools", "AnimeTrackerTools", "AnimeTrackerTools.exe"),
            Path.Combine(baseDir, "Tools", "AnimeTrackerTools.exe"),
            Path.Combine(baseDir, "AnimeTrackerTools.exe")
        };
        if (desarrollo)
        {
            ejecutables.Add(Path.Combine(directorioActual, "AnimeLocalTracker", "Tools", "AnimeTrackerTools", "AnimeTrackerTools.exe"));
            ejecutables.Add(Path.Combine(directorioActual, "AnimeLocalTracker", "Tools", "AnimeTrackerTools.exe"));
        }

        foreach (var ruta in ejecutables)
        {
            if (File.Exists(ruta)) return (ruta, null);
        }

        if (!desarrollo) return (null, null);

        if (BuscarEnRepositorio(baseDir, "cli.py") is { } script) return (null, script);

        string enDirectorioActual = Path.Combine(directorioActual, "tools", "python", "cli.py");
        return (null, File.Exists(enDirectorioActual) ? Path.GetFullPath(enDirectorioActual) : null);
    }
}
