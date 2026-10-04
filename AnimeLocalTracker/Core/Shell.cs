using System.Diagnostics;

namespace AnimeLocalTracker.Core;

public static class Shell
{
    /// <summary>Abre una URL, una carpeta o un archivo con el programa que Windows tenga asociado (navegador, Explorador,
    /// reproductor…). Lanza la excepción de <see cref="Process.Start(ProcessStartInfo)"/> si Windows no puede abrirlo.</summary>
    public static void Abrir(string ruta) =>
        Process.Start(new ProcessStartInfo { FileName = ruta, UseShellExecute = true })?.Dispose();
}
