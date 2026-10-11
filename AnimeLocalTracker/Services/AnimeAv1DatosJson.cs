using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Lectura de animeav1.com por su respuesta de datos: el sitio (SvelteKit) sirve en "página/__data.json" lo mismo que incrusta en
/// el HTML, pero como JSON. Se lee por nombre de campo, así que el sitio puede reordenar o añadir campos sin romper nada; las
/// expresiones regulares sobre el HTML dependen de ese orden y quedan solo de respaldo, por si esta respuesta deja de estar.
/// </summary>
public static partial class AnimeAv1HtmlParser
{
    private const int MaxProfundidad = 32;
    private const int MaxValores = 50_000;

    public enum RespuestaDeDatos
    {
        /// <summary>No es la respuesta de datos del sitio (HTML, un bloqueo, otra cosa): toca leer la página.</summary>
        NoEsDeDatos,

        /// <summary>El sitio contesta que esa página no existe (en el HTML sería un 404).</summary>
        PaginaInexistente,
        ConDatos
    }

    /// <summary>La dirección de los datos de una página del sitio: "/__data.json" tras la ruta y antes de los parámetros.</summary>
    public static string UrlDeDatos(string urlPagina)
    {
        int consulta = urlPagina.IndexOf('?');
        string ruta = (consulta < 0 ? urlPagina : urlPagina[..consulta]).TrimEnd('/');
        return ruta + "/__data.json" + (consulta < 0 ? string.Empty : urlPagina[consulta..]);
    }

    public static RespuestaDeDatos ClasificarDatos(string? texto)
    {
        if (!LeerDatos(texto, out var datos)) return RespuestaDeDatos.NoEsDeDatos;
        return datos == null ? RespuestaDeDatos.PaginaInexistente : RespuestaDeDatos.ConDatos;
    }

