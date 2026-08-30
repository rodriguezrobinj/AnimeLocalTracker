using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Services.Native;

public static class NativeMethods
{
    private const string DllName = "animetracker_core.dll";
    private static readonly Lazy<bool> _isAvailable = new(VerificarDisponibilidad);

    public static bool IsAvailable => _isAvailable.Value;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_parse")]
    private static extern IntPtr NativeAnitomyParse(IntPtr input);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_parse_batch")]
    private static extern IntPtr NativeAnitomyParseBatch(IntPtr inputJsonArray);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "compute_file_fingerprint")]
    private static extern IntPtr NativeComputeFingerprint(IntPtr videoPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_extract_frame")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool NativeExtractFrame(IntPtr videoPath, IntPtr outPath, double timestamp, int width);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_extract_frames_batch")]
    private static extern IntPtr NativeExtractFramesBatch(IntPtr jsonRequests);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_free_string")]
    private static extern void NativeAnitomyFreeString(IntPtr ptr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_version")]
    private static extern IntPtr NativeAnitomyVersion();

    private static bool VerificarDisponibilidad()
    {
        try
        {
            AsegurarFfmpegEnPath();

            if (NativeLibrary.TryLoad(DllName, typeof(NativeMethods).Assembly, null, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }

            // Buscar en el directorio base de la aplicación
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName);
            if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out var localHandle))
            {
                NativeLibrary.Free(localHandle);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"animetracker_core.dll no disponible: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Asegura que ffmpeg.exe/ffprobe.exe embebidos (carpeta FFmpeg/ del output de la app)
    /// estén en el PATH del proceso para que los procesos hijo los encuentren por nombre:
    /// el núcleo Rust (spritesheet.rs) y el daemon Python (ffprobe/ffmpeg) los invocan
    /// vía `Command::new("ffmpeg")`/subprocess, que solo buscan en PATH.
    /// Sin esto, miniaturas, sprite sheets y enriquecimiento fallan en silencio
    /// en máquinas donde el usuario no tiene FFmpeg instalado.
    /// </summary>
    public static void AsegurarFfmpegEnPath()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegDir = Path.Combine(baseDir, "FFmpeg");
            if (!File.Exists(Path.Combine(ffmpegDir, "ffmpeg.exe"))) return;

            string? current = Environment.GetEnvironmentVariable("PATH");
            var partes = (current ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (partes.Contains(ffmpegDir, StringComparer.OrdinalIgnoreCase)) return;

            Environment.SetEnvironmentVariable("PATH", ffmpegDir + Path.PathSeparator + current);
            AppLogger.Info("NativeMethods", $"ffmpeg embebido agregado al PATH: {ffmpegDir}");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"No se pudo agregar ffmpeg embebido al PATH: {ex.Message}");
        }
    }

    public static string? ObtenerVersion()
    {
        if (!IsAvailable) return null;

        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = NativeAnitomyVersion();
            return MarshalStringAndFree(ptr);
        }
        catch
        {
            return null;
        }
    }

    public static ParsedAnimeInfo? ParseFilename(string filename)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(filename)) return null;

        IntPtr inputPtr = IntPtr.Zero;
        IntPtr resultPtr = IntPtr.Zero;
        try
        {
            inputPtr = StringToUtf8Ptr(filename);
            resultPtr = NativeAnitomyParse(inputPtr);
            string? json = MarshalStringAndFree(resultPtr);
            if (string.IsNullOrEmpty(json)) return null;

            return JsonSerializer.Deserialize<ParsedAnimeInfo>(json);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en anitomy_parse nativo: {ex.Message}");
            return null;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static List<ParsedAnimeInfo> ParseBatch(IEnumerable<string> filenames)
    {
        if (!IsAvailable) return new();

        IntPtr inputPtr = IntPtr.Zero;
        IntPtr resultPtr = IntPtr.Zero;
        try
        {
            string jsonInput = JsonSerializer.Serialize(filenames);
            inputPtr = StringToUtf8Ptr(jsonInput);
            resultPtr = NativeAnitomyParseBatch(inputPtr);
            string? json = MarshalStringAndFree(resultPtr);
            if (string.IsNullOrEmpty(json)) return new();

            return JsonSerializer.Deserialize<List<ParsedAnimeInfo>>(json) ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en anitomy_parse_batch nativo: {ex.Message}");
            return new();
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static FingerprintResult? ComputeFingerprint(string videoPath)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(videoPath)) return null;

        IntPtr inputPtr = IntPtr.Zero;
        IntPtr resultPtr = IntPtr.Zero;
        try
        {
            inputPtr = StringToUtf8Ptr(videoPath);
            resultPtr = NativeComputeFingerprint(inputPtr);
            string? json = MarshalStringAndFree(resultPtr);
            if (string.IsNullOrEmpty(json)) return null;

            return JsonSerializer.Deserialize<FingerprintResult>(json);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en compute_file_fingerprint nativo: {ex.Message}");
            return null;
        }
        finally
        {
            if (inputPtr != IntPtr.Zero) Marshal.FreeHGlobal(inputPtr);
        }
    }

    public static bool ExtractFrame(string videoPath, string outPath, double timestamp, int width = 240)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(outPath)) return false;

        IntPtr videoPtr = IntPtr.Zero;
        IntPtr outPtr = IntPtr.Zero;
        try
        {
            videoPtr = StringToUtf8Ptr(videoPath);
            outPtr = StringToUtf8Ptr(outPath);
            return NativeExtractFrame(videoPtr, outPtr, timestamp, width);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en anitomy_extract_frame nativo: {ex.Message}");
            return false;
        }
        finally
        {
            if (videoPtr != IntPtr.Zero) Marshal.FreeHGlobal(videoPtr);
            if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
        }
    }

    /// <summary>
    /// Extrae un lote de fotogramas en paralelo utilizando todos los núcleos de CPU disponibles vía Rayon (<50ms para todo el lote).
    /// </summary>
    public static List<NativeFrameResult>? ExtractFramesBatch(IEnumerable<NativeFrameRequest> requests)
    {
        if (!IsAvailable || requests == null) return null;

        IntPtr inPtr = IntPtr.Zero;
        IntPtr outPtr = IntPtr.Zero;
        try
        {
            string jsonIn = JsonSerializer.Serialize(requests);
            inPtr = StringToUtf8Ptr(jsonIn);
            outPtr = NativeExtractFramesBatch(inPtr);
            string? jsonOut = MarshalStringAndFree(outPtr);
            outPtr = IntPtr.Zero;

            if (string.IsNullOrEmpty(jsonOut)) return null;

            return JsonSerializer.Deserialize<List<NativeFrameResult>>(jsonOut);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en anitomy_extract_frames_batch nativo: {ex.Message}");
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (outPtr != IntPtr.Zero) NativeAnitomyFreeString(outPtr);
        }
    }

    private static IntPtr StringToUtf8Ptr(string str)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(str + '\0');
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static string? MarshalStringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            NativeAnitomyFreeString(ptr);
        }
    }
}

