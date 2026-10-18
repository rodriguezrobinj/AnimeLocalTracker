using System;
using System.Security.Cryptography;

namespace AnimeLocalTracker.Services.Mal;

/// <summary>
/// PKCE de MAL: solo admite el método "plain" (el challenge es el propio verificador). El verificador es de un solo uso por
/// intento de conexión y no sale del PC salvo en la URL de autorización y en el canje del código.
/// </summary>
internal static class Pkce
{
    /// <summary>64 bytes aleatorios en base64 para URL: 86 caracteres de [A-Za-z0-9-_] (RFC 7636 pide 43-128).</summary>
    public static string NuevoVerificador()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string UrlAutorizacion(string clientId, string verificador, string state, string redireccion)
        => "https://myanimelist.net/v1/oauth2/authorize?response_type=code"
           + $"&client_id={Uri.EscapeDataString(clientId)}"
           + $"&code_challenge={Uri.EscapeDataString(verificador)}&code_challenge_method=plain"
           + $"&state={Uri.EscapeDataString(state)}"
           + $"&redirect_uri={Uri.EscapeDataString(redireccion)}";
}
