using System;
using System.Diagnostics;
using System.IO;
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

        private class PingResult
        {
            public bool Success { get; set; }
            public string? Version { get; set; }
            public string? Engine { get; set; }
        }
    }
}
