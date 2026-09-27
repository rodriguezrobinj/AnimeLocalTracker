using System;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Tope de conexiones simultáneas por servidor (MP4Upload responde 403 a las de más): distinguir
/// "demasiadas conexiones" de "enlace rechazado", y compartir el tope entre descargas.
/// </summary>
public class LimitadorPorServidorTests
{
    private const string A4 = "a4.mp4upload.com:183";
    private static readonly DateTime T0 = new(2026, 9, 27, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RegistrarRechazo_SinOtrasConexionesRecibiendoDatos_EsDelEnlace()
    {
        var limitador = new LimitadorPorServidor();
        var head = limitador.IntentarAbrir(A4, T0)!;   // abierta pero sin datos aún (p. ej. el sondeo HEAD)
        var get = limitador.IntentarAbrir(A4, T0)!;

        limitador.RegistrarRechazo(get, T0).Should().BeNull("ninguna otra conexión demuestra que el enlace funciona");
        limitador.Tope(A4, T0).Should().BeNull();
        head.Cerrar();
        get.Cerrar();
    }

    [Fact]
    public void RegistrarRechazo_ConOtrasConexionesFuncionando_AprendeElTopeYHaceEsperar()
    {
        var limitador = new LimitadorPorServidor();
        var funcionando = new LimitadorPorServidor.Conexion[8];
        for (int i = 0; i < 8; i++)
        {
            funcionando[i] = limitador.IntentarAbrir(A4, T0)!;
            funcionando[i].MarcarTransfiriendo(T0);
        }
        var novena = limitador.IntentarAbrir(A4, T0)!;

        limitador.RegistrarRechazo(novena, T0).Should().Be(8);
        novena.Cerrar();

        limitador.IntentarAbrir(A4, T0).Should().BeNull("ya hay 8 abiertas y el servidor no acepta más");
        funcionando[0].Cerrar();
        limitador.IntentarAbrir(A4, T0).Should().NotBeNull("se liberó una");
    }

    [Fact]
    public void Tope_EsPorServidor_YCaduca()
    {
        var limitador = new LimitadorPorServidor();
        var ok = limitador.IntentarAbrir(A4, T0)!;
        ok.MarcarTransfiriendo(T0);
        var extra1 = limitador.IntentarAbrir(A4, T0)!;
        var extra2 = limitador.IntentarAbrir(A4, T0)!;
        limitador.RegistrarRechazo(extra2, T0).Should().Be(LimitadorPorServidor.TopeMinimo);

        limitador.Tope("a3.mp4upload.com:183", T0).Should().BeNull("otro servidor no hereda el tope");
        limitador.Tope(A4, T0 + LimitadorPorServidor.VigenciaTope).Should().BeNull("el tope caduca");
        ok.Cerrar(); extra1.Cerrar(); extra2.Cerrar();
    }

    [Fact]
    public void RegistrarRechazo_NuncaSubeUnTopeYaAprendido()
    {
        var limitador = new LimitadorPorServidor();
        var abiertas = new LimitadorPorServidor.Conexion[4];
        for (int i = 0; i < 4; i++) { abiertas[i] = limitador.IntentarAbrir(A4, T0)!; abiertas[i].MarcarTransfiriendo(T0); }
        var quinta = limitador.IntentarAbrir(A4, T0)!;
        limitador.RegistrarRechazo(quinta, T0).Should().Be(4);
        quinta.Cerrar();

        // Con 4 abiertas otra más recibe 403: el tope baja a 3, no se queda en 4.
        abiertas[3].Cerrar();
        var otra = limitador.IntentarAbrir(A4, T0)!;
        limitador.RegistrarRechazo(otra, T0).Should().Be(3);
    }

    [Fact]
    public void RegistrarRechazo_AlArrancar_UnaRespuestaCorrectaRecienteBastaComoPrueba()
    {
        // Al empezar se abren 8 conexiones a la vez y ninguna recibe datos aún, pero el sondeo acaba de contestar bien.
        var limitador = new LimitadorPorServidor();
        var sondeo = limitador.IntentarAbrir(A4, T0)!;
        sondeo.MarcarTransfiriendo(T0);
        sondeo.Cerrar();
        var esperando = new LimitadorPorServidor.Conexion[6];
        for (int i = 0; i < 6; i++) esperando[i] = limitador.IntentarAbrir(A4, T0.AddMilliseconds(100))!;
        var septima = limitador.IntentarAbrir(A4, T0.AddMilliseconds(100))!;

        limitador.RegistrarRechazo(septima, T0.AddMilliseconds(200)).Should().Be(6);
        limitador.HayExitoReciente(A4, T0.AddSeconds(1)).Should().BeTrue();
    }

    [Fact]
    public void RegistrarRechazo_UnExitoAntiguoYaNoEsPrueba()
    {
        var limitador = new LimitadorPorServidor();
        var vieja = limitador.IntentarAbrir(A4, T0)!;
        vieja.MarcarTransfiriendo(T0);
        vieja.Cerrar();
        var ahora = T0 + LimitadorPorServidor.VigenciaExito;
        var otra = limitador.IntentarAbrir(A4, ahora)!;
        var rechazada = limitador.IntentarAbrir(A4, ahora)!;

        limitador.RegistrarRechazo(rechazada, ahora).Should().BeNull();
        limitador.HayExitoReciente(A4, ahora).Should().BeFalse();
        otra.Cerrar();
    }

    [Fact]
    public void Cerrar_EsIdempotente()
    {
        var limitador = new LimitadorPorServidor();
        var conexion = limitador.IntentarAbrir(A4, T0)!;
        conexion.MarcarTransfiriendo(T0);

        conexion.Cerrar();
        conexion.Cerrar();

        limitador.Abiertas(A4).Should().Be(0);
    }
}
