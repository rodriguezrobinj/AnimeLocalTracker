using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Mega y TransferIt (el servicio de transferencias de MEGA), investigados contra los servidores reales
/// (docs/investigacion-servidores-descarga.md):
/// • Mega: el archivo se baja CIFRADO (AES-128-CTR) de una URL temporal que da la API anónima; la clave va en el propio
///   enlace (<c>mega.nz/file/ID#CLAVE</c>). CTR permite pedir rangos y reanudar en cualquier múltiplo de 16 bytes.
/// • TransferIt: la misma API pero el archivo se sirve EN CLARO, sin clave.
/// Aquí vive lo que no necesita red (enlaces, descifrado, lectura de las respuestas); las peticiones las hace
/// <see cref="AnimeAv1VideoSourceResolver"/> con su HttpClient.
/// </summary>
public static partial class MegaTransferIt
{
    public readonly record struct EnlaceMega(string Handle, byte[] Clave);
    public readonly record struct ArchivoTransferencia(string Handle, long Tamano);
    public sealed record EnlaceDescarga(string Url, long Tamano);

    public const string ApiMega = "https://g.api.mega.co.nz/cs";
    public const string ApiTransferIt = "https://bt7.api.mega.co.nz/cs";

    /// <summary>Código de la API cuando se agota la cuota de transferencia gratuita.</summary>
    public const int ErrorCuotaExcedida = -17;

    private const string PrefijoClave = "#mega=";
    private const int TamanoBuffer = 1 << 20; // múltiplo de 16: cada trozo empieza en un bloque de AES

    [GeneratedRegex(@"^https://(?:www\.)?mega\.nz/(?:file|embed)/([A-Za-z0-9_-]{8})#([A-Za-z0-9_-]{43})$")]
    private static partial Regex EnlaceMegaRegex();

    [GeneratedRegex(@"^https://(?:www\.)?transfer\.it/t/([A-Za-z0-9_-]{12})/?$")]
    private static partial Regex EnlaceTransferItRegex();

    // === Enlaces ===

    /// <summary>Handle y clave de 32 bytes de un enlace de archivo (o embed) de Mega. Null si no es uno válido.</summary>
    public static EnlaceMega? ParsearEnlaceMega(string? url)
    {
        var m = EnlaceMegaRegex().Match(url ?? string.Empty);
        if (!m.Success) return null;
        var clave = DecodificarBase64Url(m.Groups[2].Value);
        return clave is { Length: 32 } ? new EnlaceMega(m.Groups[1].Value, clave) : null;
    }

