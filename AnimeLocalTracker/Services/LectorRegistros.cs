using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeLocalTracker.Services;

/// <summary>Una entrada leída de un archivo de registro.</summary>
/// <param name="Fecha">Fecha y hora de la entrada (null si el archivo no la da).</param>
/// <param name="Detalle">Traza de la excepción u otras líneas que siguen a la entrada.</param>
/// <param name="EsContexto">DEBUG escrita como contexto de un aviso/error (marca "·").</param>
/// <param name="EsCabecera">Línea "==== Sesión …" del principio de cada archivo de sesión.</param>
public sealed record EntradaRegistro(DateTime? Fecha, string Nivel, string Fuente, string Mensaje, string? Detalle = null, bool EsContexto = false, bool EsCabecera = false);

public enum TipoArchivoRegistro { SesionActual, Sesion, Errores, Antiguo }

/// <summary>Un archivo de la carpeta de registros.</summary>
public sealed record ArchivoRegistro(string Ruta, TipoArchivoRegistro Tipo, DateTime? Inicio, long Bytes);

/// <summary>
/// Lectura de los archivos que escribe <see cref="AppLogger"/> (lógica pura, sin interfaz): sesiones
/// ("00:09:00.123 INFO  [Fuente] …"), errores.log (con fecha) y el app.log del sistema anterior
/// ("[2026-09-29 21:08:57] [INFO] [Fuente] …"). Las líneas que no empiezan una entrada (trazas sangradas)
/// se añaden como detalle de la anterior.
/// </summary>
public static partial class LectorRegistros
{
    public static readonly string[] Niveles = { "DEBUG", "INFO", "WARN", "ERROR" };

