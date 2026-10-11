using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Native;

namespace AnimeLocalTracker.Services.Python
{
    public class PythonBridgeService : IPythonBridgeService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly JsonSerializerOptions SnakeCaseOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private string? _cachedExecutablePath;
        private string? _cachedScriptPath;
        private bool? _isAvailable;

        public async Task<bool> IsAvailableAsync()
        {
            if (_isAvailable.HasValue) return _isAvailable.Value;

            ResolveExecutable();
            if (!string.IsNullOrEmpty(_cachedExecutablePath) || !string.IsNullOrEmpty(_cachedScriptPath))
            {
                var ping = await ExecuteCommandAsync<object, PingResult>("ping", new { });
                _isAvailable = ping != null && ping.Success;
            }
            else
            {
                _isAvailable = false;
            }

            return _isAvailable.Value;
        }

        public async Task<TResponse?> ExecuteCommandAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct = default)
        {
            ResolveExecutable();

            if (string.IsNullOrEmpty(_cachedExecutablePath) && string.IsNullOrEmpty(_cachedScriptPath))
            {
                AppLogger.Warn("PythonBridge", "No se encontró el ejecutable ni el script de Python.");
                return default;
            }

            // 1. Intentar con el DAEMON persistente (evita el coste de arranque de Python/imports)
            var viaDaemon = await ExecuteViaDaemonAsync<TRequest, TResponse>(command, payload, ct);
            if (viaDaemon != null) return viaDaemon;

            // 2. Fallback: spawn de proceso one-shot (como antes)
            return await EjecutarOneShotAsync<TRequest, TResponse>(command, payload, avance: null, ct);
        }

        /// <summary>
        /// Ejecuta un comando en un proceso one-shot dedicado, SIN pasar por el daemon
        /// persistente: dentro del daemon un comando en marcha no se puede parar a la fuerza.
        /// Si se cancela, el proceso se mata con todo su árbol (yt-dlp/ffmpeg no
        /// mueren con el Dispose del objeto Process). Para comandos largos como
        /// download-stream (HLS).
        /// </summary>
        public async Task<TResponse?> ExecuteCommandOneShotAsync<TRequest, TResponse>(string command, TRequest payload,
            IProgress<(double Progreso, double Velocidad)>? avance = null, CancellationToken ct = default)
        {
            ResolveExecutable();
            return await EjecutarOneShotAsync<TRequest, TResponse>(command, payload, avance, ct);
        }

        private async Task<TResponse?> EjecutarOneShotAsync<TRequest, TResponse>(string command, TRequest payload,
            IProgress<(double Progreso, double Velocidad)>? avance, CancellationToken ct)
        {
            Process? proceso = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = Encoding.UTF8,
                    // El daemon llama a "ffmpeg"/"ffprobe" por su nombre, y Windows mira la carpeta actual ANTES que el
                    // PATH: con la carpeta desde la que se abrió la app, un ffmpeg.exe dejado ahí se ejecutaría. La de
                    // la instalación es de fiar.
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                psi.Environment["PATH"] = ComposePath();

                // Prioridad 1: Ejecutable nativo compilado Zero-Setup
                if (!string.IsNullOrEmpty(_cachedExecutablePath))
                {
                    psi.FileName = _cachedExecutablePath;
                    psi.Arguments = $"--command \"{command}\"";
                }
                // Prioridad 2: Script Python en entorno de desarrollo
                else
                {
                    psi.FileName = GetPythonCommand();
                    psi.Arguments = $"\"{_cachedScriptPath}\" --command \"{command}\"";
                }

                proceso = new Process { StartInfo = psi };
                proceso.Start();
                JobObjectHelper.AddProcess(proceso);

                // Enviar payload JSON por stdin
                string jsonInput = JsonSerializer.Serialize(payload, JsonOptions);
                await proceso.StandardInput.WriteLineAsync(jsonInput);
                await proceso.StandardInput.FlushAsync(ct);
                proceso.StandardInput.Close();

                // Leer respuesta JSON por stdout y los avisos por stderr A LA VEZ: leyendo uno detrás de otro,
                // si Python escribía más de lo que cabe en el búfer de stderr ambos procesos se quedaban
                // esperándose (bloqueo) hasta que vencía el tiempo del comando.
                var tareaError = proceso.StandardError.ReadToEndAsync(ct);
                // Línea a línea: un comando largo va escribiendo su avance antes del resultado, y hay que darlo según llega.
                var salida = new StringBuilder();
                while (await proceso.StandardOutput.ReadLineAsync(ct) is { } linea)
                {
                    if (avance != null && AvanceDe(linea) is { } paso) avance.Report(paso);
                    else salida.AppendLine(linea);
                }
                string output = salida.ToString();
                string error = await tareaError;

                await proceso.WaitForExitAsync(ct);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    AppLogger.Debug("PythonBridge", $"Stderr de '{command}': {error}");
                }

                return InterpretarSalida<TResponse>(output);
            }
            catch (OperationCanceledException)
            {
                // SEC-16/DATA-01: cancelación → matar el árbol completo; el proceso
                // Python (yt-dlp/ffmpeg) no muere con el Dispose del objeto Process
                // y quedaría huérfano descargando/escribiendo en disco.
                try { proceso?.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("PythonBridge", $"Error ejecutando comando Python '{command}': {ex.Message}", ex);
                return default;
            }
            finally
            {
                proceso?.Dispose();
            }
        }

        /// <summary>
        /// El avance que trae una línea de la salida de un comando ({"progress": 37.5, "speed": 1048576}: porcentaje y bytes
        /// por segundo), o null si la línea es otra cosa (el resultado, un aviso suelto).
        /// </summary>
        internal static (double Progreso, double Velocidad)? AvanceDe(string linea)
        {
            if (!linea.StartsWith("{\"progress\"", StringComparison.Ordinal)) return null;
            try
            {
                using var doc = JsonDocument.Parse(linea);
                if (!doc.RootElement.TryGetProperty("progress", out var progreso) || !progreso.TryGetDouble(out double porcentaje)) return null;
                double velocidad = doc.RootElement.TryGetProperty("speed", out var v) && v.TryGetDouble(out double bps) ? bps : 0;
                return (Math.Clamp(porcentaje, 0, 100), Math.Max(velocidad, 0));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resultado de un comando de un solo uso: la ÚLTIMA línea JSON de la salida. Un comando largo puede escribir antes otras líneas
        /// (el progreso de yt-dlp en una descarga HLS son ~1800): interpretar toda la salida como un único JSON fallaba y la app lo daba
        /// por "respuesta vacía del daemon". Null si no hay ninguna línea JSON utilizable.
        /// </summary>
        internal static TResponse? InterpretarSalida<TResponse>(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return default;

            var ultimaLineaJson = output.Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith('{') || l.StartsWith('['));
            foreach (var candidato in new[] { output.Trim(), ultimaLineaJson }.Where(c => !string.IsNullOrEmpty(c)).Distinct())
            {
                try
                {
                    var res = JsonSerializer.Deserialize<TResponse>(candidato!, SnakeCaseOptions) ?? JsonSerializer.Deserialize<TResponse>(candidato!, JsonOptions);
                    if (res != null) return res;
                }
                catch (JsonException)
                {
                    // esa lectura no era el JSON: se prueba con la siguiente
                }
            }
            return default;
        }

        // ────────────────────────────────────────────────────────────────
        //  DAEMON persistente (JSON-lines por stdin/stdout)
        // ────────────────────────────────────────────────────────────────
        private const int VersionProtocolo = 2;
        private static readonly object DaemonLock = new();
        private static readonly SemaphoreSlim ArranqueDaemon = new(1, 1);
        private static Process? _daemonProcess;
        private static CanalDaemon? _canal;

        /// <summary>El proceso del daemon en marcha, si lo hay (para las pruebas: cancelar una petición no debe reiniciarlo).</summary>
        internal static int? PidDelDaemon => _daemonProcess?.Id;

        /// <summary>
        /// Ejecuta un comando a través del proceso daemon persistente. Devuelve default
        /// si el daemon no está disponible (cae al path one-shot).
        /// </summary>
        private async Task<TResponse?> ExecuteViaDaemonAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct)
        {
            CanalDaemon? canal;
            try
            {
                // Daemon indisponible (sin ejecutable compilado, arranque fallido o saludo
                // con timeout): devolver default para caer al modo one-shot (nunca lanzar).
                canal = await EnsureDaemonStartedAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Debug("PythonBridge", $"Daemon no disponible ({ex.Message}); se usará el modo one-shot.");
                return default;
            }
            if (canal == null) return default;

            string? output;
            try
            {
                // Si se cancela, el canal avisa al daemon y la excepción sigue hacia quien llamó: el daemon no se reinicia y las
                // demás peticiones en curso no se enteran.
                output = await canal.PedirAsync(command, JsonSerializer.Serialize(payload, JsonOptions), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Debug("PythonBridge", $"Comando '{command}' interrumpido ({ex.GetType().Name}); se reinicia el daemon.");
                CleanupDaemon(canal);
                return default; // cae al one-shot
            }
            if (output == null)
            {
                CleanupDaemon(canal); // cerró la salida: murió
                return default;
            }

            var res = JsonSerializer.Deserialize<TResponse>(output, SnakeCaseOptions);
            return res ?? JsonSerializer.Deserialize<TResponse>(output, JsonOptions);
        }

        /// <summary>
        /// Arranca el proceso daemon de forma 100% asíncrona si no está vivo (una única instancia compartida).
        /// Devuelve false (sin lanzar) si el daemon no puede usarse, para que los llamadores
        /// degraden al modo one-shot. Hereda el PATH completo (usuario + sistema + FFmpeg embebido
        /// de la app) para que el daemon encuentre herramientas como ffmpeg instaladas por winget
        /// en WinGet\Links o los binarios ffmpeg.exe/ffprobe.exe distribuidos en la carpeta FFmpeg/ de la app.
        /// </summary>
        /// <summary>
        /// INT-04: en vez de descartar el daemon PARA SIEMPRE tras un handshake fallido, se
        /// reintenta después de una pausa (backoff de 10 min). Un onefile lento que no llega
        /// a saludar a tiempo puede lograrlo en el siguiente intento.
        /// </summary>
        private static DateTime _daemonProximoIntento = DateTime.MinValue;

        private async Task<CanalDaemon?> EnsureDaemonStartedAsync(CancellationToken ct)
        {
            if (DaemonEnMarcha() is { } enMarcha) return enMarcha;

            // De uno en uno: varias peticiones a la vez no deben arrancar cada una su daemon.
            await ArranqueDaemon.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return DaemonEnMarcha() ?? await ArrancarDaemonAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArranqueDaemon.Release();
            }
        }

        private static CanalDaemon? DaemonEnMarcha()
        {
            lock (DaemonLock)
            {
                return _daemonProcess is { HasExited: false } ? _canal : null;
            }
        }

        private async Task<CanalDaemon?> ArrancarDaemonAsync(CancellationToken ct)
        {
            if (DateTime.UtcNow < _daemonProximoIntento) return null;

            // El daemon requiere el ejecutable compilado (PyInstaller). Sin él (p.ej. CI sin el
            // binario embebido) degradar al modo one-shot en lugar de lanzar una excepción.
            if (string.IsNullOrEmpty(_cachedExecutablePath))
            {
                AppLogger.Debug("PythonBridge", "Daemon no disponible (sin ejecutable compilado); se usará el modo one-shot.");
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _cachedExecutablePath!,
                    Arguments = "--daemon",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    // ¡Sin BOM! Encoding.UTF8 (estático) sí lo emite, y el primer
                    // comando llegaba como \ufeff{...} → "JSON inválido".
                    StandardInputEncoding = new UTF8Encoding(false),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                psi.Environment["PATH"] = ComposePath();

                var proc = new Process { StartInfo = psi };
                // stderr del daemon (avisos de numpy/librosa/yt-dlp, trazas de error): antes se redirigía pero
                // nadie lo leía. Además de perderse, cuando se llenaba el búfer el daemon se quedaba colgado
                // al intentar escribir. Ahora se vacía continuamente hacia el registro.
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) AppLogger.Warn("PythonDaemon", e.Data);
                };
                proc.Start();
                proc.BeginErrorReadLine();
                JobObjectHelper.AddProcess(proc);
                _daemonProcess = proc;

                // Consumir el saludo de forma no bloqueante antes del primer comando.
                // INT-04: 20 s de margen (la extracción onefile + imports puede superar los 8 s).
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    string? greetingLine = await proc.StandardOutput.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(greetingLine))
                    {
                        throw new InvalidOperationException("Daemon cerró el stream de salida sin saludar.");
                    }

                    // INT-02: Validar la versión del protocolo para evitar desajustes ocultos.
                    using var doc = JsonDocument.Parse(greetingLine);
                    if (doc.RootElement.TryGetProperty("protocolVersion", out var prop))
                    {
                        if (prop.GetInt32() != VersionProtocolo)
                        {
                            throw new InvalidOperationException($"El daemon devolvió protocolVersion={prop.GetInt32()}, pero se esperaba {VersionProtocolo}.");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("El saludo del daemon no incluyó protocolVersion.");
                    }

                    _daemonProximoIntento = DateTime.MinValue;
                    var canal = new CanalDaemon(proc.StandardOutput, proc.StandardInput);
                    lock (DaemonLock) _canal = canal;
                    return canal;
                }
                catch (Exception initEx)
                {
                    // El daemon no saludó a tiempo, crasheó o devolvió versión errónea:
                    // backoff de 10 min (INT-04) y one-shot de respaldo mientras tanto.
                    AppLogger.Warn("PythonBridge", $"Fallo en handshake del daemon: {initEx.Message}. Se usará modo one-shot y se reintentará en unos minutos.");
                    _daemonProximoIntento = DateTime.UtcNow.AddMinutes(10);
                    CleanupDaemon();
                    return null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PythonBridge", $"No se pudo iniciar el daemon Python: {ex.Message}");
                _daemonProximoIntento = DateTime.UtcNow.AddMinutes(10);
                CleanupDaemon();
                return null;
            }
        }

        /// <param name="soloSiEsEste">
        /// El canal por el que falló quien llama: si ya hay otro daemon en su lugar (otra petición lo reinició antes), no se toca.
        /// </param>
        private static void CleanupDaemon(CanalDaemon? soloSiEsEste = null)
        {
            lock (DaemonLock)
            {
                if (soloSiEsEste != null && !ReferenceEquals(soloSiEsEste, _canal)) return;
                try
                {
                    _daemonProcess?.Kill(entireProcessTree: true);
                }
                catch { }
                _daemonProcess = null;
                _canal = null;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            CleanupDaemon();
        }

        private void ResolveExecutable()
        {
            if (!string.IsNullOrEmpty(_cachedExecutablePath) || !string.IsNullOrEmpty(_cachedScriptPath))
                return;

            // En un build publicado solo vale el ejecutable de la propia instalación (ver LocalizadorHerramientasPython).
            (_cachedExecutablePath, _cachedScriptPath) = LocalizadorHerramientasPython.Daemon(
                AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory(), LocalizadorHerramientasPython.CompilacionDeDesarrollo);

            if (!string.IsNullOrEmpty(_cachedExecutablePath)) AppLogger.Info("PythonBridge", $"Motor Python nativo detectado: {_cachedExecutablePath}");
            else if (!string.IsNullOrEmpty(_cachedScriptPath)) AppLogger.Info("PythonBridge", $"Script Python detectado: {_cachedScriptPath}");
        }

        private static string GetPythonCommand()
        {
            // 1. Si GitHub Actions o el sistema tiene pythonLocation definido
            var pyLoc = Environment.GetEnvironmentVariable("pythonLocation");
            if (!string.IsNullOrEmpty(pyLoc))
            {
                var pyExe = Path.Combine(pyLoc, "python.exe");
                if (File.Exists(pyExe)) return pyExe;
            }

            // 2. Si existe el entorno virtual local, buscarlo. (Solo se llega aquí en desarrollo: sin script no hay intérprete que lanzar.)
            return LocalizadorHerramientasPython.BuscarEnRepositorio(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(".venv", "Scripts", "python.exe"))
                   ?? "python";
        }

        /// <summary>
        /// Compone el PATH para los procesos hijo: primero la carpeta FFmpeg embebida de la
        /// app (ffmpeg.exe/ffprobe.exe distribuidos con el instalador), luego el directorio
        /// base, y después el PATH de usuario + sistema (incluye WinGet\Links).
        /// </summary>
        private static string ComposePath()
        {
            var partes = new List<string>();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegDir = Path.Combine(baseDir, "FFmpeg");
            if (Directory.Exists(ffmpegDir)) partes.Add(ffmpegDir);
            if (Directory.Exists(baseDir)) partes.Add(baseDir);

            string userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            string machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            partes.Add(userPath);
            partes.Add(machinePath);

            return string.Join(";", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private class PingResult
        {
            public bool Success { get; set; }
            public string? Version { get; set; }
            public string? Engine { get; set; }
        }
    }
}
