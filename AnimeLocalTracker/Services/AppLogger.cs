using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public record LogEntry(DateTime Timestamp, string Level, string Source, string Message, string? ExceptionDetails = null)
{
    /// <summary>Entrada DEBUG que no se escribió en su momento y se vuelca como contexto de un aviso o error.</summary>
    public bool EsContexto { get; init; }

    public override string ToString() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{Source}] {Message}" +
        (!string.IsNullOrEmpty(ExceptionDetails) ? Environment.NewLine + ExceptionDetails : string.Empty);
}

/// <summary>
/// Logger de la aplicación. Estructura en disco (carpeta Logs):
/// <list type="bullet">
/// <item><c>sesiones/AAAA-MM-DD_HH-mm-ss.log</c>: un archivo por cada vez que se abre la app (antes todo
/// iba a un único app.log: 118 sesiones de 3 días mezcladas).</item>
/// <item><c>errores.log</c>: solo avisos y errores de todas las sesiones, para ver de un vistazo qué falló.</item>
/// </list>
/// Las entradas DEBUG solo se escriben con el registro detallado activado; si no, se guardan en memoria
/// y, cuando llega un aviso o un error, se escriben las anteriores como contexto (lo que llevó al fallo).
/// Los mensajes repetidos (mismo texto salvo números) se resumen tras 5 en un minuto. Enqueue es O(1) y
/// nunca bloquea al llamador; un único consumidor escribe a disco en lotes.
/// </summary>
public static class AppLogger
{
    private static readonly string LogDirectory = Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_LOG_DIR") ?? AppDataPaths.LogsDir;
    private static readonly string SesionesDirectory = Path.Combine(LogDirectory, "sesiones");
    private static readonly string ErroresPath = Path.Combine(LogDirectory, "errores.log");
    private static readonly DateTime InicioSesion = DateTime.Now;

    /// <summary>Archivo de esta sesión.</summary>
    public static string RutaSesion { get; } = Path.Combine(SesionesDirectory, $"{InicioSesion:yyyy-MM-dd_HH-mm-ss}.log");

    /// <summary>Carpeta de los registros (para abrirla desde Configuración).</summary>
    public static string Carpeta => LogDirectory;

    private const int MaxInMemoryLogs = 500;
    private const long MaxBytesSesion = 10 * 1024 * 1024;
    private const long MaxBytesErrores = 2 * 1024 * 1024;
    private const int BatchFlushMs = 500;

    /// <summary>Sesiones que se conservan (y como mucho <see cref="MaxBytesSesiones"/> entre todas).</summary>
    internal const int MaxSesionesConservadas = 30;
    internal const long MaxBytesSesiones = 25 * 1024 * 1024;

    /// <summary>Entradas DEBUG no escritas que se vuelcan como contexto de un aviso o error.</summary>
    internal const int MaxContexto = 40;
    private static readonly TimeSpan AntiguedadMaxContexto = TimeSpan.FromMinutes(2);

