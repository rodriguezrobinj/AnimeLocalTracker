using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Mal;

/// <summary>Conexión con la cuenta de MyAnimeList (OAuth2 + PKCE) y tokens siempre vigentes.</summary>
public interface IMalAuthService
{
    /// <summary>Hay Client ID de la app registrada en MAL (sin él, la función está apagada).</summary>
    bool Configurado { get; }

    bool Conectado { get; }

    /// <summary>Abre el navegador en MAL y espera (2 min como mucho) a que el usuario dé permiso. True si quedó conectado.</summary>
    Task<bool> ConectarAsync(CancellationToken ct);

    void Desconectar();

    /// <summary>Token de acceso vigente (lo renueva si está por caducar); null si no hay conexión con MAL o no se pudo renovar.</summary>
    Task<string?> TokenValidoAsync(CancellationToken ct);

    /// <summary>MAL rechazó <paramref name="tokenRechazado"/> antes de su caducidad: lo renueva (si nadie lo hizo ya) y devuelve el nuevo; null si no se pudo.</summary>
    Task<string?> RenovarAsync(string tokenRechazado, CancellationToken ct);
}

/// <summary>
/// Flujo de MAL para apps de escritorio: código de autorización con PKCE "plain" (el único que admite) y redirección a un servidor
/// local en el puerto 5051 (AniList usa el 5050). Los tokens nunca van al registro.
/// </summary>
public sealed class MalAuthService : IMalAuthService, IDisposable
{
    /// <summary>Client ID de la app "AnimeLocalTracker" registrada en myanimelist.net/apiconfig (tipo "other": público). Vacío = apagado.</summary>
    internal const string ClientIdMal = "b7b613ea3e7e0088cd0fa92e1761c22e";
    internal const string Redireccion = "http://localhost:5051/callback";
    internal const string NombreCliente = "Mal";
    private const string UrlTokens = "https://myanimelist.net/v1/oauth2/token";
    private const string Origen = "Mal";
    private static readonly TimeSpan MargenRenovacion = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _http;
    private readonly string _clientId;
    private readonly AlmacenTokenMal _almacen;
    private readonly TimeProvider _reloj;
    private readonly SemaphoreSlim _renovando = new(1, 1);
    private readonly object _cerrojo = new();
    private TokensMal? _tokens;
    private bool _tokensLeidos;

    public MalAuthService(IHttpClientFactory http) : this(http, ClientIdMal, new AlmacenTokenMal(), TimeProvider.System) { }

    internal MalAuthService(IHttpClientFactory http, string clientId, AlmacenTokenMal almacen, TimeProvider reloj)
    {
        _http = http;
        _clientId = clientId;
        _almacen = almacen;
        _reloj = reloj;
    }

    public bool Configurado => _clientId.Length > 0;

    public bool Conectado => Tokens != null;

    public void Desconectar() => Guardar(null);

    public void Dispose() => _renovando.Dispose();

    /// <summary>Los tokens, leídos del disco (y descifrados) una sola vez.</summary>
    private TokensMal? Tokens
    {
        get
        {
            lock (_cerrojo)
            {
                if (!_tokensLeidos)
                {
                    _tokens = _almacen.Leer();
                    _tokensLeidos = true;
                }
                return _tokens;
            }
        }
    }

    private void Guardar(TokensMal? tokens)
    {
        lock (_cerrojo)
        {
            _almacen.Guardar(tokens);
            _tokens = tokens;
            _tokensLeidos = true;
        }
    }

    /// <summary>Guarda <paramref name="nuevos"/> solo si los tokens siguen siendo <paramref name="esperados"/>: una renovación que
    /// responde después de "Desconectar" (o de conectar otra cuenta) no debe volver a conectar la vieja.</summary>
    private bool Reemplazar(TokensMal esperados, TokensMal? nuevos)
    {
        lock (_cerrojo)
        {
            if (!ReferenceEquals(Tokens, esperados)) return false;
            Guardar(nuevos);
            return true;
        }
    }

    public Task<string?> TokenValidoAsync(CancellationToken ct) => TokenAsync(null, ct);

    public Task<string?> RenovarAsync(string tokenRechazado, CancellationToken ct) => TokenAsync(tokenRechazado, ct);

    private bool Vigente(TokensMal tokens, string? tokenRechazado)
        => tokens.AccessToken != tokenRechazado && tokens.CaducaUtc - _reloj.GetUtcNow().UtcDateTime > MargenRenovacion;

