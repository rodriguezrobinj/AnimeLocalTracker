using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Mal;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>MAL: tokens cifrados, canje y renovación, y el cliente de la API. Red simulada; carpeta temporal.</summary>
public sealed class MalConexionTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "mal-pruebas-" + Guid.NewGuid().ToString("N"));
    private readonly RedFalsa _red = new();
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch (IOException) { }
    }

    private AlmacenTokenMal Almacen() => new(Path.Combine(_carpeta, "mal-token.bin"));

    private IHttpClientFactory Fabrica()
    {
        var f = new Mock<IHttpClientFactory>();
        f.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_red, disposeHandler: false));
        return f.Object;
    }

    private MalAuthService Auth(AlmacenTokenMal? almacen = null) => new(Fabrica(), "cliente123", almacen ?? Almacen(), _reloj);

    private const string RespuestaTokens = """{"token_type":"Bearer","expires_in":2678400,"access_token":"acceso1","refresh_token":"renovar1"}""";

    // ---- Almacén ----

    [Fact]
    public void Almacen_GuardaCifradoYLee()
    {
        var almacen = Almacen();
        var tokens = new TokensMal("acceso-secreto", "renovar-secreto", new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));

        almacen.Guardar(tokens);

        almacen.Leer().Should().Be(tokens);
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_carpeta, "mal-token.bin"))).Should().NotContain("secreto");
    }

    [Fact]
    public void Almacen_GuardarNull_Borra()
    {
        var almacen = Almacen();
        almacen.Guardar(new TokensMal("a", "r", DateTime.UtcNow));

        almacen.Guardar(null);

        almacen.Leer().Should().BeNull();
    }

    // ---- Canje y renovación ----

    [Fact]
    public async Task CanjearCodigo_PideLosTokensConPkceYLosGuarda()
    {
        _red.Responder(HttpStatusCode.OK, RespuestaTokens);
        var auth = Auth();

        (await auth.CanjearCodigoAsync("codigo1", "verificador1", CancellationToken.None)).Should().BeTrue();

        var p = _red.Peticiones[0];
        p.Metodo.Should().Be(HttpMethod.Post);
        p.Url.Should().Be("https://myanimelist.net/v1/oauth2/token");
        p.Cuerpo.Should().Contain("client_id=cliente123").And.Contain("grant_type=authorization_code").And.Contain("code=codigo1")
            .And.Contain("code_verifier=verificador1").And.Contain("redirect_uri=http%3A%2F%2Flocalhost%3A5051%2Fcallback");
        auth.Conectado.Should().BeTrue();
        Almacen().Leer()!.CaducaUtc.Should().Be(_reloj.GetUtcNow().UtcDateTime.AddSeconds(2678400));
    }

    [Fact]
    public async Task TokenValido_SinCaducar_NoPideNada()
    {
        Almacen().Guardar(new TokensMal("acceso0", "renovar0", _reloj.GetUtcNow().UtcDateTime.AddDays(10)));

        (await Auth().TokenValidoAsync(CancellationToken.None)).Should().Be("acceso0");
        _red.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task TokenValido_Caducado_LoRenueva()
    {
        Almacen().Guardar(new TokensMal("viejo", "renovar0", _reloj.GetUtcNow().UtcDateTime.AddMinutes(-1)));
        _red.Responder(HttpStatusCode.OK, RespuestaTokens);

        (await Auth().TokenValidoAsync(CancellationToken.None)).Should().Be("acceso1");
        _red.Peticiones[0].Cuerpo.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=renovar0");
        Almacen().Leer()!.RefreshToken.Should().Be("renovar1");
    }

    [Fact]
    public async Task TokenValido_RenovacionRechazada_QuedaDesconectado()
    {
        Almacen().Guardar(new TokensMal("viejo", "revocado", _reloj.GetUtcNow().UtcDateTime.AddMinutes(-1)));
        _red.Responder(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var auth = Auth();

        (await auth.TokenValidoAsync(CancellationToken.None)).Should().BeNull();
        auth.Conectado.Should().BeFalse();
    }

    [Fact]
    public async Task TokenValido_SinConexion_NoDesconecta()
    {
        Almacen().Guardar(new TokensMal("viejo", "renovar0", _reloj.GetUtcNow().UtcDateTime.AddMinutes(-1)));
        _red.Excepcion = new HttpRequestException("Sin conexión a internet.");
        var auth = Auth();

        (await auth.TokenValidoAsync(CancellationToken.None)).Should().BeNull();
        auth.Conectado.Should().BeTrue("un corte de red no es un permiso revocado");
    }

    [Fact]
    public void SinClientId_NoEstaConfigurado()
        => new MalAuthService(Fabrica(), "", Almacen(), _reloj).Configurado.Should().BeFalse();

    // ---- Cliente de la API ----

    private MalApiClient Api()
    {
        Almacen().Guardar(new TokensMal("acceso0", "renovar0", _reloj.GetUtcNow().UtcDateTime.AddDays(10)));
        return new MalApiClient(Fabrica(), Auth());
    }

    [Fact]
    public async Task Leer_EnLaLista_DevuelveSuEstado()
    {
        _red.Responder(HttpStatusCode.OK, """{"id":52991,"title":"Frieren","my_list_status":{"status":"watching","score":8,"num_episodes_watched":12}}""");

        var (resultado, estado) = await Api().LeerAsync(52991, CancellationToken.None);

        resultado.Should().Be(ResultadoMal.Ok);
        estado.Should().Be(new EstadoMal("watching", 12, 8));
        var p = _red.Peticiones[0];
        p.Metodo.Should().Be(HttpMethod.Get);
        p.Url.Should().Be("https://api.myanimelist.net/v2/anime/52991?fields=my_list_status");
        p.Autorizacion.Should().Be("Bearer acceso0");
    }

    [Fact]
    public async Task Leer_FueraDeLaLista_OkSinEstado()
    {
        _red.Responder(HttpStatusCode.OK, """{"id":52991,"title":"Frieren"}""");

        (await Api().LeerAsync(52991, CancellationToken.None)).Should().Be((ResultadoMal.Ok, (EstadoMal?)null));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ResultadoMal.LimiteAlcanzado)]
    [InlineData(HttpStatusCode.InternalServerError, ResultadoMal.Error)]
    public async Task Leer_Errores(HttpStatusCode codigo, ResultadoMal esperado)
    {
        _red.Responder(codigo, "{}");

        (await Api().LeerAsync(1, CancellationToken.None)).Resultado.Should().Be(esperado);
    }

    [Fact]
    public async Task TokenInvalidadoAntesDeCaducar_LoRenuevaYReintenta()
    {
        // Contraseña cambiada en MAL: el token guardado aún "vale" 10 días, pero MAL ya no lo acepta.
        _red.Responder(HttpStatusCode.Unauthorized, "{}");
        _red.Responder(HttpStatusCode.OK, RespuestaTokens);
        _red.Responder(HttpStatusCode.OK, """{"id":1,"name":"robin_anime"}""");

        (await Api().UsuarioAsync(CancellationToken.None)).Should().Be("robin_anime");
        _red.Peticiones[1].Cuerpo.Should().Contain("grant_type=refresh_token");
        _red.Peticiones[2].Autorizacion.Should().Be("Bearer acceso1");
    }

    [Fact]
    public async Task TokenInvalidadoYRenovacionRechazada_SinSesionYDesconectado()
    {
        _red.Responder(HttpStatusCode.Unauthorized, "{}");
        _red.Responder(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        Almacen().Guardar(new TokensMal("acceso0", "revocado", _reloj.GetUtcNow().UtcDateTime.AddDays(10)));
        var auth = Auth();

        (await new MalApiClient(Fabrica(), auth).LeerAsync(1, CancellationToken.None)).Resultado.Should().Be(ResultadoMal.SinSesion);
        auth.Conectado.Should().BeFalse();
    }

    [Fact]
    public async Task RenovacionQueRespondeTrasDesconectar_NoVuelveAConectar()
    {
        Almacen().Guardar(new TokensMal("viejo", "renovar0", _reloj.GetUtcNow().UtcDateTime.AddMinutes(-1)));
        _red.Responder(HttpStatusCode.OK, RespuestaTokens);
        var auth = Auth();
        _red.AlPedir = auth.Desconectar; // el usuario pulsa "Desconectar" mientras MAL responde a la renovación

        (await auth.TokenValidoAsync(CancellationToken.None)).Should().BeNull();
        auth.Conectado.Should().BeFalse();
        Almacen().Leer().Should().BeNull();
    }

    [Fact]
    public async Task Actualizar_PatchConEstadoProgresoYNota()
    {
        _red.Responder(HttpStatusCode.OK, """{"status":"watching","score":9,"num_episodes_watched":14}""");

        (await Api().ActualizarAsync(52991, new EstadoMal("watching", 14, 9), CancellationToken.None)).Should().Be(ResultadoMal.Ok);

        var p = _red.Peticiones[0];
        p.Metodo.Should().Be(HttpMethod.Patch);
        p.Url.Should().Be("https://api.myanimelist.net/v2/anime/52991/my_list_status");
        p.Cuerpo.Should().Contain("status=watching").And.Contain("num_watched_episodes=14").And.Contain("score=9");
    }

    [Fact]
    public async Task Usuario_DevuelveElNombre()
    {
        _red.Responder(HttpStatusCode.OK, """{"id":1,"name":"robin_anime"}""");

        (await Api().UsuarioAsync(CancellationToken.None)).Should().Be("robin_anime");
        _red.Peticiones[0].Url.Should().Be("https://api.myanimelist.net/v2/users/@me");
    }

    [Fact]
    public async Task SinSesion_NoHacePeticiones()
    {
        var api = new MalApiClient(Fabrica(), Auth());

        (await api.LeerAsync(1, CancellationToken.None)).Resultado.Should().Be(ResultadoMal.SinSesion);
        _red.Peticiones.Should().BeEmpty();
    }

    // ---- Dobles ----

    internal sealed class RedFalsa : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _respuestas = new();
        public List<(HttpMethod Metodo, string Url, string Cuerpo, string? Autorizacion)> Peticiones { get; } = new();
        public Exception? Excepcion { get; set; }
        public Action? AlPedir { get; set; }

        public void Responder(HttpStatusCode codigo, string json) => _respuestas.Enqueue((codigo, json));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Excepcion != null) throw Excepcion;
            AlPedir?.Invoke();
            string cuerpo = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Peticiones.Add((request.Method, request.RequestUri!.ToString(), cuerpo, request.Headers.Authorization?.ToString()));
            var (codigo, json) = _respuestas.Count > 0 ? _respuestas.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(codigo) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    internal sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