    /// <summary>Repeticiones de un mismo mensaje por minuto que se escriben antes de resumirlas.</summary>
    internal const int MaxRepeticionesPorMinuto = 5;
    private static readonly TimeSpan VentanaRepeticiones = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentQueue<LogEntry> _recentLogs = new();
    private static readonly Channel<LogEntry> _cola = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(50_000)
    {
        SingleReader = true,
        SingleWriter = false,
        // Wait: TryWrite devuelve false si está llena (nunca bloquea: solo se usa TryWrite) y así se cuentan las perdidas.
        FullMode = BoundedChannelFullMode.Wait
    });
    private static readonly object _escritura = new();
    private static readonly object _estado = new();
    private static readonly Queue<LogEntry> _contexto = new();
    private static readonly Dictionary<(string Fuente, string Plantilla), Repeticiones> _repeticiones = new();
    private static long _descartadas;
    private static bool _cabeceraEscrita;

    private sealed class Repeticiones
    {
        public DateTime InicioVentana;
        public int Escritas;
        public int Suprimidas;
        public string Nivel = "";
    }

    /// <summary>
    /// Escribir también las entradas DEBUG (Configuración → "Registro detallado"). Apagado, el log es
    /// mucho más corto y los fallos siguen llegando con su contexto.
    /// </summary>
    public static bool RegistroDetallado { get; set; } = Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_LOG_DETALLADO") == "1";

    public static event Action<LogEntry>? LogEmitted;

    public static IReadOnlyCollection<LogEntry> RecentLogs => _recentLogs.ToArray();

    static AppLogger()
    {
        _ = Task.Run(ProcesarColaAsync);
        _ = Task.Run(LimpiarRegistrosAntiguos);

        // Al cerrar: escribir lo pendiente (incluidos los resúmenes de mensajes repetidos).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Debug(string source, string message) => Log("DEBUG", source, message);
    public static void Info(string source, string message) => Log("INFO", source, message);
    public static void Warn(string source, string message) => Log("WARN", source, message);
    public static void Error(string source, string message, Exception? ex = null)
    {
        // Sin internet es un estado esperado de la app (offline-first), no un error: una línea sin traza en vez de llenar el log.
        if (ex is SinConexionException)
        {
            Log("DEBUG", source, $"{message}: sin conexión.");
            return;
        }
        Log("ERROR", source, message, ex?.ToString());
    }

    /// <summary>
    /// Escribe YA todo lo pendiente, sin esperar al siguiente lote. Llamarlo antes de que el proceso pueda
    /// morir (error fatal): antes el error que tumbaba la app quedaba en memoria y nunca llegaba al disco.
    /// </summary>
    public static void Flush()
    {
        try
        {
            lock (_estado)
            {
                foreach (var ((fuente, plantilla), r) in _repeticiones)
                {
                    if (r.Suprimidas > 0) EncolarResumen(fuente, plantilla, r);
                }
                _repeticiones.Clear();
            }
            lock (_escritura)
            {
                EscribirPendientes();
            }
        }
        catch
        {
            // Cerrando la app: no hay nada más que hacer.
        }
    }

    private static void Log(string level, string source, string message, string? exceptionDetails = null)
    {
        // SEC-12: nunca volcar rutas completas del perfil del usuario en los logs.
        message = Sanitizar(message);
        exceptionDetails = Sanitizar(exceptionDetails);

        var entry = new LogEntry(DateTime.Now, level, source, message, exceptionDetails);

        _recentLogs.Enqueue(entry);
        while (_recentLogs.Count > MaxInMemoryLogs && _recentLogs.TryDequeue(out _)) { }

        try
        {
            LogEmitted?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Error en suscriptor de LogEmitted: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine(entry.ToString());

        lock (_estado)
        {
            if (level == "DEBUG" && !RegistroDetallado)
            {
                // Se guarda por si luego llega un aviso o error que la necesite como contexto.
                _contexto.Enqueue(entry with { EsContexto = true });
                while (_contexto.Count > MaxContexto) _contexto.Dequeue();
                return;
            }

            if (!PermitirPorRepeticion(entry)) return;

            if (level is "WARN" or "ERROR" && _contexto.Count > 0)
            {
                var limite = entry.Timestamp - AntiguedadMaxContexto;
                foreach (var previa in _contexto.Where(e => e.Timestamp >= limite)) Encolar(previa);
                _contexto.Clear();
            }

            Encolar(entry);
        }
    }

    private static void Encolar(LogEntry entry)
    {
        if (!_cola.Writer.TryWrite(entry)) Interlocked.Increment(ref _descartadas);
    }

    /// <summary>
    /// Deja pasar las primeras <see cref="MaxRepeticionesPorMinuto"/> entradas de un mismo mensaje (mismo
    /// texto salvo los números) por minuto; el resto se cuenta y se resume en una sola línea. Sin esto, un
    /// servicio reintentando sin conexión escribió 239 líneas casi iguales.
    /// </summary>
    private static bool PermitirPorRepeticion(LogEntry entry)
    {
        var clave = (entry.Source, Plantilla(entry.Message));
        if (!_repeticiones.TryGetValue(clave, out var r))
        {
            if (_repeticiones.Count >= MaxPlantillasVigiladas) PurgarRepeticiones();
            _repeticiones[clave] = new Repeticiones { InicioVentana = entry.Timestamp, Escritas = 1, Nivel = entry.Level };
            return true;
        }

        if (entry.Timestamp - r.InicioVentana >= VentanaRepeticiones)
        {
            if (r.Suprimidas > 0) EncolarResumen(clave.Source, clave.Item2, r);
            r.InicioVentana = entry.Timestamp;
            r.Escritas = 1;
            r.Suprimidas = 0;
            r.Nivel = entry.Level;
            return true;
        }

        if (r.Escritas < MaxRepeticionesPorMinuto)
        {
            r.Escritas++;
            return true;
        }

        r.Suprimidas++;
        return false;
    }

    private static void EncolarResumen(string fuente, string plantilla, Repeticiones r)
    {
        string muestra = plantilla.Length > 120 ? plantilla[..120] + "…" : plantilla;
        Encolar(new LogEntry(DateTime.Now, r.Nivel, fuente, $"(repetido {r.Suprimidas} veces más en el último minuto: \"{muestra}\")"));
    }

    /// <summary>Mensajes distintos que se vigilan a la vez para detectar repeticiones.</summary>
    private const int MaxPlantillasVigiladas = 2000;

    /// <summary>
    /// Libera la tabla de repeticiones de una sola pasada: los mensajes que no suprimieron nada solo son
    /// contadores y se olvidan; si aun así sigue llena, se resumen los suprimidos y se vacía. (Una versión
    /// anterior la recorría entera en CADA mensaje nuevo sin poder vaciarla: 1,4 ms por llamada.)
    /// </summary>
    private static void PurgarRepeticiones()
    {
        foreach (var clave in _repeticiones.Where(kv => kv.Value.Suprimidas == 0).Select(kv => kv.Key).ToList())
        {
            _repeticiones.Remove(clave);
        }
        if (_repeticiones.Count < MaxPlantillasVigiladas / 2) return;

        foreach (var ((fuente, plantilla), r) in _repeticiones) EncolarResumen(fuente, plantilla, r);
        _repeticiones.Clear();
    }

    /// <summary>El mensaje con cada número sustituido por '#': "Reintento 3 en 2 s" y "Reintento 4 en 8 s" son el mismo.</summary>
    internal static string Plantilla(string mensaje)
    {
        var sb = new StringBuilder(mensaje.Length);
        bool enNumero = false;
        foreach (char c in mensaje)
        {
            if (char.IsDigit(c))
            {
                if (!enNumero) sb.Append('#');
                enNumero = true;
            }
            else
            {
                sb.Append(c);
                enNumero = false;
            }
        }
        return sb.ToString();
    }

    private static async Task ProcesarColaAsync()
    {
        var lector = _cola.Reader;
        try
        {
            while (await lector.WaitToReadAsync().ConfigureAwait(false))
            {
                // Agrupar lo que llegue en el próximo medio segundo: menos escrituras a disco. Las entradas
                // siguen en la cola mientras tanto, así que Flush() (cierre, error fatal) también las ve.
                await Task.Delay(BatchFlushMs).ConfigureAwait(false);
                lock (_escritura)
                {
                    EscribirPendientes();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Consumidor de logs terminó inesperadamente: {ex.Message}");
        }
    }

    /// <summary>Vacía la cola y escribe (llamar con <see cref="_escritura"/> tomado).</summary>
    private static void EscribirPendientes()
    {
        var sesion = new StringBuilder();
        var errores = new StringBuilder();

        long descartadas = Interlocked.Exchange(ref _descartadas, 0);
        if (descartadas > 0)
        {
            sesion.Append(FormatoSesion(new LogEntry(DateTime.Now, "WARN", "AppLogger", $"{descartadas} entradas descartadas: llegaban más rápido de lo que se podían escribir.")));
        }

        while (_cola.Reader.TryRead(out var entry))
        {
            sesion.Append(FormatoSesion(entry));
            if (entry.Level is "WARN" or "ERROR" && !entry.EsContexto) errores.Append(FormatoErrores(entry));
        }
        if (sesion.Length == 0) return;

        try
        {
            Directory.CreateDirectory(SesionesDirectory);
            if (!_cabeceraEscrita)
            {
                sesion.Insert(0, Cabecera());
                _cabeceraEscrita = true;
            }
            AnadirConLimite(RutaSesion, sesion.ToString(), MaxBytesSesion);
            if (errores.Length > 0) AnadirConLimite(ErroresPath, errores.ToString(), MaxBytesErrores);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Error al escribir log en disco: {ex.Message}");
        }
    }

    private static string Cabecera() =>
        $"==== Sesión {InicioSesion:yyyy-MM-dd HH:mm:ss} · Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · " +
        $"registro detallado: {(RegistroDetallado ? "sí" : "no (DEBUG solo como contexto de avisos y errores)")} ====" + Environment.NewLine;

    /// <summary>"00:09:00.123 INFO  [App] mensaje" (la fecha ya está en el nombre del archivo). Las trazas van sangradas.</summary>
    internal static string FormatoSesion(LogEntry e)
    {
        var sb = new StringBuilder(e.Message.Length + 48);
        sb.Append(e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(e.Level.PadRight(5)).Append(e.EsContexto ? " · " : " ")
          .Append('[').Append(e.Source).Append("] ").Append(e.Message).AppendLine();
        AnadirTraza(sb, e.ExceptionDetails);
        return sb.ToString();
    }

    /// <summary>Como <see cref="FormatoSesion"/> pero con fecha: errores.log junta todas las sesiones.</summary>
    internal static string FormatoErrores(LogEntry e)
    {
        var sb = new StringBuilder(e.Message.Length + 56);
        sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(e.Level.PadRight(5)).Append(" [").Append(e.Source).Append("] ").Append(e.Message).AppendLine();
        AnadirTraza(sb, e.ExceptionDetails);
        return sb.ToString();
    }

    private static void AnadirTraza(StringBuilder sb, string? traza)
    {
        if (string.IsNullOrEmpty(traza)) return;
        foreach (var linea in traza.Split('\n'))
        {
            sb.Append("    ").Append(linea.TrimEnd('\r')).AppendLine();
        }
    }

    /// <summary>Añade al archivo; si supera su tope se aparta como ".1" (se conserva uno anterior).</summary>
    private static void AnadirConLimite(string ruta, string texto, long maxBytes)
    {
        try
        {
            var info = new FileInfo(ruta);
            if (info.Exists && info.Length >= maxBytes)
            {
                string anterior = Path.ChangeExtension(ruta, ".1.log");
                File.Delete(anterior);
                File.Move(ruta, anterior);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] No se pudo rotar {ruta}: {ex.Message}");
        }
        File.AppendAllText(ruta, texto);
    }

    /// <summary>
    /// Borra sesiones viejas (más de <see cref="MaxSesionesConservadas"/> o de <see cref="MaxBytesSesiones"/>
    /// entre todas) y aparta el app.log del sistema anterior.
    /// </summary>
    private static void LimpiarRegistrosAntiguos()
    {
        try
        {
            string antiguo = Path.Combine(LogDirectory, "app.log");
            if (File.Exists(antiguo))
            {
                Directory.CreateDirectory(SesionesDirectory);
                string destino = Path.Combine(SesionesDirectory, "0000-anteriores (app.log antiguo).log");
                if (!File.Exists(destino)) File.Move(antiguo, destino);
            }
            File.Delete(Path.Combine(LogDirectory, "app.log.1"));

            if (!Directory.Exists(SesionesDirectory)) return;
            foreach (var ruta in SesionesABorrar(new DirectoryInfo(SesionesDirectory).GetFiles("*.log")
                         .Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)), RutaSesion))
            {
                File.Delete(ruta);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] No se pudieron limpiar los registros antiguos: {ex.Message}");
        }
    }

    /// <summary>Lógica pura de la limpieza: las más antiguas que sobran por número o por tamaño total (nunca la actual).</summary>
    internal static List<string> SesionesABorrar(IEnumerable<(string Ruta, long Bytes, DateTime ModificadoUtc)> archivos, string sesionActual)
    {
        var borrar = new List<string>();
        long total = 0;
        int conservadas = 0;
        foreach (var a in archivos.OrderByDescending(a => a.ModificadoUtc))
        {
            if (string.Equals(a.Ruta, sesionActual, StringComparison.OrdinalIgnoreCase)) { total += a.Bytes; conservadas++; continue; }
            if (conservadas >= MaxSesionesConservadas || total + a.Bytes > MaxBytesSesiones)
            {
                borrar.Add(a.Ruta);
                continue;
            }
            total += a.Bytes;
            conservadas++;
        }
        return borrar;
    }

    private static readonly Lazy<(string LocalApp, string Perfil)> _rutasPrivadas = new(() =>
    {
        try
        {
            return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch
        {
            return ("", "");
        }
    });

    /// <summary>
    /// SEC-12: sustituye las rutas del perfil de usuario por marcadores cortos antes
    /// de escribir cualquier entrada de log (higiene de privacidad en disco). Las rutas se leen una sola
    /// vez: consultarlas a Windows en cada mensaje era la mayor parte del coste de registrar.
    /// </summary>
    internal static string Sanitizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto ?? string.Empty;

        var (localApp, perfil) = _rutasPrivadas.Value;
        string resultado = texto;
        // Orden importante: %LocalAppData% vive DENTRO del perfil → reemplazarlo primero.
        if (localApp.Length > 0) resultado = resultado.Replace(localApp, "<datos>", StringComparison.OrdinalIgnoreCase);
        if (perfil.Length > 0) resultado = resultado.Replace(perfil, "<perfil>", StringComparison.OrdinalIgnoreCase);
        return resultado;
    }
}