    private async Task<string?> TokenAsync(string? tokenRechazado, CancellationToken ct)
    {
        var tokens = Tokens;
        if (tokens == null) return null;
        if (Vigente(tokens, tokenRechazado)) return tokens.AccessToken;

        await _renovando.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Otra petición pudo renovarlo mientras se esperaba turno.
            tokens = Tokens;
            if (tokens == null) return null;
            if (Vigente(tokens, tokenRechazado)) return tokens.AccessToken;

            var (nuevos, rechazado) = await PedirTokensAsync(new()
            {
                ["client_id"] = _clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken,
            }, ct).ConfigureAwait(false);
            if (nuevos != null) return Reemplazar(tokens, nuevos) ? nuevos.AccessToken : null;

            if (rechazado && Reemplazar(tokens, null))
            {
                // Permiso revocado o renovación caducada: hay que volver a conectar (un corte de red no desconecta).
                AppLogger.Warn(Origen, "MAL rechazó la renovación del permiso: la cuenta queda desconectada.");
            }
            return null;
        }
        finally
        {
            _renovando.Release();
        }
    }

    public async Task<bool> ConectarAsync(CancellationToken ct)
    {
        if (!Configurado) return false;

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add("http://localhost:5051/");
            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
        {
            AppLogger.Warn(Origen, $"No se pudo abrir el puerto 5051 para la conexión con MAL: {ex.Message}");
            return false;
        }

        string verificador = Pkce.NuevoVerificador();
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        try
        {
            Core.Shell.Abrir(Pkce.UrlAutorizacion(_clientId, verificador, state, Redireccion));
        }
        catch (Exception ex)
        {
            AppLogger.Warn(Origen, $"No se pudo abrir el navegador para conectar con MAL: {ex.Message}");
            return false;
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromMinutes(2));
        Task<HttpListenerContext>? contexto = null;
        try
        {
            while (!limite.IsCancellationRequested)
            {
                contexto = listener.GetContextAsync();
                if (await Task.WhenAny(contexto, Task.Delay(Timeout.Infinite, limite.Token)).ConfigureAwait(false) != contexto) break;

                var peticion = await contexto.ConfigureAwait(false);
                contexto = null;
                if (peticion.Request.Url?.AbsolutePath != "/callback")
                {
                    peticion.Response.StatusCode = 404;
                    peticion.Response.Close();
                    continue;
                }

                if (peticion.Request.QueryString["state"] != state)
                {
                    // Una pestaña de un intento anterior (o una redirección repetida): no es la respuesta a este, se sigue esperando.
                    await ResponderAsync(peticion.Response, LocalizationService.T("Cfg_MalNavegadorError")).ConfigureAwait(false);
                    continue;
                }

                string? codigo = peticion.Request.QueryString["code"];
                bool ok = codigo != null && await CanjearCodigoAsync(codigo, verificador, limite.Token).ConfigureAwait(false);
                await ResponderAsync(peticion.Response, LocalizationService.T(ok ? "Cfg_MalNavegadorOk" : "Cfg_MalNavegadorError")).ConfigureAwait(false);
                return ok;
            }
        }
        finally
        {
            // La espera pendiente falla al cerrarse el servidor local: se observa para que no salga como error sin atender.
            _ = contexto?.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        AppLogger.Warn(Origen, "La conexión con MAL caducó (2 minutos sin respuesta del navegador).");
        return false;
    }

    internal async Task<bool> CanjearCodigoAsync(string codigo, string verificador, CancellationToken ct)
    {
        var (tokens, _) = await PedirTokensAsync(new()
        {
            ["client_id"] = _clientId, ["grant_type"] = "authorization_code", ["code"] = codigo,
            ["redirect_uri"] = Redireccion, ["code_verifier"] = verificador,
        }, ct).ConfigureAwait(false);
        if (tokens != null) Guardar(tokens);
        return tokens != null;
    }

    /// <summary>Pide tokens (sin guardarlos). <c>Rechazado</c> = MAL dijo que no (400/401); un error de red no lo es.</summary>
    private async Task<(TokensMal? Tokens, bool Rechazado)> PedirTokensAsync(Dictionary<string, string> formulario, CancellationToken ct)
    {
        try
        {
            using var cliente = _http.CreateClient(NombreCliente);
            using var respuesta = await cliente.PostAsync(UrlTokens, new FormUrlEncodedContent(formulario), ct).ConfigureAwait(false);
            if (respuesta.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) return (null, true);
            if (!respuesta.IsSuccessStatusCode)
            {
                AppLogger.Warn(Origen, $"MAL no dio tokens ({(int)respuesta.StatusCode}).");
                return (null, false);
            }

            var json = JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
            var tokens = new TokensMal(json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(),
                _reloj.GetUtcNow().UtcDateTime.AddSeconds(json["expires_in"]!.GetValue<int>()));
            return (tokens, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
        {
            AppLogger.Warn(Origen, $"No se pudieron pedir los tokens de MAL: {ex.Message}");
            return (null, false);
        }
    }

    private static async Task ResponderAsync(HttpListenerResponse respuesta, string mensaje)
    {
        string html = "<html><head><meta charset='UTF-8'><title>AnimeLocalTracker</title></head>"
                      + "<body style='font-family:sans-serif;text-align:center;padding:50px;background:#121212;color:white'>"
                      + $"<h2>{WebUtility.HtmlEncode(mensaje)}</h2></body></html>";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        respuesta.ContentType = "text/html; charset=utf-8";
        respuesta.ContentLength64 = bytes.Length;
        await respuesta.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        respuesta.Close();
    }
}
