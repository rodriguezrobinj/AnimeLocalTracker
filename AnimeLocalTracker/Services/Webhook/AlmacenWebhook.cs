using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AnimeLocalTracker.Services.Webhook;

/// <summary>
/// La URL del webhook es un secreto (quien la tenga escribe en el canal): se guarda cifrada con DPAPI para el usuario de Windows,
/// como el token de AniList, y no en settings.json. La ruta es inyectable: las pruebas usan una carpeta temporal.
/// </summary>
public sealed class AlmacenWebhook(string ruta)
{
    public AlmacenWebhook() : this(Path.Combine(AppDataPaths.DataRoot, "discord-webhook.bin")) { }

    public string? Leer()
    {
        if (!File.Exists(ruta)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(ruta), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("Webhook", $"No se pudo leer la URL guardada del webhook: {ex.Message}");
            return null;
        }
    }

    /// <summary>Vacía o null borra la URL guardada.</summary>
    public void Guardar(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            if (File.Exists(ruta)) File.Delete(ruta);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllBytes(ruta, ProtectedData.Protect(Encoding.UTF8.GetBytes(url.Trim()), null, DataProtectionScope.CurrentUser));
    }
}
