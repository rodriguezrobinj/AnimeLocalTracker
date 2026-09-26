using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Defensas en profundidad para datos que llegan de fuera (archivos dentro de un .torrent, respuestas HTTP de
/// terceros): rutas que no pueden escapar de su carpeta y lecturas con tope de tamaño.
/// </summary>
public static class EntradaSegura
{
    /// <summary>
    /// True si <paramref name="ruta"/>, ya normalizada (resuelve <c>..</c>, mayúsculas y separadores), queda DENTRO de
    /// <paramref name="carpetaBase"/> (o es la propia carpeta). Se compara por componentes de ruta: "C:\Base2" no
    /// cuenta como dentro de "C:\Base".
    /// </summary>
    public static bool EstaDentroDe(string carpetaBase, string ruta)
    {
        if (string.IsNullOrWhiteSpace(carpetaBase) || string.IsNullOrWhiteSpace(ruta)) return false;

        string baseNorm, rutaNorm;
        try
        {
            baseNorm = Path.GetFullPath(carpetaBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            rutaNorm = Path.GetFullPath(ruta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(baseNorm, rutaNorm, StringComparison.OrdinalIgnoreCase)) return true;
        return rutaNorm.StartsWith(baseNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lee el cuerpo de una respuesta HTTP sin permitir que supere <paramref name="maximoBytes"/>: rechaza pronto si
    /// <c>Content-Length</c> ya lo excede y corta la lectura si el servidor miente o no lo declara.
    /// </summary>
    /// <exception cref="InvalidDataException">La respuesta supera el tope.</exception>
    public static async Task<byte[]> LeerAcotadoAsync(HttpContent contenido, long maximoBytes, CancellationToken ct = default)
    {
        if (contenido.Headers.ContentLength is long declarado && declarado > maximoBytes)
            throw new InvalidDataException($"La respuesta declara {declarado} bytes; el máximo permitido es {maximoBytes}.");

        await using var origen = await contenido.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destino = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await origen.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (destino.Length + leidos > maximoBytes)
                throw new InvalidDataException($"La respuesta supera el máximo permitido de {maximoBytes} bytes.");
            destino.Write(buffer, 0, leidos);
        }
        return destino.ToArray();
    }
}
