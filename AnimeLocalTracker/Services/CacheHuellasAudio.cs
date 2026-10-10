using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Huellas de audio de los temas (opening/ending) guardadas en disco: la segunda vez que hace falta un tema no se decodifica.
/// Un archivo por tema, f32 en crudo (fotogramas × 8). Si falla el disco solo se pierde velocidad: nada de aquí lanza.
/// </summary>
internal sealed class CacheHuellasAudio(string carpeta)
{
    /// <summary>Cambiarlo invalida todo lo guardado (forma parte de la clave).</summary>
    private const int FormatoHuella = 2;
    private const string Extension = ".huella";
    private const int BytesPorFotograma = NativeMethods.ColumnasHuella * sizeof(float);
    private const int BloqueDeClave = 65536;

    /// <summary>Tope de archivos (~30 KB cada uno): al pasarlo se borran los menos usados.</summary>
    internal const int MaximoArchivos = 800;
    internal const int ArchivosTrasPoda = 600;

    /// <summary>
    /// Por contenido (tamaño + primeros y últimos 64 KB), no por nombre: renombrar el mp3 no obliga a recalcular.
    /// Lanza <see cref="IOException"/> si el archivo no se puede leer.
    /// </summary>
    internal static string Clave(string rutaTema)
    {
        using var archivo = File.OpenRead(rutaTema);
        long tamano = archivo.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.ASCII.GetBytes($"{FormatoHuella}|{tamano}|"));

        var bloque = new byte[BloqueDeClave];
        sha.AppendData(bloque, 0, archivo.ReadAtLeast(bloque, BloqueDeClave, throwOnEndOfStream: false));
        if (tamano > 2 * BloqueDeClave)
        {
            archivo.Seek(-BloqueDeClave, SeekOrigin.End);
            sha.AppendData(bloque, 0, archivo.ReadAtLeast(bloque, BloqueDeClave, throwOnEndOfStream: false));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Null si no está guardada o el archivo está dañado (vacío o cortado a medias).</summary>
    public float[]? Leer(string clave)
    {
        try
        {
            string archivo = Ruta(clave);
            if (!File.Exists(archivo)) return null;

            byte[] bytes = File.ReadAllBytes(archivo);
            if (bytes.Length == 0 || bytes.Length % BytesPorFotograma != 0) return null;

            try { File.SetLastWriteTimeUtc(archivo, DateTime.UtcNow); } catch (IOException) { /* solo es el orden para la poda */ }
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Guardar(string clave, float[] huella)
    {
        try
        {
            Directory.CreateDirectory(carpeta);
            string destino = Ruta(clave);
            // Nombre propio por escritura: el análisis del episodio y el pre-análisis del siguiente pueden guardar el mismo tema a la vez.
            string temporal = $"{destino}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporal, MemoryMarshal.AsBytes(huella.AsSpan()).ToArray());
                File.Move(temporal, destino, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporal); } catch (IOException) { /* best-effort */ }
            }

            BorrarFormatoAnterior();
            Podar();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sin caché solo se pierde velocidad
        }
    }

    private string Ruta(string clave) => Path.Combine(carpeta, clave + Extension);

    /// <summary>Las huellas que guardaba el plugin Python (.npy) ya no las lee nadie.</summary>
    private void BorrarFormatoAnterior()
    {
        foreach (string viejo in Directory.EnumerateFiles(carpeta, "*.npy"))
        {
            try { File.Delete(viejo); } catch (IOException) { /* se intentará en el siguiente guardado */ }
        }
    }

    private void Podar()
    {
        var archivos = new DirectoryInfo(carpeta).EnumerateFiles("*" + Extension).ToList();
        if (archivos.Count <= MaximoArchivos) return;

        foreach (var viejo in archivos.OrderBy(a => a.LastWriteTimeUtc).Take(archivos.Count - ArchivosTrasPoda))
        {
            try { viejo.Delete(); } catch (IOException) { /* best-effort */ }
        }
    }
}
