using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnimeLocalTracker.Services.Mal;

/// <summary>Tokens de MAL: el de acceso caduca (≈ 31 días) y se renueva con el de renovación sin volver a pedir permiso.</summary>
public sealed record TokensMal(string AccessToken, string RefreshToken, DateTime CaducaUtc);

/// <summary>
/// Los tokens dan acceso a la lista de MAL del usuario: cifrados con DPAPI para su usuario de Windows (como el token de AniList),
/// en su propio archivo. Ruta inyectable: las pruebas usan una carpeta temporal.
/// </summary>
public sealed class AlmacenTokenMal(string ruta)
{
    public AlmacenTokenMal() : this(Path.Combine(AppDataPaths.DataRoot, "mal-token.bin")) { }

    public TokensMal? Leer()
    {
        if (!File.Exists(ruta)) return null;
        try
        {
            return JsonSerializer.Deserialize<TokensMal>(ProtectedData.Unprotect(File.ReadAllBytes(ruta), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Warn("Mal", $"No se pudieron leer los tokens guardados de MAL: {ex.Message}");
            return null;
        }
    }

    /// <summary>null borra los tokens (desconectar).</summary>
    public void Guardar(TokensMal? tokens)
    {
        if (tokens == null)
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllBytes(ruta, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens), null, DataProtectionScope.CurrentUser));
    }
}
