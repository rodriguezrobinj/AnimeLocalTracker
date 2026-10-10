using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeLocalTracker.Services.Native;

public static partial class NativeMethods
{
    private const string DllName = "animetracker_core.dll";
    private static readonly Lazy<bool> _isAvailable = new(VerificarDisponibilidad);

    public static bool IsAvailable => _isAvailable.Value;

    // ARC-08: LibraryImport source-generated en lugar de DllImport: el stub se genera
    // en compilación, el puntero nativo se libera siempre en el finally del llamador.
    [LibraryImport(DllName, EntryPoint = "anitomy_parse")]
    private static partial IntPtr NativeAnitomyParse(IntPtr input);

    [LibraryImport(DllName, EntryPoint = "anitomy_parse_batch")]
    private static partial IntPtr NativeAnitomyParseBatch(IntPtr inputJsonArray);

    [LibraryImport(DllName, EntryPoint = "compute_file_fingerprint")]
    private static partial IntPtr NativeComputeFingerprint(IntPtr videoPath);

    [LibraryImport(DllName, EntryPoint = "anitomy_free_string")]
    private static partial void NativeAnitomyFreeString(IntPtr ptr);

    [LibraryImport(DllName, EntryPoint = "anitomy_version")]
    private static partial IntPtr NativeAnitomyVersion();

    public const int FotogramasPorSegundo = 10;
    /// <summary>Muestras de audio (mono, 8 kHz) por fotograma de huella.</summary>
    public const int MuestrasPorFotograma = 800;
    /// <summary>Valores por fotograma de huella: volumen + 7 bandas.</summary>
    public const int ColumnasHuella = 8;

    [LibraryImport(DllName, EntryPoint = "audio_huella")]
    private static unsafe partial int NativeAudioHuella(float* pcm, nuint muestras, float* salida, nuint capacidad);

    [LibraryImport(DllName, EntryPoint = "audio_mejor_coincidencia")]
    private static unsafe partial int NativeAudioMejorCoincidencia(float* ventana, nuint fotogramasVentana, double inicioVentana,
        float* temas, nuint* largos, nuint nTemas, double excluirInicio, double excluirFin, CoincidenciaAudio* salida);

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

    /// <summary>
    /// Huella de audio mono a 8 kHz (fotogramas × <see cref="ColumnasHuella"/>, por filas). Vacía si hay menos de un fotograma.
    /// Null si el núcleo nativo no está o falla: quien llama lo trata como "el motor no respondió".
    /// </summary>
    public static unsafe float[]? HuellaDeAudio(ReadOnlySpan<float> pcm)
    {
        if (!IsAvailable) return null;

        int fotogramas = pcm.Length / MuestrasPorFotograma;
        var salida = new float[fotogramas * ColumnasHuella];
        if (fotogramas == 0) return salida;

        try
        {
            fixed (float* entrada = pcm)
            fixed (float* destino = salida)
            {
                return NativeAudioHuella(entrada, (nuint)pcm.Length, destino, (nuint)fotogramas) == fotogramas ? salida : null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en audio_huella nativo: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// El tema que mejor suena en un tramo del episodio. <paramref name="mejor"/> es null si ninguno se puede ubicar (o todos caen
    /// en el tramo excluido); se devuelve aunque su confianza sea baja. False si el núcleo nativo no está o falla.
    /// </summary>
    public static unsafe bool MejorCoincidencia(float[] ventana, double inicioVentana, IReadOnlyList<float[]> temas,
        (double Inicio, double Fin)? excluir, out CoincidenciaAudio? mejor)
    {
        mejor = null;
        if (!IsAvailable) return false;
        if (temas.Count == 0 || ventana.Length < ColumnasHuella) return true;

        try
        {
            var largos = new nuint[temas.Count];
            int total = 0;
            for (int i = 0; i < temas.Count; i++)
            {
                largos[i] = (nuint)(temas[i].Length / ColumnasHuella);
                total += (int)largos[i] * ColumnasHuella;
            }
            if (total == 0) return true;

            var juntos = new float[total];
            int posicion = 0;
            for (int i = 0; i < temas.Count; i++)
            {
                int valores = (int)largos[i] * ColumnasHuella;
                temas[i].AsSpan(0, valores).CopyTo(juntos.AsSpan(posicion));
                posicion += valores;
            }

            CoincidenciaAudio resultado = default;
            int codigo;
            fixed (float* v = ventana)
            fixed (float* t = juntos)
            fixed (nuint* l = largos)
            {
                codigo = NativeAudioMejorCoincidencia(v, (nuint)(ventana.Length / ColumnasHuella), inicioVentana, t, l, (nuint)temas.Count,
                    excluir?.Inicio ?? double.NaN, excluir?.Fin ?? double.NaN, &resultado);
            }
            if (codigo < 0) return false;
            if (codigo == 1) mejor = resultado;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en audio_mejor_coincidencia nativo: {ex.Message}");
            return false;
        }
    }

    private static IntPtr StringToUtf8Ptr(string str)
    {
        // SEC-06: codificación estricta — un string .NET con surrogates sin par no se
        // convierte silenciosamente a '?': se lanza y el llamador degrada con log
        // (el contrato FFI espera UTF-8 válido terminado en NUL).
        byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(str + '\0');
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

/// <summary>Dónde suena un tema dentro del episodio. Mismos campos y orden que <c>CoincidenciaAudio</c> en audio.rs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CoincidenciaAudio
{
    /// <summary>Posición del tema ganador en la lista enviada.</summary>
    public int Indice;
    /// <summary>1 si el episodio usa solo una parte del tema.</summary>
    public int Parcial;
    public double Inicio;
    public double Fin;
    public double Confianza;
}
