using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Mal;

public enum ResultadoMal { Ok, Error, LimiteAlcanzado, SinSesion }

/// <summary>Las tres llamadas de la API v2 de MAL que usa la sincronización.</summary>
public interface IMalApiClient
{
    Task<string?> UsuarioAsync(CancellationToken ct);

    /// <summary>Lo que el usuario tiene en su lista de MAL para ese anime (null si no está en la lista).</summary>
    Task<(ResultadoMal Resultado, EstadoMal? Estado)> LeerAsync(int malId, CancellationToken ct);

    Task<ResultadoMal> ActualizarAsync(int malId, EstadoMal estado, CancellationToken ct);
}

public sealed class MalApiClient(IHttpClientFactory http, IMalAuthService auth) : IMalApiClient
{
    private const string Base = "https://api.myanimelist.net/v2/";
    private const string Origen = "Mal";

    public async Task<string?> UsuarioAsync(CancellationToken ct)
    {
        var (resultado, json) = await EnviarAsync(HttpMethod.Get, "users/@me", null, ct).ConfigureAwait(false);
        return resultado == ResultadoMal.Ok ? json?["name"]?.GetValue<string>() : null;
    }

    public async Task<(ResultadoMal Resultado, EstadoMal? Estado)> LeerAsync(int malId, CancellationToken ct)
    {
        var (resultado, json) = await EnviarAsync(HttpMethod.Get, $"anime/{malId}?fields=my_list_status", null, ct).ConfigureAwait(false);
        if (resultado != ResultadoMal.Ok || json?["my_list_status"] is not JsonObject lista) return (resultado, null);

        return (ResultadoMal.Ok, new EstadoMal(lista["status"]?.GetValue<string>() ?? string.Empty,
            lista["num_episodes_watched"]?.GetValue<int>() ?? 0, lista["score"]?.GetValue<int>() ?? 0));
    }

    public async Task<ResultadoMal> ActualizarAsync(int malId, EstadoMal estado, CancellationToken ct)
    {
        var formulario = new Dictionary<string, string>
        {
            ["status"] = estado.Estado,
            ["num_watched_episodes"] = estado.Progreso.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["score"] = estado.Nota.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return (await EnviarAsync(HttpMethod.Patch, $"anime/{malId}/my_list_status", formulario, ct).ConfigureAwait(false)).Resultado;
    }

    private async Task<(ResultadoMal Resultado, JsonNode? Json)> EnviarAsync(HttpMethod metodo, string ruta, Dictionary<string, string>? formulario, CancellationToken ct)
    {
        string? token = await auth.TokenValidoAsync(ct).ConfigureAwait(false);
        if (token == null) return (ResultadoMal.SinSesion, null);

        try
        {
            using var cliente = http.CreateClient(MalAuthService.NombreCliente);
            var primera = await PedirAsync(cliente, metodo, ruta, formulario, token, ct).ConfigureAwait(false);
            // MAL invalidó el token antes de su caducidad (contraseña cambiada, permiso revocado): se renueva y se reintenta una vez.
            // Si la renovación también se rechaza, la cuenta queda desconectada y la interfaz lo dice.
            if (primera.StatusCode == HttpStatusCode.Unauthorized && await auth.RenovarAsync(token, ct).ConfigureAwait(false) is { } renovado)
            {
                primera.Dispose();
                primera = await PedirAsync(cliente, metodo, ruta, formulario, renovado, ct).ConfigureAwait(false);
            }
            using var respuesta = primera;

            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests) return (ResultadoMal.LimiteAlcanzado, null);
            if (respuesta.StatusCode == HttpStatusCode.Unauthorized) return (ResultadoMal.SinSesion, null);
            if (!respuesta.IsSuccessStatusCode)
            {
                AppLogger.Warn(Origen, $"MAL respondió {(int)respuesta.StatusCode} a {metodo} {ruta.Split('?')[0]}.");
                return (ResultadoMal.Error, null);
            }
            return (ResultadoMal.Ok, JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            AppLogger.Warn(Origen, $"No se pudo hablar con MAL ({metodo} {ruta.Split('?')[0]}): {ex.Message}");
            return (ResultadoMal.Error, null);
        }
    }

    private static async Task<HttpResponseMessage> PedirAsync(HttpClient cliente, HttpMethod metodo, string ruta,
        Dictionary<string, string>? formulario, string token, CancellationToken ct)
    {
        using var peticion = new HttpRequestMessage(metodo, Base + ruta) { Content = formulario == null ? null : new FormUrlEncodedContent(formulario) };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await cliente.SendAsync(peticion, ct).ConfigureAwait(false);
    }
}
