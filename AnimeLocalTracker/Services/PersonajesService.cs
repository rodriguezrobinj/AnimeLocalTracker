using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Minijuegos;

namespace AnimeLocalTracker.Services;

public interface IPersonajesService
{
    /// <summary>
    /// Personajes de los animes indicados (id de AniList → personajes), desde la copia local. Solo consulta a AniList los
    /// animes sin copia o con copia vieja; sin red devuelve lo que haya guardado (los que no, quedan sin personajes).
    /// </summary>
    Task<Dictionary<int, List<PersonajeAnime>>> ObtenerAsync(IReadOnlyCollection<int> animeIds, CancellationToken ct = default);

    /// <summary>Ruta local de la imagen del personaje (la descarga la primera vez); null si no se pudo conseguir.</summary>
    Task<string?> ObtenerImagenAsync(PersonajeAnime personaje, CancellationToken ct = default);

    /// <summary>La imagen del personaje ya está guardada (sin conexión, solo esos pueden ser respuesta).</summary>
    bool TieneImagenLocal(PersonajeAnime personaje);
}

/// <summary>
/// Personajes de AniList para "Adivina el personaje". Los datos (nombre, género, edad, rol…) se guardan en la base de
/// datos y las imágenes en una carpeta propia dentro de <see cref="AppDataPaths"/>, así que cada anime se pide a AniList
/// una sola vez al mes y después se juega sin conexión.
/// </summary>
public sealed class PersonajesService : IPersonajesService
{
    internal static readonly TimeSpan Vigencia = TimeSpan.FromDays(30);

    /// <summary>Animes por consulta a AniList (una petición con 5 animes × 12 personajes pesa ~20 KB; el límite es de 30 peticiones/minuto).</summary>
    internal const int AnimesPorConsulta = 5;

    private const long MaximoBytesImagen = 5L * 1024 * 1024;

    /// <summary>Imágenes que se conservan en disco (~90 KB cada una): al pasarse se borran las más antiguas; volver a jugar las descarga de nuevo.</summary>
    internal int MaximoImagenesEnCache { get; set; } = 200;
    private const int MaximoApodosGuardados = 12;
    private const string SeparadorApodos = " | ";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Consulta = @"
        query ($ids: [Int], $porAnime: Int) {
            Page(perPage: 50) {
                media(id_in: $ids, type: ANIME) {
                    id
                    characters(perPage: $porAnime, sort: [ROLE, FAVOURITES_DESC]) {
                        edges {
                            role
                            node {
                                id
                                name { full native alternative }
                                image { large }
                                gender
                                age
                                favourites
                            }
                        }
                    }
                }
            }
        }";

    private readonly IDatabaseService _database;
    private readonly HttpClient _http;
    private readonly string _carpetaImagenes;

    /// <param name="carpetaImagenes">Solo para pruebas: carpeta alternativa (las pruebas nunca deben escribir en <see cref="AppDataPaths"/>).</param>
    public PersonajesService(IDatabaseService database, HttpClient http, string? carpetaImagenes = null)
    {
        _database = database;
        _http = http;
        _carpetaImagenes = carpetaImagenes ?? AppDataPaths.CharactersDir;

        try
        {
            if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeLocalTracker");
            if (!_http.DefaultRequestHeaders.Contains("Accept")) _http.DefaultRequestHeaders.Add("Accept", "application/json");
            _http.Timeout = TimeSpan.FromSeconds(15); // un juego no puede quedarse minutos esperando a AniList
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PersonajesService", $"No se pudo configurar el HttpClient: {ex.Message}");
        }
    }