public class NativeFrameRequest
{
    [JsonPropertyName("video_path")]
    public string VideoPath { get; set; } = string.Empty;

    [JsonPropertyName("out_path")]
    public string OutPath { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public double Timestamp { get; set; } = 30.0;

    [JsonPropertyName("width")]
    public uint Width { get; set; } = 320;
}

public class NativeFrameResult
{
    [JsonPropertyName("out_path")]
    public string OutPath { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class ParsedAnimeInfo
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("original_filename")]
    public string OriginalFilename { get; set; } = string.Empty;

    [JsonPropertyName("anime_title")]
    public string? AnimeTitle { get; set; }

    [JsonPropertyName("episode_number")]
    public string? EpisodeNumber { get; set; }

    [JsonPropertyName("release_group")]
    public string? ReleaseGroup { get; set; }

    [JsonPropertyName("video_resolution")]
    public string? VideoResolution { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("file_extension")]
    public string? FileExtension { get; set; }

    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }

    [JsonPropertyName("audio_term")]
    public string? AudioTerm { get; set; }

    [JsonPropertyName("video_term")]
    public string? VideoTerm { get; set; }

    [JsonPropertyName("subtitles")]
    public string? Subtitles { get; set; }
}

public class FingerprintResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    [JsonPropertyName("file_size")]
    public ulong FileSize { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
