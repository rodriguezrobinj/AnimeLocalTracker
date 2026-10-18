using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Webhook;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class WebhookTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 17, 20, 0, 0, TimeSpan.Zero);
    private const string Portada = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx154587.jpg";

    private static JsonNode Embed(string json) => JsonNode.Parse(json)!["embeds"]![0]!;

    // ---- Episodio emitido ----

    [Fact]
    public void EpisodioEmitido_Uno_TituloConElEpisodioYPortada()
    {
        var json = JsonNode.Parse(EmbedWebhook.EpisodioEmitido(154587, "Frieren", Portada, 15, 15, Ahora))!;
        var e = json["embeds"]![0]!;

        json["username"]!.GetValue<string>().Should().Be("AnimeLocalTracker");
        e["title"]!.GetValue<string>().Should().Be("Frieren · " + string.Format(LocalizationService.T("Act_EpisodioFormato"), 15));
        e["description"]!.GetValue<string>().Should().Be(LocalizationService.T("Webhook_YaSeEmitio"));
        e["color"]!.GetValue<int>().Should().Be(0xF59E0B);
        e["url"]!.GetValue<string>().Should().Be("https://anilist.co/anime/154587");
        e["thumbnail"]!["url"]!.GetValue<string>().Should().Be(Portada);
        e["timestamp"]!.GetValue<string>().Should().Be("2026-10-17T20:00:00.0000000+00:00");
    }

    [Fact]
    public void EpisodioEmitido_Varios_TituloConElRango()
        => Embed(EmbedWebhook.EpisodioEmitido(1, "Frieren", Portada, 14, 16, Ahora))["title"]!.GetValue<string>()
            .Should().Be("Frieren · " + string.Format(LocalizationService.T("Webhook_EpisodiosFormato"), 14, 16));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Cache\101.jpg")]
    [InlineData("http://inseguro/portada.jpg")]
    public void SinPortadaHttps_SinMiniatura(string? portada)
        => Embed(EmbedWebhook.EpisodioEmitido(1, "Frieren", portada, 15, 15, Ahora)).AsObject().ContainsKey("thumbnail").Should().BeFalse();

    [Fact]
    public void SinAnime_SinEnlace()
        => Embed(EmbedWebhook.EpisodioEmitido(0, "Frieren", Portada, 15, 15, Ahora)).AsObject().ContainsKey("url").Should().BeFalse();

    [Fact]
    public void TituloLargoYCaracteresEspeciales_JsonValidoYRecortadoA256()
    {
        string titulo = "\"Comillas\" \\ barra <b> & " + new string('x', 300);

        var e = Embed(EmbedWebhook.EpisodioEmitido(1, titulo, Portada, 1, 1, Ahora));

        e["title"]!.GetValue<string>().Should().StartWith("\"Comillas\" \\ barra <b> & ").And.HaveLength(256);
    }

    // ---- Serie terminada ----

    [Fact]
    public void SerieTerminada_TituloDescripcionYColorVerde()
    {
        var e = Embed(EmbedWebhook.SerieTerminada(154587, "Frieren", Portada, 28, Ahora));

        e["title"]!.GetValue<string>().Should().Be(string.Format(LocalizationService.T("Webhook_TerminasteFormato"), "Frieren"));
        e["description"]!.GetValue<string>().Should().Be(string.Format(LocalizationService.T("Webhook_EpisodiosTotalFormato"), 28));
        e["color"]!.GetValue<int>().Should().Be(0x34D399);
        e["url"]!.GetValue<string>().Should().Be("https://anilist.co/anime/154587");
    }

    [Fact]
    public void Prueba_TituloDePrueba()
        => Embed(EmbedWebhook.Prueba(Ahora))["title"]!.GetValue<string>().Should().Be(LocalizationService.T("Webhook_PruebaTitulo"));

    // ---- URL ----

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc-DEF_9")]
    [InlineData("https://discordapp.com/api/webhooks/123/abc")]
    [InlineData("https://ptb.discord.com/api/webhooks/123/abc")]
    [InlineData("https://canary.discord.com/api/webhooks/123/abc")]
    [InlineData("  https://discord.com/api/webhooks/123/abc  ")]
    public void EsUrlValida_WebhooksDeDiscord(string url) => EmbedWebhook.EsUrlValida(url).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://discord.com/api/webhooks/123/abc")]
    [InlineData("https://discord.com.evil.net/api/webhooks/123/abc")]
    [InlineData("https://evil.net/discord.com/api/webhooks/123/abc")]
    [InlineData("https://discord.com/channels/123/456")]
    [InlineData("https://discord.com/api/webhooks/")]
    public void EsUrlValida_RechazaElResto(string? url) => EmbedWebhook.EsUrlValida(url).Should().BeFalse();

    // ---- Almacén cifrado ----

    [Fact]
    public void Almacen_GuardaCifradoYLeeLaMismaUrl()
    {
        string carpeta = Path.Combine(Path.GetTempPath(), "webhook-pruebas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        try
        {
            string ruta = Path.Combine(carpeta, "discord-webhook.bin");
            const string url = "https://discord.com/api/webhooks/123/secreto";
            var almacen = new AlmacenWebhook(ruta);

            almacen.Guardar(url);

            almacen.Leer().Should().Be(url);
            Encoding.UTF8.GetString(File.ReadAllBytes(ruta)).Should().NotContain("secreto");
            new AlmacenWebhook(ruta).Leer().Should().Be(url);
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public void Almacen_GuardarVacia_BorraLaUrl()
    {
        string carpeta = Path.Combine(Path.GetTempPath(), "webhook-pruebas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        try
        {
            string ruta = Path.Combine(carpeta, "discord-webhook.bin");
            var almacen = new AlmacenWebhook(ruta);
            almacen.Guardar("https://discord.com/api/webhooks/123/abc");

            almacen.Guardar("  ");

            almacen.Leer().Should().BeNull();
            File.Exists(ruta).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public void Almacen_SinArchivo_DevuelveNull()
        => new AlmacenWebhook(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N"), "x.bin")).Leer().Should().BeNull();
}
