using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AnimeLocalTracker.Services;

/// <summary>
/// "Borrar todos mis datos": la carpeta de datos ENTERA, no una lista de subcarpetas (que se quedaba corta cada vez que la
/// app estrenaba una: música, personajes, cola de descargas, ajustes…). Con la app abierta no se puede: tiene abiertos la
/// base de datos, el registro y las imágenes en pantalla. Por eso lo hace un segundo proceso de la propia app
/// (<c>--borrar-datos &lt;pid&gt;</c>) que espera a que esta se cierre, borra y termina sin llegar a cargar nada.
/// No usa <see cref="AppLogger"/>: escribir un registro volvería a crear la carpeta que se está borrando.
/// </summary>
internal static class BorradoTotalDeDatos
{
    internal const string Argumento = "--borrar-datos";

    private static readonly TimeSpan EsperaMaximaAlCierre = TimeSpan.FromSeconds(30);

    /// <summary>Todo lo que la app guarda en el equipo fuera de la carpeta de anime del usuario.</summary>
    internal static IReadOnlyList<string> CarpetasDeDatos() =>
    [
        AppDataPaths.DataRoot,
        DownloadService.CarpetaBaseTemporalTorrents,
        AppDataPaths.RutaRoamingAntigua()
    ];

    /// <summary>Lanza el proceso que borrará los datos cuando esta app se cierre. False si no se pudo lanzar.</summary>
    internal static bool LanzarTrasCerrar()
    {
        try
        {
            string? ejecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(ejecutable)) return false;

            var psi = new ProcessStartInfo(ejecutable) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(Argumento);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var proceso = Process.Start(psi);
            return proceso != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Si la línea de órdenes pide el borrado, espera a que la app anterior se cierre, borra y devuelve true: quien llama
    /// debe salir sin iniciar la app.
    /// </summary>
    internal static bool EjecutarSiSePidio(string[] args)
    {
        if (!TryLeerPeticion(args, out int pidAnterior)) return false;

        if (pidAnterior > 0)
        {
            try
            {
                using var anterior = Process.GetProcessById(pidAnterior);
                anterior.WaitForExit(EsperaMaximaAlCierre);
            }
            catch (Exception)
            {
                // Ya había terminado (o no se puede consultar): se sigue con el borrado.
            }
        }

        BorrarCarpetas(CarpetasDeDatos());
        return true;
    }

    internal static bool TryLeerPeticion(string[] args, out int pidAnterior)
    {
        pidAnterior = 0;
        int indice = Array.FindIndex(args, a => string.Equals(a, Argumento, StringComparison.OrdinalIgnoreCase));
        if (indice < 0) return false;

        if (indice + 1 < args.Length && int.TryParse(args[indice + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) pidAnterior = pid;
        return true;
    }

    /// <summary>Una ruta absoluta que no sea la raíz de una unidad: lo único que este borrado acepta tocar.</summary>
    internal static bool EsCarpetaBorrable(string? carpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta) || !Path.IsPathFullyQualified(carpeta)) return false;
        string completa = Path.GetFullPath(carpeta);
        return !string.Equals(Path.TrimEndingDirectorySeparator(completa), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(completa) ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Borra cada carpeta con todo su contenido, insistiendo un poco si algo sigue en uso (un antivirus, el proceso
    /// anterior terminando de cerrar). Lo que aun así no se pueda borrar no frena al resto: se borra archivo por archivo y
    /// se devuelven las carpetas que quedaron con algo dentro.
    /// </summary>
    internal static IReadOnlyList<string> BorrarCarpetas(IEnumerable<string> carpetas, int intentos = 20, int esperaMs = 500)
    {
        var restos = new List<string>();
        foreach (string carpeta in carpetas)
        {
            if (!EsCarpetaBorrable(carpeta) || !Directory.Exists(carpeta)) continue;

            for (int intento = 1; intento <= intentos && Directory.Exists(carpeta); intento++)
            {
                try
                {
                    Directory.Delete(carpeta, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (intento < intentos) Thread.Sleep(esperaMs);
                }
            }

            if (!Directory.Exists(carpeta)) continue;

            BorrarLoQueSePueda(carpeta);
            if (Directory.Exists(carpeta)) restos.Add(carpeta);
        }
        return restos;
    }

    /// <summary>
    /// Recorre las subcarpetas pero NO entra en enlaces ni uniones (junctions): uno que apuntara fuera de la carpeta de
    /// datos haría borrar archivos ajenos.
    /// </summary>
    private static readonly EnumerationOptions SinSeguirEnlaces = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static void BorrarLoQueSePueda(string carpeta)
    {
        try
        {
            foreach (string archivo in Directory.EnumerateFiles(carpeta, "*", SinSeguirEnlaces))
            {
                try
                {
                    File.SetAttributes(archivo, FileAttributes.Normal);
                    File.Delete(archivo);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* en uso: se queda */ }
            }

            // De la más profunda a la raíz: solo se van las que quedaron vacías.
            var subcarpetas = new List<string>(Directory.EnumerateDirectories(carpeta, "*", SinSeguirEnlaces)) { carpeta };
            subcarpetas.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string subcarpeta in subcarpetas)
            {
                try { Directory.Delete(subcarpeta); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* aún tiene algo */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No se pudo recorrer: queda como resto.
        }
    }
}