    public async Task<Dictionary<int, List<PersonajeAnime>>> ObtenerAsync(IReadOnlyCollection<int> animeIds, CancellationToken ct = default)
    {
        var ids = animeIds.Where(i => i > 0).Distinct().ToList();
        var resultado = new Dictionary<int, List<PersonajeAnime>>();
        if (ids.Count == 0) return resultado;

        List<PersonajeAnime> guardados = new();
        List<PersonajesAnimeSync> marcas = new();
        try
        {
            guardados = await _database.ObtenerPersonajesAsync(ids);
            marcas = await _database.ObtenerMarcasPersonajesAsync(ids);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PersonajesService", $"No se pudo leer la copia local de personajes: {ex.Message}");
        }

        var porAnime = guardados.GroupBy(p => p.AnimeId).ToDictionary(g => g.Key, g => g.ToList());
        var ahora = DateTime.UtcNow;
        var vigentes = marcas.Where(m => !DebeConsultar(m, ahora)).Select(m => m.AnimeId).ToHashSet();
        var pendientes = ids.Where(i => !vigentes.Contains(i)).ToList();

        foreach (int id in ids)
        {
            if (porAnime.TryGetValue(id, out var lista)) resultado[id] = lista;
        }

        foreach (var lote in pendientes.Chunk(AnimesPorConsulta))
        {
            ct.ThrowIfCancellationRequested();

            var nuevos = await ConsultarAsync(lote, ct);
            // Sin red o AniList caído: se sigue con la copia vieja y no se insiste con los demás lotes. Con wifi pero sin salida a
            // internet cada intento esperaba su tiempo máximo y la partida agotaba el suyo teniendo personajes guardados.
            if (nuevos == null) break;

            try { await _database.GuardarPersonajesAsync(nuevos); }
            catch (Exception ex) { AppLogger.Debug("PersonajesService", $"No se pudieron guardar los personajes: {ex.Message}"); }

            foreach (var (animeId, lista) in nuevos) resultado[animeId] = lista;
        }

        return resultado;
    }

    internal static bool DebeConsultar(PersonajesAnimeSync marca, DateTime ahoraUtc)
    {
        var consultado = marca.FechaUtc.Kind == DateTimeKind.Local ? marca.FechaUtc.ToUniversalTime() : DateTime.SpecifyKind(marca.FechaUtc, DateTimeKind.Utc);
        return ahoraUtc - consultado > Vigencia;
    }

