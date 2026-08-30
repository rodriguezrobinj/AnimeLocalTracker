using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
                    StandardInputEncoding = Encoding.UTF8
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

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Enviar payload JSON por stdin
                string jsonInput = JsonSerializer.Serialize(payload, JsonOptions);
                await process.StandardInput.WriteLineAsync(jsonInput);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();

                // Leer respuesta JSON por stdout
                string output = await process.StandardOutput.ReadToEndAsync(ct);
                string error = await process.StandardError.ReadToEndAsync(ct);

                await process.WaitForExitAsync(ct);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    AppLogger.Debug("PythonBridge", $"Stderr de '{command}': {error}");
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    return default;
                }

                var res = JsonSerializer.Deserialize<TResponse>(output, SnakeCaseOptions);
                if (res == null)
                {
                    res = JsonSerializer.Deserialize<TResponse>(output, JsonOptions);
                }
                return res;
            }
            catch (Exception ex)
            {
                AppLogger.Error("PythonBridge", $"Error ejecutando comando Python '{command}': {ex.Message}", ex);
                return default;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  DAEMON persistente (JSON-lines por stdin/stdout)
        // ────────────────────────────────────────────────────────────────
        private const string DaemonGreeting = "daemon";
        private static readonly object DaemonLock = new();
        private static readonly SemaphoreSlim DaemonSemaphore = new(1, 1);
        private static Process? _daemonProcess;
        private static StreamReader? _daemonOut;
        private static StreamWriter? _daemonIn;

        /// <summary>
        /// Ejecuta un comando a través del proceso daemon persistente. Devuelve default
        /// si el daemon no está disponible (cae al path one-shot).
        /// </summary>
        private async Task<TResponse?> ExecuteViaDaemonAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct)
        {
            // El protocolo es una sola línea por comando en un stream compartido:
            // serializa send+receive para evitar respuestas cruzadas entre llamadas.
            await DaemonSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureDaemonStartedAsync(ct).ConfigureAwait(false);
                if (_daemonProcess == null || _daemonProcess.HasExited)
                    return default;

                try
                {
                    string jsonInput = JsonSerializer.Serialize(payload, JsonOptions);
                    string send = JsonSerializer.Serialize(new { command, payload = System.Text.Json.Nodes.JsonNode.Parse(jsonInput) }, JsonOptions);

                    lock (DaemonLock)
                    {
                        if (_daemonIn == null || _daemonOut == null)
                            return default;

                        _daemonIn.WriteLine(send);
                        _daemonIn.Flush();
                    }

                    // Leer la línea de respuesta (el daemon responde una línea JSON por comando)
                    string? output = await _daemonOut.ReadLineAsync(ct).AsTask().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(output))
                        return default;

                    var res = JsonSerializer.Deserialize<TResponse>(output, SnakeCaseOptions);
                    return res ?? JsonSerializer.Deserialize<TResponse>(output, JsonOptions);
                }
                catch
                {
                    // Si el daemon murió, cae al one-shot
                    return default;
                }
            }
            finally
            {
                DaemonSemaphore.Release();
            }
        }

        /// <summary>
        /// Arranca el proceso daemon de forma 100% asíncrona si no está vivo (una única instancia compartida).
        /// Hereda el PATH completo (usuario + sistema + FFmpeg embebido de la app) para que
        /// el daemon encuentre herramientas como ffmpeg instaladas por winget en WinGet\Links
        /// o los binarios ffmpeg.exe/ffprobe.exe distribuidos en la carpeta FFmpeg/ de la app.
        /// </summary>
        private async Task EnsureDaemonStartedAsync(CancellationToken ct)
        {
            if (_daemonProcess != null && !_daemonProcess.HasExited)
                return;

            string path = !string.IsNullOrEmpty(_cachedExecutablePath) ? _cachedExecutablePath
                : throw new InvalidOperationException("Daemon requiere el ejecutable compilado.");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--daemon",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    // ¡Sin BOM! Encoding.UTF8 (estático) sí lo emite, y el primer
                    // comando llegaba como \ufeff{...} → "JSON inválido".
                    StandardInputEncoding = new UTF8Encoding(false)
                };
                psi.Environment["PATH"] = ComposePath();

                var proc = new Process { StartInfo = psi };
                proc.Start();
                _daemonProcess = proc;
                _daemonOut = proc.StandardOutput;
                _daemonIn = proc.StandardInput;

                // Consumir el saludo de forma no bloqueante antes del primer comando
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    _ = await _daemonOut.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    CleanupDaemon();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PythonBridge", $"No se pudo iniciar el daemon Python: {ex.Message}");
                _daemonProcess = null;
                _daemonOut = null;
                _daemonIn = null;
            }
        }

        private void CleanupDaemon()
        {
            try
            {
                _daemonProcess?.Kill(entireProcessTree: true);
            }
            catch { }
            _daemonProcess = null;
            _daemonOut = null;
            _daemonIn = null;
        }

        private void ResolveExecutable()
        {
            if (!string.IsNullOrEmpty(_cachedExecutablePath) || !string.IsNullOrEmpty(_cachedScriptPath))
                return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. Buscar binario compilado embebido AnimeTrackerTools.exe
            string[] possibleBinaryPaths =
            {
                Path.Combine(baseDir, "Tools", "AnimeTrackerTools.exe"),
                Path.Combine(baseDir, "AnimeTrackerTools.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "AnimeLocalTracker", "Tools", "AnimeTrackerTools.exe")
            };

            foreach (var path in possibleBinaryPaths)
            {
                if (File.Exists(path))
                {
                    _cachedExecutablePath = path;
                    AppLogger.Info("PythonBridge", $"Motor Python nativo detectado: {path}");
                    return;
                }
            }

            // 2. Buscar script cli.py en directorio tools/python/
            string[] possibleScriptPaths =
            {
                Path.Combine(baseDir, "tools", "python", "cli.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "python", "cli.py"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "tools", "python", "cli.py")
            };

            foreach (var path in possibleScriptPaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    _cachedScriptPath = fullPath;
                    AppLogger.Info("PythonBridge", $"Script Python detectado: {fullPath}");
                    return;
                }
            }
        }

        private static string GetPythonCommand()
        {
            // Si existe el entorno virtual local, usarlo
            string[] venvPythons =
            {
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "python", ".venv", "Scripts", "python.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "tools", "python", ".venv", "Scripts", "python.exe")
            };

            foreach (var venv in venvPythons)
            {
                string full = Path.GetFullPath(venv);
                if (File.Exists(full)) return full;
            }

            return "python";
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
