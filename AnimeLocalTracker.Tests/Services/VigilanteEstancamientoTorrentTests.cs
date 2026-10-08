using System;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Regresión: un torrent sin nadie compartiéndolo esperaba el 100 % para siempre y dejaba
/// ocupado uno de los huecos de descarga de la app.
/// </summary>
public class VigilanteEstancamientoTorrentTests
{
    private static readonly DateTime Inicio = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Registrar_ElMotivoSaleEnElIdiomaDeLaApp()
    {
        try
        {
            LocalizationService.Instance.Idioma = "en";

            new VigilanteEstancamientoTorrent(0, Inicio).Registrar(0, Inicio + VigilanteEstancamientoTorrent.MaximoSinPrimerDato)
                .Should().Be("Nobody is sharing this torrent (no data in 5 min).");

            var conDatos = new VigilanteEstancamientoTorrent(0, Inicio);
            conDatos.Registrar(10, Inicio.AddMinutes(1));
            conDatos.Registrar(10, Inicio.AddMinutes(1) + VigilanteEstancamientoTorrent.MaximoSinAvance)
                .Should().Be("The torrent stopped progressing (10 min without receiving data).");
        }
        finally
        {
            LocalizationService.Instance.Idioma = "es";
        }
    }

    [Fact]
    public void Registrar_SinRecibirNadaDurante5Minutos_LoDaPorEstancado()
    {
        var vigilante = new VigilanteEstancamientoTorrent(0, Inicio);

        vigilante.Registrar(0, Inicio.AddMinutes(4)).Should().BeNull();
        vigilante.Registrar(0, Inicio + VigilanteEstancamientoTorrent.MaximoSinPrimerDato).Should().NotBeNull();
    }

    [Fact]
    public void Registrar_ConAvanceReciente_NoLoDaPorEstancado()
    {
        var vigilante = new VigilanteEstancamientoTorrent(0, Inicio);

        vigilante.Registrar(10, Inicio.AddMinutes(4)).Should().BeNull();
        // Ya llegaban datos: se le da más margen que a uno que nunca empezó.
        vigilante.Registrar(10, Inicio.AddMinutes(4 + 9)).Should().BeNull();
        vigilante.Registrar(10.5, Inicio.AddMinutes(4 + 13)).Should().BeNull();
    }

    [Fact]
    public void Registrar_SiDejaDeAvanzarTrasEmpezar_LoDaPorEstancadoTras10Minutos()
    {
        var vigilante = new VigilanteEstancamientoTorrent(0, Inicio);
        vigilante.Registrar(40, Inicio.AddMinutes(1));

        vigilante.Registrar(40, Inicio.AddMinutes(1) + VigilanteEstancamientoTorrent.MaximoSinAvance).Should().NotBeNull();
    }

    [Fact]
    public void Registrar_AlReanudarConPiezasPrevias_NoCuentaElProgresoInicialComoAvance()
    {
        // Reanudar un torrent al 60 %: ese 60 % ya estaba, no demuestra que haya fuentes ahora.
        var vigilante = new VigilanteEstancamientoTorrent(60, Inicio);

        vigilante.Registrar(60, Inicio + VigilanteEstancamientoTorrent.MaximoSinPrimerDato).Should().NotBeNull();
    }
}