    /// <summary>Null si la consulta falla. Los animes que AniList no devuelve (o sin personajes) quedan con lista vacía.</summary>
    private async Task<Dictionary<int, List<PersonajeAnime>>?> ConsultarAsync(int[] animeIds, CancellationToken ct)
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                query = Consulta,
                variables = new { ids = animeIds, porAnime = AdivinaPersonajeJuego.PersonajesPorAnime }
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Warn("PersonajesService", $"AniList respondió {(int)response.StatusCode} al pedir personajes.");
                return null;
            }

            string contenido = await response.Content.ReadAsStringAsync(ct);
            if (contenido.Contains("\"errors\"", StringComparison.Ordinal))
            {
                AppLogger.Warn("PersonajesService", $"AniList devolvió error al pedir personajes: {Truncar(contenido)}");
                return null;
            }

            return Convertir(animeIds, JsonSerializer.Deserialize<RespuestaPersonajes>(contenido, JsonOptions));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PersonajesService", $"No se pudieron consultar los personajes: {ex.Message}");
            return null;
        }
    }

    internal static Dictionary<int, List<PersonajeAnime>> Convertir(IEnumerable<int> animeIds, RespuestaPersonajes? respuesta)
    {
        var resultado = animeIds.ToDictionary(id => id, _ => new List<PersonajeAnime>());

        foreach (var media in respuesta?.Data?.Page?.Media ?? new List<MediaPersonajes>())
        {
            if (!resultado.TryGetValue(media.Id, out var lista)) continue;

            foreach (var arista in media.Characters?.Edges ?? new List<AristaPersonaje>())
            {
                var nodo = arista.Node;
                if (nodo == null || nodo.Id <= 0 || string.IsNullOrWhiteSpace(nodo.Name?.Full)) continue;
                if (lista.Any(p => p.PersonajeId == nodo.Id)) continue;

                lista.Add(new PersonajeAnime
                {
                    AnimeId = media.Id,
                    PersonajeId = nodo.Id,
                    Nombre = nodo.Name!.Full!.Trim(),
                    NombreNativo = nodo.Name.Native?.Trim() ?? string.Empty,
                    Alternativos = string.Join(SeparadorApodos, (nodo.Name.Alternative ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a) && !a.Contains(SeparadorApodos, StringComparison.Ordinal))
                        .Select(a => a.Trim())
                        .Take(MaximoApodosGuardados)),
                    ImagenUrl = nodo.Image?.Large ?? string.Empty,
                    Genero = nodo.Gender?.Trim() ?? string.Empty,
                    Edad = nodo.Age?.Trim() ?? string.Empty,
                    Rol = arista.Role?.Trim().ToUpperInvariant() ?? string.Empty,
                    Favoritos = nodo.Favourites ?? 0
                });
            }
        }
        return resultado;
    }

    public bool TieneImagenLocal(PersonajeAnime personaje)
    {
        if (personaje.PersonajeId <= 0) return false;
        try
        {
            var info = new FileInfo(Path.Combine(_carpetaImagenes, $"{personaje.PersonajeId}.jpg"));
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<string?> ObtenerImagenAsync(PersonajeAnime personaje, CancellationToken ct = default)
    {
        if (personaje.PersonajeId <= 0) return null;

        string ruta = Path.Combine(_carpetaImagenes, $"{personaje.PersonajeId}.jpg");
        try
        {
            if (File.Exists(ruta) && new FileInfo(ruta).Length > 0) return ruta;

            if (!ImageCacheService.EsHostPortadaPermitido(personaje.ImagenUrl)) return null;

            using var response = await _http.GetAsync(personaje.ImagenUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is long declarada && declarada > MaximoBytesImagen) return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaximoBytesImagen || !ImageCacheService.EsImagenValida(bytes)) return null;

            Directory.CreateDirectory(_carpetaImagenes);
            string temporal = ruta + ".tmp";
            await File.WriteAllBytesAsync(temporal, bytes, ct);
            File.Move(temporal, ruta, overwrite: true); // nunca queda una imagen a medias en la caché
            PodarImagenesAntiguas(ruta);
            return ruta;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PersonajesService", $"No se pudo conseguir la imagen del personaje {personaje.PersonajeId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deja como mucho <see cref="MaximoImagenesEnCache"/> imágenes: borra las más antiguas, nunca la recién guardada.</summary>
    internal void PodarImagenesAntiguas(string conservar)
    {
        try
        {
            var sobrantes = new DirectoryInfo(_carpetaImagenes).EnumerateFiles("*.jpg")
                .Where(f => !string.Equals(f.FullName, conservar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Max(0, MaximoImagenesEnCache - 1)); // -1: el que se acaba de guardar también cuenta
            foreach (var f in sobrantes.ToList()) f.Delete();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PersonajesService", $"No se pudo podar la caché de imágenes: {ex.Message}");
        }
    }

    private static string Truncar(string texto) => texto.Length <= 300 ? texto : texto[..300] + "…";

    // ── Estructura de la respuesta de AniList ──

    internal sealed class RespuestaPersonajes
    {
        [JsonPropertyName("data")] public DatosPersonajes? Data { get; set; }
    }

    internal sealed class DatosPersonajes
    {
        [JsonPropertyName("Page")] public PaginaPersonajes? Page { get; set; }
    }

    internal sealed class PaginaPersonajes
    {
        [JsonPropertyName("media")] public List<MediaPersonajes>? Media { get; set; }
    }

    internal sealed class MediaPersonajes
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("characters")] public ConexionPersonajes? Characters { get; set; }
    }

    internal sealed class ConexionPersonajes
    {
        [JsonPropertyName("edges")] public List<AristaPersonaje>? Edges { get; set; }
    }

    internal sealed class AristaPersonaje
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("node")] public NodoPersonaje? Node { get; set; }
    }

    internal sealed class NodoPersonaje
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public NombrePersonaje? Name { get; set; }
        [JsonPropertyName("image")] public ImagenPersonaje? Image { get; set; }
        [JsonPropertyName("gender")] public string? Gender { get; set; }
        [JsonPropertyName("age")] public string? Age { get; set; }
        [JsonPropertyName("favourites")] public int? Favourites { get; set; }
    }

    internal sealed class NombrePersonaje
    {
        [JsonPropertyName("full")] public string? Full { get; set; }
        [JsonPropertyName("native")] public string? Native { get; set; }
        [JsonPropertyName("alternative")] public List<string>? Alternative { get; set; }
    }

    internal sealed class ImagenPersonaje
    {
        [JsonPropertyName("large")] public string? Large { get; set; }
    }
}
