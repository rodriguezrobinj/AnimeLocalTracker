using System;
using System.Text.Json.Nodes;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Discord;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class DiscordPresenciaTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 17, 20, 0, 0, TimeSpan.Zero);

    private static EstadoPresencia Estado(int animeId = 101, string titulo = "Frieren", int episodio = 14,
        double posicion = 755, double duracion = 1450, bool reproduciendo = true)
        => new(animeId, titulo, episodio, posicion, duracion, reproduciendo);

    private static string Episodio14 => string.Format(LocalizationService.T("Act_EpisodioFormato"), 14);

    // ---- ActividadDiscord ----

    [Fact]
    public void Construir_Reproduciendo_PoneTituloEpisodioYTiempos()
    {
        var a = ActividadDiscord.Construir(Estado(), "https://s4.anilist.co/portada.jpg", Ahora);

        a["type"]!.GetValue<int>().Should().Be(3);
        a["details"]!.GetValue<string>().Should().Be("Frieren");
        a["state"]!.GetValue<string>().Should().Be(Episodio14);
        long inicio = Ahora.ToUnixTimeSeconds() - 755;
        a["timestamps"]!["start"]!.GetValue<long>().Should().Be(inicio);
        a["timestamps"]!["end"]!.GetValue<long>().Should().Be(inicio + 1450);
        a["assets"]!["large_image"]!.GetValue<string>().Should().Be("https://s4.anilist.co/portada.jpg");
        a["assets"]!["large_text"]!.GetValue<string>().Should().Be("Frieren");
    }

    [Fact]
    public void Construir_EnPausa_SinTiemposYConEstadoEnPausa()
    {
        var a = ActividadDiscord.Construir(Estado(reproduciendo: false), null, Ahora);

        a.ContainsKey("timestamps").Should().BeFalse();
        a["state"]!.GetValue<string>().Should().Be(string.Format(LocalizationService.T("Discord_EnPausaFormato"), Episodio14));
    }

    [Fact]
    public void Construir_DuracionDesconocida_SoloInicio()
    {
        var a = ActividadDiscord.Construir(Estado(duracion: 0), null, Ahora);

        a["timestamps"]!["start"]!.GetValue<long>().Should().Be(Ahora.ToUnixTimeSeconds() - 755);
        a["timestamps"]!.AsObject().ContainsKey("end").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Cache\portadas\101.jpg")]
    [InlineData("http://inseguro/portada.jpg")]
    public void Construir_SinUrlHttps_UsaElLogo(string? url)
    {
        var a = ActividadDiscord.Construir(Estado(), url, Ahora);

        a["assets"]!["large_image"]!.GetValue<string>().Should().Be("logo");
    }

    [Fact]
    public void Construir_ConAnime_AnadeBotonDeAniList()
    {
        var a = ActividadDiscord.Construir(Estado(animeId: 101), null, Ahora);

        var boton = a["buttons"]!.AsArray().Should().ContainSingle().Subject!;
        boton["label"]!.GetValue<string>().Should().Be(LocalizationService.T("Discord_VerEnAniList"));
        boton["url"]!.GetValue<string>().Should().Be("https://anilist.co/anime/101");
    }

    [Fact]
    public void Construir_SinAnime_SinBotones()
    {
        var a = ActividadDiscord.Construir(Estado(animeId: 0), null, Ahora);

        a.ContainsKey("buttons").Should().BeFalse();
    }

    [Fact]
    public void Construir_TituloLargo_SeRecortaA128()
    {
        var a = ActividadDiscord.Construir(Estado(titulo: new string('a', 200)), null, Ahora);

        a["details"]!.GetValue<string>().Length.Should().Be(128);
        a["assets"]!["large_text"]!.GetValue<string>().Length.Should().Be(128);
    }

    [Fact]
    public void Construir_TituloDeUnCaracter_SeRellenaHastaDos()
    {
        // Discord rechaza details/state de menos de 2 caracteres (el anime "K" existe).
        var a = ActividadDiscord.Construir(Estado(titulo: "K"), null, Ahora);

        a["details"]!.GetValue<string>().Should().StartWith("K").And.HaveLength(2);
    }

    [Fact]
    public void ComandoSetActivity_SinActividad_EnviaNullParaBorrar()
    {
        var json = JsonNode.Parse(ActividadDiscord.ComandoSetActivity(null, 1234))!;

        json["cmd"]!.GetValue<string>().Should().Be("SET_ACTIVITY");
        json["args"]!["pid"]!.GetValue<int>().Should().Be(1234);
        json["args"]!.AsObject().ContainsKey("activity").Should().BeTrue();
        json["args"]!["activity"].Should().BeNull();
        json["nonce"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    // ---- ReglaEnvioPresencia ----

    [Fact]
    public void HayCambio_SinNadaEnviado_True()
        => ReglaEnvioPresencia.HayCambio(null, Ahora, Estado(), Ahora).Should().BeTrue();

    [Fact]
    public void HayCambio_OtroEpisodio_True()
        => ReglaEnvioPresencia.HayCambio(Estado(), Ahora, Estado(episodio: 15, posicion: 0), Ahora).Should().BeTrue();

    [Fact]
    public void HayCambio_PausaOReanudacion_True()
    {
        ReglaEnvioPresencia.HayCambio(Estado(), Ahora, Estado(reproduciendo: false), Ahora).Should().BeTrue();
        ReglaEnvioPresencia.HayCambio(Estado(reproduciendo: false), Ahora, Estado(), Ahora).Should().BeTrue();
    }

    [Fact]
    public void HayCambio_PosicionAvanzaAlRitmoEsperado_False()
        => ReglaEnvioPresencia.HayCambio(Estado(posicion: 755), Ahora, Estado(posicion: 757), Ahora.AddSeconds(2)).Should().BeFalse();

    [Fact]
    public void HayCambio_SaltoEnElVideo_True()
        => ReglaEnvioPresencia.HayCambio(Estado(posicion: 755), Ahora, Estado(posicion: 817), Ahora.AddSeconds(2)).Should().BeTrue();

    [Fact]
    public void HayCambio_DesvioPequeno_False()
        => ReglaEnvioPresencia.HayCambio(Estado(posicion: 755), Ahora, Estado(posicion: 759), Ahora.AddSeconds(2)).Should().BeFalse();

    [Fact]
    public void HayCambio_EnPausaConOtraPosicion_False()
        => ReglaEnvioPresencia.HayCambio(Estado(posicion: 755, reproduciendo: false), Ahora,
            Estado(posicion: 900, reproduciendo: false), Ahora.AddSeconds(30)).Should().BeFalse();

    [Fact]
    public void HayCambio_DuracionConocidaTarde_True()
        => ReglaEnvioPresencia.HayCambio(Estado(duracion: 0), Ahora, Estado(duracion: 1450), Ahora).Should().BeTrue();

    // ---- ClienteIpcDiscord (tramas) ----

    [Fact]
    public void CodificarTrama_CabeceraLittleEndianConLongitudEnBytesUtf8()
    {
        const string json = "{\"a\":\"ñ\"}"; // 9 caracteres, 10 bytes en UTF-8

        byte[] trama = ClienteIpcDiscord.CodificarTrama(1, json);

        trama.Should().HaveCount(8 + 10);
        trama[..8].Should().Equal(1, 0, 0, 0, 10, 0, 0, 0);
        System.Text.Encoding.UTF8.GetString(trama, 8, 10).Should().Be(json);
    }

    [Fact]
    public void LeerCabecera_DevuelveOpcodeYLongitudDeLaTrama()
    {
        byte[] trama = ClienteIpcDiscord.CodificarTrama(2, "{\"cmd\":\"x\"}");

        ClienteIpcDiscord.LeerCabecera(trama.AsSpan(0, 8)).Should().Be((2, 11));
    }

    // ---- DiscordPresenceService (sin trabajador: nunca abre la tubería de Discord) ----

    private static (DiscordPresenceService Servicio, Mock<ISettingsService> Ajustes, AppSettings Config) Servicio(bool activo = true)
    {
        var config = new AppSettings { DiscordRichPresence = activo };
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        var servicio = new DiscordPresenceService(ajustes.Object, Mock.Of<IDatabaseService>(), "123", TimeProvider.System, conTrabajador: false);
        return (servicio, ajustes, config);
    }

    [Fact]
    public void Actualizar_AjusteApagado_NoPublicaNada()
    {
        var (servicio, _, _) = Servicio(activo: false);

        servicio.Actualizar(Estado());

        servicio.Deseado.Should().BeNull();
    }

    [Fact]
    public void Actualizar_AjusteEncendido_GuardaElUltimoEstado()
    {
        var (servicio, _, _) = Servicio();

        servicio.Actualizar(Estado(posicion: 10));
        servicio.Actualizar(Estado(posicion: 11));

        servicio.Deseado.Should().Be(Estado(posicion: 11));
    }

    [Fact]
    public void Actualizar_SinIdDeAplicacion_NoPublicaNada()
    {
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DiscordRichPresence = true });
        var servicio = new DiscordPresenceService(ajustes.Object, Mock.Of<IDatabaseService>(), "", TimeProvider.System, conTrabajador: false);

        servicio.Actualizar(Estado());

        servicio.Deseado.Should().BeNull();
    }

    [Fact]
    public void Limpiar_BorraElEstado()
    {
        var (servicio, _, _) = Servicio();
        servicio.Actualizar(Estado());

        servicio.Limpiar();

        servicio.Deseado.Should().BeNull();
    }

    [Fact]
    public void Suprimir_NoVuelveAPublicarElMismoEpisodio()
    {
        var (servicio, _, _) = Servicio();
        servicio.Actualizar(Estado(episodio: 14));

        servicio.Suprimir();
        servicio.Actualizar(Estado(episodio: 14, posicion: 800));

        servicio.Deseado.Should().BeNull();
    }

    [Fact]
    public void Suprimir_OtroEpisodioVuelveAPublicar()
    {
        var (servicio, _, _) = Servicio();
        servicio.Actualizar(Estado(episodio: 14));

        servicio.Suprimir();
        servicio.Actualizar(Estado(episodio: 15, posicion: 0));

        servicio.Deseado.Should().Be(Estado(episodio: 15, posicion: 0));
    }

    [Fact]
    public void Limpiar_ConElAjusteApagado_NoArrancaElTrabajador()
    {
        // Cerrar el reproductor llama a Limpiar siempre; con Discord apagado no debe quedar un hilo despertándose cada segundo.
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DiscordRichPresence = false });
        using var servicio = new DiscordPresenceService(ajustes.Object, Mock.Of<IDatabaseService>(), "123", TimeProvider.System, conTrabajador: true);

        servicio.Limpiar();

        servicio.TrabajadorIniciado.Should().BeFalse();
    }

    [Fact]
    public void ApagarElAjusteEnCaliente_BorraElEstado()
    {
        var (servicio, ajustes, config) = Servicio();
        servicio.Actualizar(Estado());

        config.DiscordRichPresence = false;
        ajustes.Raise(s => s.ConfiguracionModificada += null, config);

        servicio.Deseado.Should().BeNull();
    }
}
