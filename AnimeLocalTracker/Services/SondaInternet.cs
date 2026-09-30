using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Comprobación propia de "¿hay internet?", independiente del indicador de Windows y de <see cref="GuardiaConexion"/> (cliente
/// propio). Primero el servidor de comprobación de Microsoft (el mismo que usa Windows; solo cuenta un 200, un portal cautivo
/// redirige): en el caso normal es la única petición. Si no responde, AniList y AnimeThemes a la vez (cualquier respuesta HTTP
/// demuestra que hay red). Todo con un tope de <see cref="TiempoMaximo"/>: sin red real falla en milisegundos. No se usa lo que
/// devuelven, así que el http del primero no es un riesgo.
/// </summary>
public static class SondaInternet
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    })
    { Timeout = TimeSpan.FromSeconds(3) };

    internal static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TiempoPrimero = TimeSpan.FromMilliseconds(1500);

    private const string Microsoft = "http://www.msftconnecttest.com/connecttest.txt";

    private static readonly (string Url, Func<HttpResponseMessage, bool> Vale)[] Respaldos =
    {
        ("https://graphql.anilist.co/", _ => true),
        ("https://api.animethemes.moe/", _ => true)
    };

    public static async Task<bool> HayInternetAsync(CancellationToken ct)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TiempoMaximo);

        using (var primero = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
        {
            primero.CancelAfter(TiempoPrimero);
            if (await ProbarAsync(Microsoft, r => r.StatusCode == HttpStatusCode.OK, primero.Token)) return true;
        }
        if (total.IsCancellationRequested) return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        var pendientes = Respaldos.Select(d => ProbarAsync(d.Url, d.Vale, cts.Token)).ToList();
        while (pendientes.Count > 0)
        {
            var terminada = await Task.WhenAny(pendientes);
            pendientes.Remove(terminada);
            if (await terminada)
            {
                cts.Cancel(); // no hace falta esperar a los demás
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> ProbarAsync(string url, Func<HttpResponseMessage, bool> vale, CancellationToken ct)
    {
        try
        {
            using var respuesta = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return vale(respuesta);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
