using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Registro por sesión + errores.log, contexto de DEBUG ante avisos, resumen de repetidos y limpieza.
/// Escribe en la carpeta temporal de TestInitializer (ANIMELOCALTRACKER_LOG_DIR), nunca en la del usuario.
/// Cada test usa una fuente única para no depender de lo que registren otros tests en paralelo.
/// </summary>
public class AppLoggerTests
{
    private static string Fuente() => "Test" + Guid.NewGuid().ToString("N")[..8];

    private static string LeerSesion()
    {
        AppLogger.Flush();
        return File.ReadAllText(AppLogger.RutaSesion);
    }

    private static string LeerErrores()
    {
        AppLogger.Flush();
        string ruta = Path.Combine(AppLogger.Carpeta, "errores.log");
        return File.Exists(ruta) ? File.ReadAllText(ruta) : "";
    }

    [Fact]
    public void RutaSesion_EsUnArchivoPorSesionDentroDeSesiones()
    {
        AppLogger.RutaSesion.Should().StartWith(Path.Combine(AppLogger.Carpeta, "sesiones"));
        Path.GetFileNameWithoutExtension(AppLogger.RutaSesion).Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$");
    }

    [Fact]
    public void Info_VaAlArchivoDeLaSesion_PeroNoAErrores()
    {
        string fuente = Fuente();
        AppLogger.Info(fuente, "mensaje informativo");

        LeerSesion().Should().Contain($"INFO  [{fuente}] mensaje informativo");
        LeerErrores().Should().NotContain(fuente);
    }

    [Fact]
    public void Error_VaALaSesionYAErrores_ConLaTrazaSangrada()
    {
        string fuente = Fuente();
        AppLogger.Error(fuente, "algo falló", new InvalidOperationException("detalle interno"));

        LeerSesion().Should().Contain($"ERROR [{fuente}] algo falló");
        var errores = LeerErrores();
        errores.Should().MatchRegex($@"\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}} ERROR \[{fuente}\] algo falló");
        errores.Should().Contain("    System.InvalidOperationException: detalle interno");
    }

    [Fact]
    public void Debug_SinRegistroDetallado_SoloSeEscribeComoContextoDeUnAviso()
    {
        AppLogger.RegistroDetallado.Should().BeFalse("en los tests no se activa");
        string fuente = Fuente();

        AppLogger.Debug(fuente, "paso previo 1");
        LeerSesion().Should().NotContain("paso previo 1", "sin registro detallado no se escribe en el momento");

        AppLogger.Warn(fuente, "no se encontró el episodio");
        var sesion = LeerSesion();
        sesion.Should().Contain($"DEBUG · [{fuente}] paso previo 1", "se escribe como contexto del aviso");
        sesion.IndexOf("paso previo 1", StringComparison.Ordinal).Should().BeLessThan(sesion.IndexOf("no se encontró el episodio", StringComparison.Ordinal));
        LeerErrores().Should().NotContain("paso previo 1", "errores.log solo lleva avisos y errores");
    }

    [Fact]
    public void MensajesRepetidos_SeResumenTrasCinco()
    {
        string fuente = Fuente();
        for (int i = 0; i < 12; i++) AppLogger.Info(fuente, $"Reintento {i} en {i * 2} s (Host desconocido)");

        var lineas = LeerSesion().Split('\n').Where(l => l.Contains($"[{fuente}]")).ToList();
        lineas.Count(l => l.Contains("Reintento")).Should().Be(AppLogger.MaxRepeticionesPorMinuto + 1, "5 escritas + el resumen");
        lineas.Should().Contain(l => l.Contains("repetido 7 veces más en el último minuto"));
    }

    [Fact]
    public void Plantilla_IgnoraLosNumeros()
    {
        AppLogger.Plantilla("Reintento 3 en 2 s (api:443)").Should().Be(AppLogger.Plantilla("Reintento 14 en 16 s (api:443)"));
        AppLogger.Plantilla("Episodio 12.5").Should().Be("Episodio #.#");
    }

    [Fact]
    public void FormatoSesion_HoraConMilisegundosYNivelAlineado()
    {
        var e = new LogEntry(new DateTime(2026, 9, 30, 0, 9, 0, 123), "WARN", "Fuente", "texto");

        AppLogger.FormatoSesion(e).TrimEnd().Should().Be("00:09:00.123 WARN  [Fuente] texto");
        AppLogger.FormatoErrores(e).TrimEnd().Should().Be("2026-09-30 00:09:00.123 WARN  [Fuente] texto");
    }

    [Fact]
    public void SesionesABorrar_ConservaLasUltimasYNuncaLaActual()
    {
        var ahora = DateTime.UtcNow;
        var archivos = Enumerable.Range(0, AppLogger.MaxSesionesConservadas + 5)
            .Select(i => ($"s{i}.log", 1000L, ahora.AddMinutes(-i)))
            .ToList();

        var borrar = AppLogger.SesionesABorrar(archivos, "s0.log");

        borrar.Should().HaveCount(5);
        borrar.Should().NotContain("s0.log");
        borrar.Should().BeEquivalentTo(Enumerable.Range(AppLogger.MaxSesionesConservadas, 5).Select(i => $"s{i}.log"));
    }

    [Fact]
    public void SesionesABorrar_RespetaElTamanoTotal()
    {
        var ahora = DateTime.UtcNow;
        long grande = AppLogger.MaxBytesSesiones / 2 + 1;
        var archivos = new List<(string, long, DateTime)>
        {
            ("actual.log", 10, ahora),
            ("ayer.log", grande, ahora.AddDays(-1)),
            ("antier.log", grande, ahora.AddDays(-2)),
        };

        AppLogger.SesionesABorrar(archivos, "actual.log").Should().Equal("antier.log");
    }

    [Fact]
    public void Sanitizar_DeberiaReemplazarLaCarpetaDeDatosYElPerfil()
    {
        // Arrange: rutas reales del entorno de ejecución
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(localApp) || string.IsNullOrEmpty(perfil)) return;

        // Act
        string resultado = AppLogger.Sanitizar($"{localApp}\\AnimeLocalTrackerData\\Backups\\x.db y {perfil}\\Desktop\\f.mkv");

        // Assert (SEC-12): sin rutas completas del usuario en el log
        resultado.Should().Be(@"<datos>\AnimeLocalTrackerData\Backups\x.db y <perfil>\Desktop\f.mkv");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitizar_ConTextoVacio_DeberiaDevolverVacio(string? texto)
    {
        AppLogger.Sanitizar(texto).Should().BeEmpty();
    }

    [Fact]
    public void Sanitizar_SinRutasConocidas_DeberiaDejarElTextoIntacto()
    {
        AppLogger.Sanitizar("Sincronizado AniListId=123 hasta episodio 5.").Should()
            .Be("Sincronizado AniListId=123 hasta episodio 5.");
    }
}
