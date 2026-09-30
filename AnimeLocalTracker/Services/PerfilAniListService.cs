using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>Nombre y avatar de la cuenta de AniList; <see cref="Avatar"/> es la ruta del archivo local si está guardado (si no, la URL).</summary>
public sealed record PerfilAniList(string Nombre, string? Avatar);

/// <summary>
/// Perfil de AniList con copia local (nombre en JSON + avatar descargado): la Galería y la tarjeta Wrapped lo muestran sin
/// conexión. Antes, sin internet salía el usuario por defecto y la tarjeta esperaba un minuto al perfil.
/// </summary>
public interface IPerfilAniListService
{
    /// <summary>El de AniList si responde (y lo guarda); si no, el guardado; null si no hay ninguno.</summary>
    Task<PerfilAniList?> ObtenerAsync(string token);
}

public sealed class PerfilAniListService : IPerfilAniListService
{
    private const long MaximoBytesAvatar = 2 * 1024 * 1024;

    private readonly IAnimeTrackingService _tracking;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _rutaPerfil;
    private readonly string _rutaAvatar;

    /// <param name="carpeta">Solo para pruebas: carpeta alternativa a la de datos del usuario.</param>
    public PerfilAniListService(IAnimeTrackingService tracking, IHttpClientFactory httpFactory, string? carpeta = null)
    {
        _tracking = tracking;
        _httpFactory = httpFactory;
        string dir = carpeta ?? AppDataPaths.DataRoot;
        _rutaPerfil = Path.Combine(dir, "perfil_anilist.json");
        _rutaAvatar = Path.Combine(dir, "avatar_anilist.img");
    }

    private sealed record PerfilGuardado(string Nombre, string? AvatarUrl);

    public async Task<PerfilAniList?> ObtenerAsync(string token)
    {
        var guardado = Leer();
        var remoto = string.IsNullOrEmpty(token) ? null : await _tracking.ObtenerPerfilUsuarioAsync(token);
        if (remoto == null)
            return guardado == null ? null : new PerfilAniList(guardado.Nombre, File.Exists(_rutaAvatar) ? _rutaAvatar : guardado.AvatarUrl);

        var nuevo = new PerfilGuardado(remoto.Name ?? string.Empty, remoto.Avatar?.Large);
        bool avatarCambio = guardado?.AvatarUrl != nuevo.AvatarUrl || !File.Exists(_rutaAvatar);
        Guardar(nuevo);
        if (avatarCambio) await DescargarAvatarAsync(nuevo.AvatarUrl);
        return new PerfilAniList(nuevo.Nombre, File.Exists(_rutaAvatar) ? _rutaAvatar : nuevo.AvatarUrl);
    }

    private PerfilGuardado? Leer()
    {
        try
        {
            return File.Exists(_rutaPerfil) ? JsonSerializer.Deserialize<PerfilGuardado>(File.ReadAllText(_rutaPerfil)) : null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PerfilAniListService", $"No se pudo leer el perfil guardado: {ex.Message}");
            return null;
        }
    }

    private void Guardar(PerfilGuardado perfil)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_rutaPerfil)!);
            File.WriteAllText(_rutaPerfil, JsonSerializer.Serialize(perfil));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PerfilAniListService", $"No se pudo guardar el perfil: {ex.Message}");
        }
    }

    private async Task DescargarAvatarAsync(string? url)
    {
        try
        {
            if (File.Exists(_rutaAvatar)) File.Delete(_rutaAvatar); // el de antes ya no es el de la cuenta
            if (!ImageCacheService.EsHostPortadaPermitido(url)) return;

            using var http = _httpFactory.CreateClient();
            using var respuesta = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!respuesta.IsSuccessStatusCode) return;
            if (respuesta.Content.Headers.ContentLength is long declarada && declarada > MaximoBytesAvatar) return;

            byte[] bytes = await respuesta.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaximoBytesAvatar || !ImageCacheService.EsImagenValida(bytes)) return;
            await File.WriteAllBytesAsync(_rutaAvatar, bytes);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PerfilAniListService", $"No se pudo guardar el avatar: {ex.Message}");
        }
    }
}