    /// <summary>
    /// True si <paramref name="texto"/> es la respuesta de datos del sitio; <paramref name="datos"/> es entonces lo que trae la
    /// página (media, episode, embeds, downloads, results…), o null si el sitio dice que esa página no existe.
    /// </summary>
    private static bool LeerDatos(string? texto, out JsonObject? datos)
    {
        datos = null;
        if (texto == null || !texto.AsSpan().TrimStart().StartsWith("{")) return false;

        try
        {
            using var documento = JsonDocument.Parse(texto);
            if (documento.RootElement.ValueKind != JsonValueKind.Object
                || !documento.RootElement.TryGetProperty("nodes", out var nodos) || nodos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            // Un nodo por nivel de la ruta (el primero es la sesión). El de la página es el último; si es de error, la página no existe.
            var pagina = nodos.EnumerateArray().LastOrDefault(n => n.ValueKind == JsonValueKind.Object);
            string? tipo = pagina.ValueKind == JsonValueKind.Object && pagina.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (tipo == "error") return true;
            if (tipo != "data" || !pagina.TryGetProperty("data", out var valores) || valores.ValueKind != JsonValueKind.Array) return false;

            int presupuesto = MaxValores;
            datos = Hidratar(valores.EnumerateArray().ToArray(), 0, 0, ref presupuesto) as JsonObject;
            return datos != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deshace el formato de SvelteKit (devalue): una lista plana en la que cada objeto y cada lista guardan POSICIONES de esa
    /// misma lista en vez de valores (así un valor repetido se escribe una vez). Una posición negativa es "sin valor". El
    /// contenido viene del sitio, así que se acotan la profundidad y el total: una posición puede repetirse o apuntarse a sí misma.
    /// </summary>
    private static JsonNode? Hidratar(JsonElement[] valores, int posicion, int profundidad, ref int presupuesto)
    {
        if (posicion < 0 || posicion >= valores.Length || profundidad > MaxProfundidad || --presupuesto < 0) return null;

        var valor = valores[posicion];
        switch (valor.ValueKind)
        {
            case JsonValueKind.Object:
                var objeto = new JsonObject();
                foreach (var campo in valor.EnumerateObject())
                {
                    objeto[campo.Name] = Posicion(campo.Value) is { } i ? Hidratar(valores, i, profundidad + 1, ref presupuesto) : null;
                }
                return objeto;

            case JsonValueKind.Array:
                var lista = new JsonArray();
                foreach (var elemento in valor.EnumerateArray())
                {
                    // Los tipos especiales (["Date", "2024-…"], ["Map", …]) llevan su nombre delante; nada de lo que se lee aquí lo es.
                    if (Posicion(elemento) is not { } i) return null;
                    lista.Add(Hidratar(valores, i, profundidad + 1, ref presupuesto));
                }
                return lista;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;

            default:
                return JsonValue.Create(valor.Clone()); // texto, número o booleano
        }
    }

    private static int? Posicion(JsonElement elemento) => elemento.ValueKind == JsonValueKind.Number && elemento.TryGetInt32(out int i) ? i : null;

    private static JsonNode? Campo(JsonNode? nodo, string nombre) => (nodo as JsonObject)?[nombre];

    private static string? Texto(JsonNode? nodo) => nodo is JsonValue valor && valor.TryGetValue(out string? texto) ? texto?.Trim() : null;

    private static int? Entero(JsonNode? nodo) => nodo is JsonValue valor && valor.TryGetValue(out int numero) ? numero : null;

    private static IEnumerable<JsonNode?> Elementos(JsonNode? nodo) => nodo as JsonArray ?? Enumerable.Empty<JsonNode?>();

    /// <summary>Servidores de <c>embeds</c> o <c>downloads</c>: {SUB:[{server,url},…],DUB:[…]}. Solo direcciones http(s).</summary>
    private static List<EmbedServidor> ServidoresDe(JsonNode? porAudio)
    {
        var lista = new List<EmbedServidor>();
        if (porAudio is not JsonObject pistas) return lista;

        foreach (var (audio, servidores) in pistas)
        {
            foreach (var servidor in Elementos(servidores))
            {
                string? nombre = Texto(Campo(servidor, "server")), url = Texto(Campo(servidor, "url"));
                if (!string.IsNullOrEmpty(nombre) && url != null
                    && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                {
                    lista.Add(new EmbedServidor(nombre, url, audio));
                }
            }
        }
        return lista;
    }

    private static List<(int Id, int Numero)> EpisodiosDe(JsonObject datos) => Elementos(Campo(Campo(datos, "media"), "episodes"))
        .Select(e => (Id: Entero(Campo(e, "id")), Numero: Entero(Campo(e, "number"))))
        .Where(e => e.Id.HasValue && e.Numero.HasValue)
        .Select(e => (e.Id!.Value, e.Numero!.Value))
        .ToList();

    private static TitulosMedia? TitulosDe(JsonObject datos)
    {
        var media = Campo(datos, "media");
        if (Texto(Campo(media, "title")) is not { Length: > 0 } principal) return null;

        var alternativos = (Campo(media, "aka") as JsonObject)?.Select(a => Texto(a.Value)).Where(a => !string.IsNullOrEmpty(a)).Select(a => a!).ToList();
        return new TitulosMedia(principal, alternativos ?? []);
    }

    private static List<(string Slug, string Titulo)> RelationsDe(JsonObject datos) => Elementos(Campo(Campo(datos, "media"), "relations"))
        .Select(r => Campo(r, "destination"))
        .Select(d => (Slug: Texto(Campo(d, "slug")), Titulo: Texto(Campo(d, "title"))))
        .Where(r => !string.IsNullOrEmpty(r.Slug) && !string.IsNullOrEmpty(r.Titulo))
        .Select(r => (r.Slug!, r.Titulo!))
        .ToList();

    private static List<ResultadoCatalogo> ResultadosDe(JsonObject datos)
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Elementos(Campo(datos, "results"))
            // El slug acaba en una dirección ("/media/{slug}"): solo los caracteres que usa el sitio.
            .Select(r => (Slug: Texto(Campo(r, "slug")), Titulo: Texto(Campo(r, "title"))))
            .Where(r => !string.IsNullOrEmpty(r.Slug) && r.Slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') && vistos.Add(r.Slug))
            .Select(r => new ResultadoCatalogo(r.Slug!, r.Titulo))
            .ToList();
    }
}