    /// <summary>Los archivos de la carpeta, del más reciente al más antiguo: la sesión actual primero, luego errores.log.</summary>
    public static List<ArchivoRegistro> ListarArchivos(string carpeta, string rutaSesionActual)
    {
        var lista = new List<ArchivoRegistro>();
        string sesiones = Path.Combine(carpeta, "sesiones");

        if (Directory.Exists(sesiones))
        {
            foreach (var f in new DirectoryInfo(sesiones).GetFiles("*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                if (FechaDeSesion(f.FullName) is DateTime inicio)
                {
                    var tipo = string.Equals(f.FullName, rutaSesionActual, StringComparison.OrdinalIgnoreCase) ? TipoArchivoRegistro.SesionActual : TipoArchivoRegistro.Sesion;
                    lista.Add(new ArchivoRegistro(f.FullName, tipo, inicio, f.Length));
                }
                else
                {
                    lista.Add(new ArchivoRegistro(f.FullName, TipoArchivoRegistro.Antiguo, null, f.Length));
                }
            }
        }

        // La sesión actual puede no tener archivo todavía (nada escrito aún): se ofrece igual.
        if (!lista.Any(a => a.Tipo == TipoArchivoRegistro.SesionActual))
        {
            lista.Insert(0, new ArchivoRegistro(rutaSesionActual, TipoArchivoRegistro.SesionActual, FechaDeSesion(rutaSesionActual), 0));
        }

        string errores = Path.Combine(carpeta, "errores.log");
        if (File.Exists(errores))
        {
            lista.Insert(1, new ArchivoRegistro(errores, TipoArchivoRegistro.Errores, null, new FileInfo(errores).Length));
        }

        // El archivo antiguo, al final.
        return lista.OrderBy(a => a.Tipo switch { TipoArchivoRegistro.SesionActual => 0, TipoArchivoRegistro.Errores => 1, TipoArchivoRegistro.Sesion => 2, _ => 3 })
                    .ThenByDescending(a => a.Inicio)
                    .ToList();
    }

    /// <summary>Fecha de inicio de una sesión a partir del nombre del archivo ("2026-09-30_00-40-26.log").</summary>
    public static DateTime? FechaDeSesion(string ruta)
        => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(ruta), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    /// <summary>
    /// Convierte el texto de un archivo en entradas (en el orden del archivo). <paramref name="fechaBase"/> da
    /// el día a las líneas de sesión, que solo llevan la hora.
    /// </summary>
    public static List<EntradaRegistro> Analizar(string texto, DateTime? fechaBase)
    {
        var entradas = new List<EntradaRegistro>();
        if (string.IsNullOrEmpty(texto)) return entradas;

        EntradaRegistro? actual = null;
        StringBuilder? detalle = null;

        void Cerrar()
        {
            if (actual == null) return;
            entradas.Add(detalle is { Length: > 0 } ? actual with { Detalle = detalle.ToString().TrimEnd() } : actual);
            actual = null;
            detalle = null;
        }

        foreach (var lineaCruda in texto.Split('\n'))
        {
            string linea = lineaCruda.TrimEnd('\r');
            var nueva = AnalizarLinea(linea, fechaBase);
            if (nueva != null)
            {
                Cerrar();
                actual = nueva;
                continue;
            }
            if (actual == null || linea.Length == 0) continue;

            detalle ??= new StringBuilder();
            detalle.Append(linea.StartsWith("    ", StringComparison.Ordinal) ? linea[4..] : linea).Append('\n');
        }
        Cerrar();
        return entradas;
    }

    private static EntradaRegistro? AnalizarLinea(string linea, DateTime? fechaBase)
    {
        if (linea.Length < 10) return null;

        var m = LineaSesionRegex().Match(linea);
        if (m.Success)
        {
            DateTime? fecha = null;
            if (TimeSpan.TryParseExact(m.Groups["hora"].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var hora))
            {
                fecha = (fechaBase?.Date ?? DateTime.MinValue.Date) + hora;
            }
            return new EntradaRegistro(fecha, m.Groups["nivel"].Value, m.Groups["fuente"].Value, m.Groups["msg"].Value, EsContexto: m.Groups["ctx"].Success);
        }

        m = LineaErroresRegex().Match(linea);
        if (m.Success)
        {
            return new EntradaRegistro(ParsearFecha(m.Groups["fecha"].Value, "yyyy-MM-dd HH:mm:ss.fff"), m.Groups["nivel"].Value, m.Groups["fuente"].Value, m.Groups["msg"].Value);
        }

        m = LineaAntiguaRegex().Match(linea);
        if (m.Success)
        {
            return new EntradaRegistro(ParsearFecha(m.Groups["fecha"].Value, "yyyy-MM-dd HH:mm:ss"), m.Groups["nivel"].Value, m.Groups["fuente"].Value, m.Groups["msg"].Value);
        }

        if (linea.StartsWith("==== ", StringComparison.Ordinal))
        {
            return new EntradaRegistro(fechaBase, "INFO", "", linea.Trim('=', ' '), EsCabecera: true);
        }

        return null;
    }

    private static DateTime? ParsearFecha(string texto, string formato)
        => DateTime.TryParseExact(texto, formato, CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;

    /// <summary>
    /// Lee lo que se añadió a un archivo desde <paramref name="desplazamiento"/> (bytes), solo hasta la última
    /// línea completa. Devuelve el texto y dónde seguir la próxima vez. Comparte el archivo con el que escribe.
    /// </summary>
    public static (string Texto, long Siguiente) LeerDesde(string ruta, long desplazamiento)
    {
        if (!File.Exists(ruta)) return ("", 0);
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // El archivo es más corto que lo ya leído: se rotó o se vació; se empieza de nuevo.
        if (fs.Length < desplazamiento) desplazamiento = 0;
        if (fs.Length == desplazamiento) return ("", desplazamiento);

        fs.Seek(desplazamiento, SeekOrigin.Begin);
        var bytes = new byte[fs.Length - desplazamiento];
        int leidos = 0;
        while (leidos < bytes.Length)
        {
            int n = fs.Read(bytes, leidos, bytes.Length - leidos);
            if (n == 0) break;
            leidos += n;
        }

        int ultimoSalto = Array.LastIndexOf(bytes, (byte)'\n', leidos - 1);
        if (ultimoSalto < 0) return ("", desplazamiento);
        return (Encoding.UTF8.GetString(bytes, 0, ultimoSalto + 1), desplazamiento + ultimoSalto + 1);
    }

    // "00:09:00.123 INFO  [Fuente] mensaje" / "00:09:00.123 DEBUG · [Fuente] mensaje" (contexto)
    [GeneratedRegex(@"^(?<hora>\d{2}:\d{2}:\d{2}\.\d{3}) (?<nivel>DEBUG|INFO|WARN|ERROR)\s+(?<ctx>·\s+)?\[(?<fuente>[^\]]*)\] ?(?<msg>.*)$")]
    private static partial Regex LineaSesionRegex();

    // "2026-09-30 00:09:00.123 ERROR [Fuente] mensaje"
    [GeneratedRegex(@"^(?<fecha>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<nivel>DEBUG|INFO|WARN|ERROR)\s+\[(?<fuente>[^\]]*)\] ?(?<msg>.*)$")]
    private static partial Regex LineaErroresRegex();

    // "[2026-09-29 21:08:57] [INFO] [Fuente] mensaje" (app.log del sistema anterior)
    [GeneratedRegex(@"^\[(?<fecha>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] \[(?<nivel>DEBUG|INFO|WARN|ERROR)\] \[(?<fuente>[^\]]*)\] ?(?<msg>.*)$")]
    private static partial Regex LineaAntiguaRegex();
}
