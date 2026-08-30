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

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_free_string")]
    private static extern void NativeAnitomyFreeString(IntPtr ptr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "anitomy_version")]
    private static extern IntPtr NativeAnitomyVersion();

    private static bool VerificarDisponibilidad()
    {
        try
        {
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
