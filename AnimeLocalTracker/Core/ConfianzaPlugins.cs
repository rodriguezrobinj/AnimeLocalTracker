using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// SEC-01: política de confianza de plugins. Un plugin (.dll de C# o .py de Python) ejecuta código de terceros con los
/// permisos de la app, así que solo corre si (1) el usuario activó los plugins Y (2) marcó ese archivo concreto como
/// confiable, quedando fijada su huella SHA-256. Un archivo que cambie después (por ejemplo, reemplazado por otro
/// programa) ya no coincide con la huella y deja de ejecutarse hasta que el usuario lo confirme otra vez.
/// Lógica pura: no toca la red ni la UI, para poder probarla con archivos temporales.
/// </summary>
public static class ConfianzaPlugins
{
    private static readonly string[] ExtensionesPermitidas = { ".dll", ".py" };

    /// <summary>Separadores de ruta y ":" (unidad/flujo alterno NTFS): un nombre de plugin no puede llevarlos.</summary>
    private static readonly SearchValues<char> CaracteresDeRuta = SearchValues.Create("/\\:");

    private static readonly SearchValues<char> CaracteresInvalidosDeNombre = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>Huella SHA-256 del archivo en hex mayúsculas. Lanza si no se puede leer.</summary>
    public static string CalcularSha256(string rutaArchivo)
    {
        using var flujo = new FileStream(rutaArchivo, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(flujo));
    }

    /// <summary>
    /// True si es solo un nombre de archivo (sin carpetas ni "..") y con extensión de plugin. Evita que un nombre como
    /// "..\..\otra.py" saque la ejecución de la carpeta de plugins.
    /// </summary>
    public static bool EsNombreDePluginValido(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return false;
        if (nombre.AsSpan().ContainsAny(CaracteresDeRuta)) return false;
        if (nombre.AsSpan().ContainsAny(CaracteresInvalidosDeNombre)) return false;
        if (nombre is "." or "..") return false;

        string extension = Path.GetExtension(nombre);
        foreach (var permitida in ExtensionesPermitidas)
        {
            if (string.Equals(extension, permitida, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Estado de un archivo según la huella que el usuario aprobó (o no).</summary>
    public static EstadoConfianzaPlugin Evaluar(AppSettings configuracion, string nombreArchivo, string sha256Actual)
    {
        // Búsqueda sin distinguir mayúsculas (Windows): System.Text.Json crea el diccionario con el comparador por
        // defecto al leer settings.json, así que no se puede asumir el comparador de la propiedad.
        string? aprobada = null;
        if (configuracion.PluginsConfiables != null)
        {
            foreach (var par in configuracion.PluginsConfiables)
            {
                if (string.Equals(par.Key, nombreArchivo, StringComparison.OrdinalIgnoreCase)) { aprobada = par.Value; break; }
            }
        }

        if (string.IsNullOrWhiteSpace(aprobada)) return EstadoConfianzaPlugin.SinConfiar;

        return string.Equals(aprobada, sha256Actual, StringComparison.OrdinalIgnoreCase)
            ? EstadoConfianzaPlugin.Confiable
            : EstadoConfianzaPlugin.Modificado;
    }

    /// <summary>
    /// True solo si los plugins están activados y el archivo está aprobado con la huella EXACTA que tiene ahora. Si no
    /// se puede leer el archivo, no se ejecuta.
    /// </summary>
    public static bool PuedeEjecutarse(AppSettings configuracion, string rutaArchivo)
    {
        if (!configuracion.PluginsHabilitados) return false;

        try
        {
            string nombre = Path.GetFileName(rutaArchivo);
            return Evaluar(configuracion, nombre, CalcularSha256(rutaArchivo)) == EstadoConfianzaPlugin.Confiable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