    /// <summary>Handle de una transferencia (<c>transfer.it/t/HANDLE</c>). Null si no es un enlace válido.</summary>
    public static string? ExtraerHandleTransferIt(string? url)
    {
        var m = EnlaceTransferItRegex().Match(url ?? string.Empty);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// La URL temporal de descarga lleva la clave en el fragmento (<c>#mega=…</c>): un fragmento nunca viaja en la petición
    /// HTTP, y así la clave acompaña a la URL por toda la cadena de descarga sin tocar su firma.
    /// </summary>
    public static string UrlConClave(string urlDescarga, byte[] clave) => urlDescarga + PrefijoClave + CodificarBase64Url(clave);

    public static byte[]? ClaveDeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Fragment.StartsWith(PrefijoClave, StringComparison.Ordinal)) return null;
        var clave = DecodificarBase64Url(uri.Fragment[PrefijoClave.Length..]);
        return clave is { Length: 32 } ? clave : null;
    }

    // === Descifrado (AES-128-CTR) ===

    /// <summary>
    /// Descifra (o cifra: CTR es simétrico) <paramref name="datos"/>, que empiezan en el byte <paramref name="offset"/> del archivo.
    /// Clave AES = palabras 0-3 XOR 4-7 de la clave del enlace; contador = nonce (palabras 4-5) + número de bloque de 16 bytes.
    /// </summary>
    public static void Descifrar(byte[] clave, long offset, Span<byte> datos)
    {
        using var aes = CrearAes(clave);
        Procesar(aes, clave.AsSpan(16, 8), offset, datos, new byte[Redondear(datos.Length)], new byte[Redondear(datos.Length)]);
    }

    /// <summary>Descifra un archivo ya descargado entero (por trozos y en paralelo, como cualquier otra descarga) a otro archivo.</summary>
    public static async Task DescifrarArchivoAsync(string origen, string destino, byte[] clave, CancellationToken ct)
    {
        using var aes = CrearAes(clave);
        var contadores = new byte[TamanoBuffer];
        var flujoDeClave = new byte[TamanoBuffer];
        var buffer = new byte[TamanoBuffer];

        await using var entrada = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await using var salida = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        long offset = 0;
        int leidos;
        // ReadAtLeast llena el búfer salvo al final del archivo: cada trozo empieza en un bloque de 16 bytes.
        while ((leidos = await entrada.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)) > 0)
        {
            Procesar(aes, clave.AsSpan(16, 8), offset, buffer.AsSpan(0, leidos), contadores, flujoDeClave);
            await salida.WriteAsync(buffer.AsMemory(0, leidos), ct);
            offset += leidos;
        }
    }

    /// <summary>
    /// ¿La cabecera es la de un contenedor de video en claro (mp4/mov, mkv/webm, avi, ogg, flv)? Un archivo cifrado nunca lo
    /// parece (probabilidad ~2⁻³²), así que, si el que hay en disco ya lo parece, es que ya se descifró y no hay que
    /// hacerlo otra vez: descifrar dos veces lo convertiría en basura (p. ej. al reanudar tras un corte justo después).
    /// </summary>
    public static bool PareceContenedorDeVideo(ReadOnlySpan<byte> cabecera)
    {
        if (cabecera.Length < 8) return false;
        return cabecera[4..8].SequenceEqual("ftyp"u8)
            || cabecera[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })
            || cabecera[..4].SequenceEqual("RIFF"u8)
            || cabecera[..4].SequenceEqual("OggS"u8)
            || cabecera[..3].SequenceEqual("FLV"u8);
    }

    private static int Redondear(int bytes) => (bytes + 15) / 16 * 16;

    private static Aes CrearAes(byte[] clave)
    {
        if (clave is not { Length: 32 }) throw new ArgumentException("La clave de Mega debe tener 32 bytes.", nameof(clave));
        var claveAes = new byte[16];
        for (int i = 0; i < 16; i++) claveAes[i] = (byte)(clave[i] ^ clave[16 + i]);
        var aes = Aes.Create();
        aes.Key = claveAes;
        return aes;
    }

    private static void Procesar(Aes aes, ReadOnlySpan<byte> nonce, long offset, Span<byte> datos, byte[] contadores, byte[] flujoDeClave)
    {
        if (offset < 0 || offset % 16 != 0) throw new ArgumentException("El offset debe ser múltiplo de 16 (un bloque de AES).", nameof(offset));
        int bloques = Redondear(datos.Length) / 16;
        long bloqueInicial = offset / 16;

        for (int b = 0; b < bloques; b++)
        {
            var contador = contadores.AsSpan(b * 16, 16);
            nonce.CopyTo(contador);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(contador[8..], (ulong)(bloqueInicial + b));
        }

        aes.EncryptEcb(contadores.AsSpan(0, bloques * 16), flujoDeClave.AsSpan(0, bloques * 16), PaddingMode.None);
        for (int i = 0; i < datos.Length; i++) datos[i] ^= flujoDeClave[i];
    }

    // === Respuestas de la API (todas devuelven un arreglo con una respuesta por comando) ===

    /// <summary>Cuerpo de <c>a:g</c> para un archivo de Mega. <c>ssl</c> pide la URL temporal ya en https.</summary>
    public static string CuerpoDescargaMega(string handle) => $$"""[{"a":"g","g":1,"p":"{{handle}}","ssl":1}]""";

    public static string CuerpoListarTransferencia() => """[{"a":"f","c":1,"r":1}]""";

    public static string CuerpoDescargaTransferencia(string handleNodo) => $$"""[{"a":"g","n":"{{handleNodo}}","pt":1,"g":1,"ssl":1}]""";

    /// <summary>URL temporal (https) y tamaño de la respuesta de <c>a:g</c>. Null si es un error o no trae un enlace https.</summary>
    public static EnlaceDescarga? LeerRespuestaG(string? json)
    {
        var respuesta = PrimeraRespuesta(json);
        if (respuesta is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty("g", out var g) || g.ValueKind != JsonValueKind.String) return null;
        string? url = g.GetString();
        if (url == null || !url.StartsWith("https://", StringComparison.Ordinal)) return null;
        return new EnlaceDescarga(url, obj.TryGetProperty("s", out var s) ? LeerEntero(s) : 0);
    }

    /// <summary>Código de error de la API (número negativo), si la respuesta es uno.</summary>
    public static int? CodigoErrorApi(string? json) =>
        PrimeraRespuesta(json) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int codigo) && codigo < 0 ? codigo : null;

    /// <summary>El archivo de la lista de nodos de una transferencia (la carpeta contenedora, <c>t:1</c>, se ignora).</summary>
    public static ArchivoTransferencia? LeerArchivoDeTransferencia(string? json)
    {
        if (PrimeraRespuesta(json) is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("f", out var nodos) || nodos.ValueKind != JsonValueKind.Array) return null;

        foreach (var nodo in nodos.EnumerateArray())
        {
            if (nodo.ValueKind != JsonValueKind.Object || !nodo.TryGetProperty("t", out var t) || LeerEntero(t) != 0) continue;
            if (nodo.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String && h.GetString() is { Length: > 0 } handle)
                return new ArchivoTransferencia(handle, nodo.TryGetProperty("s", out var s) ? LeerEntero(s) : 0);
        }
        return null;
    }

    /// <summary>Primer elemento de la respuesta (o la propia respuesta si es un número suelto, como el error de toda la petición).</summary>
    private static JsonElement? PrimeraRespuesta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            if (raiz.ValueKind == JsonValueKind.Number) return raiz.Clone();
            return raiz.ValueKind == JsonValueKind.Array && raiz.GetArrayLength() > 0 ? raiz[0].Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long LeerEntero(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out long n) => n,
        JsonValueKind.String when long.TryParse(e.GetString(), out long n) => n,
        _ => -1
    };

    // === Base64 URL ===

    private static string CodificarBase64Url(byte[] datos) => Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? DecodificarBase64Url(string texto)
    {
        try
        {
            string b64 = texto.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
