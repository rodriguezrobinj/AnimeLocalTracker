using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Fase 1 offline-first: sin internet las peticiones fallan al instante y los reintentos no esperan un minuto.</summary>
public class ConexionOfflineTests
{
    private sealed class RedFalsa : IConectividadRed
    {
        public bool HayInternet { get; set; }
        public Task<bool> EsperarInternetAsync(TimeSpan maximo, CancellationToken ct) => Task.FromResult(HayInternet);
    }

    /// <summary>Transporte de prueba: cuenta los intentos y responde lo que se le diga (o falla como un DNS sin red).</summary>
    private sealed class Transporte : HttpMessageHandler
    {
        public int Intentos;
        public Func<int, HttpResponseMessage>? Responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Intentos);
            if (Responder == null) throw new HttpRequestException("No se conoce ese host.");
            return Task.FromResult(Responder(n));
        }
    }

    private static HttpClient Cliente(IAsyncPolicy<HttpResponseMessage>? politica, GuardiaConexion? guardia, Transporte transporte)
    {
        var services = new ServiceCollection();
        if (guardia != null) services.AddSingleton(guardia);
        var builder = services.AddHttpClient("prueba").ConfigurePrimaryHttpMessageHandler(() => transporte);
        if (politica != null) builder.AddPolicyHandler(politica);
        if (guardia != null) builder.ConCorteSinConexion();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("prueba");
    }

    private static Task<HttpResponseMessage> Pedir(HttpClient http) => http.GetAsync("https://graphql.anilist.co/");

    // === Guardia ===

    [Fact]
    public void SinInternet_BloqueaSalvoUnaPeticionDePruebaCada30s()
    {
        var red = new RedFalsa { HayInternet = false };
        var ahora = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var guardia = new GuardiaConexion(red, () => ahora);

        guardia.DebeBloquear().Should().BeFalse("la primera sale de prueba por si el indicador de Windows se equivoca");
        guardia.DebeBloquear().Should().BeTrue();
        ahora = ahora.AddSeconds(29);
        guardia.DebeBloquear().Should().BeTrue();
        ahora = ahora.AddSeconds(1);
        guardia.DebeBloquear().Should().BeFalse("pasaron 30 s: otra de prueba");
        guardia.DebeBloquear().Should().BeTrue();
    }

    [Fact]
    public void ConInternet_NuncaBloquea()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = true });

        for (int i = 0; i < 5; i++) guardia.DebeBloquear().Should().BeFalse();
        guardia.PareceSinConexion.Should().BeFalse();
    }

    [Fact]
    public void SiUnServidorRespondio_NoSeBloqueaAunqueWindowsDigaSinInternet_HastaDosMinutosSinRespuestas()
    {
        // Caso real: en el equipo del usuario el indicador de Windows parpadea ("sin internet" con internet funcionando).
        var red = new RedFalsa { HayInternet = false };
        var ahora = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var guardia = new GuardiaConexion(red, () => ahora);
        guardia.DebeBloquear().Should().BeFalse(); // la de prueba
        guardia.RegistrarRespuesta();              // …respondió

        for (int i = 0; i < 5; i++) guardia.DebeBloquear().Should().BeFalse();
        guardia.PareceSinConexion.Should().BeFalse();

        red.HayInternet = true;
        guardia.DebeBloquear().Should().BeFalse();
        red.HayInternet = false; // parpadeo: no cambia nada, respondió hace poco
        guardia.DebeBloquear().Should().BeFalse();

        ahora = ahora.AddMinutes(2); // dos minutos sin ninguna respuesta: se vuelve a creer a Windows (con su petición de prueba)
        guardia.PareceSinConexion.Should().BeTrue();
        guardia.DebeBloquear().Should().BeFalse();
        guardia.DebeBloquear().Should().BeTrue();
    }

    [Fact]
    public void UnFalloDeRedDespuesDeLaUltimaRespuesta_AnulaLaConfianza()
    {
        var red = new RedFalsa { HayInternet = false };
        var ahora = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var guardia = new GuardiaConexion(red, () => ahora);
        guardia.DebeBloquear();
        guardia.RegistrarRespuesta();
        guardia.PareceSinConexion.Should().BeFalse();

        ahora = ahora.AddSeconds(10);
        guardia.RegistrarFalloDeRed(); // la red se cayó: ya no vale "respondió hace poco"

        guardia.PareceSinConexion.Should().BeTrue();
    }

    [Fact]
    public async Task UnaPeticionSinRespuesta_SeApuntaComoFalloDeRed_PeroUnErrorDelServidorNo()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = true });
        var http = Cliente(null, guardia, new Transporte());
        await ((Func<Task>)(() => Pedir(http))).Should().ThrowAsync<HttpRequestException>();
        guardia.UltimoFalloDeRedUtc.Should().BeAfter(DateTime.MinValue);

        var guardia2 = new GuardiaConexion(new RedFalsa { HayInternet = true });
        var http2 = Cliente(null, guardia2, new Transporte { Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) });
        await Pedir(http2);
        guardia2.UltimoFalloDeRedUtc.Should().Be(DateTime.MinValue, "el servidor respondió: hay red");
    }

    [Fact]
    public void ModoSinRedForzado_BloqueaSiempre_SinPeticionesDePrueba()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = true }, sinRedForzado: true);

        guardia.DebeBloquear().Should().BeTrue();
        guardia.DebeBloquear().Should().BeTrue();
        guardia.PareceSinConexion.Should().BeTrue();
    }

    [Fact]
    public void SinConexion_NoSeRegistraComoErrorConTraza()
    {
        string fuente = "PruebaSinConexion_" + Guid.NewGuid().ToString("N");

        AppLogger.Error(fuente, "Error al obtener perfil de usuario", new SinConexionException());

        var entrada = AppLogger.RecentLogs.Last(l => l.Source == fuente);
        entrada.Level.Should().Be("DEBUG");
        entrada.ExceptionDetails.Should().BeNullOrEmpty();
    }

    // === Cliente completo ===

    [Fact]
    public async Task SinInternet_ConLaPoliticaDeAniList_FallaAlInstanteSinTocarLaRed()
    {
        // Antes (medido el 2026-09-29): 60,0 s por consulta sin internet.
        var red = new RedFalsa { HayInternet = false };
        var guardia = new GuardiaConexion(red);
        guardia.DebeBloquear(); // gasta la peticiÃ³n de prueba del periodo
        var transporte = new Transporte();
        var http = Cliente(PoliticasHttp.AniList(), guardia, transporte);

        var reloj = Stopwatch.StartNew();
        var accion = () => Pedir(http);

        await accion.Should().ThrowAsync<SinConexionException>();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        transporte.Intentos.Should().Be(0);
    }

    [Fact]
    public async Task LaPeticionDePrueba_SiFalla_NoSeReintentaContraLaRed()
    {
        // La guardia va despuÃ©s de la polÃ­tica: el reintento vuelve a consultarla y, dentro del periodo, ya no sale.
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = false });
        var transporte = new Transporte();
        var http = Cliente(PoliticasHttp.AniList(esperaRedBase: TimeSpan.FromMilliseconds(10)), guardia, transporte);

        await ((Func<Task>)(() => Pedir(http))).Should().ThrowAsync<HttpRequestException>();

        transporte.Intentos.Should().Be(1);
    }

    [Fact]
    public async Task ConInternet_UnErrorDeRed_SeReintentaDosVecesConEsperasCortas()
    {
        var transporte = new Transporte();
        var http = Cliente(PoliticasHttp.AniList(esperaRedBase: TimeSpan.FromMilliseconds(10)), new GuardiaConexion(new RedFalsa { HayInternet = true }), transporte);

        var reloj = Stopwatch.StartNew();
        await ((Func<Task>)(() => Pedir(http))).Should().ThrowAsync<HttpRequestException>();

        transporte.Intentos.Should().Be(3);
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "antes cada reintento esperaba 60 s");
    }

    [Fact]
    public async Task UnErrorDelServidor_SeReintentaCorto_YLuegoFunciona()
    {
        var transporte = new Transporte { Responder = n => new HttpResponseMessage(n < 3 ? HttpStatusCode.BadGateway : HttpStatusCode.OK) };
        var http = Cliente(PoliticasHttp.AniList(esperaRedBase: TimeSpan.FromMilliseconds(10)), null, transporte);

        var respuesta = await Pedir(http);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        transporte.Intentos.Should().Be(3);
    }

    [Fact]
    public async Task ElLimiteDePeticiones_SeRespetaEsperandoAntesDeReintentar()
    {
        var transporte = new Transporte { Responder = n => new HttpResponseMessage(n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) };
        var http = Cliente(PoliticasHttp.AniList(esperaLimitePorDefecto: TimeSpan.FromMilliseconds(200)), null, transporte);

        var reloj = Stopwatch.StartNew();
        var respuesta = await Pedir(http);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        transporte.Intentos.Should().Be(2);
        reloj.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(190));
    }

    [Fact]
    public async Task AnimeThemes_SinInternet_NoReintenta()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = false });
        guardia.DebeBloquear();
        var transporte = new Transporte();
        var http = Cliente(AnimeThemesService.CrearPoliticaReintentos(TimeSpan.FromSeconds(5)), guardia, transporte);

        var reloj = Stopwatch.StartNew();
        await ((Func<Task>)(() => Pedir(http))).Should().ThrowAsync<SinConexionException>();

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConsultaRealDeAniList_SinInternet_DevuelveFalloEnMenosDeUnSegundo()
    {
        var guardia = new GuardiaConexion(new RedFalsa { HayInternet = false });
        guardia.DebeBloquear();
        var transporte = new Transporte();
        var services = new ServiceCollection();
        services.AddSingleton(guardia);
        services.AddHttpClient<IAnimeTrackingService, AniListTrackingService>()
            .ConfigurePrimaryHttpMessageHandler(() => transporte)
            .AddPolicyHandler(PoliticasHttp.AniList())
            .ConCorteSinConexion();
        var tracking = services.BuildServiceProvider().GetRequiredService<IAnimeTrackingService>();

        var reloj = Stopwatch.StartNew();
        var (exito, _) = await tracking.ObtenerDatosExtraAsync(1);

        exito.Should().BeFalse();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}

