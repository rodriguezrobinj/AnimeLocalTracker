using System;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Ajuste automático de conexiones (fase 4): más conexiones solo si medir demuestra que la
/// descarga va más rápida; menos si el servidor pide calma.
/// </summary>
public class ControlConexionesDescargaTests
{
    private const int Inicio = 8;
    private const int Techo = 16;
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private const double MB = 1024 * 1024;

    private sealed class Reloj
    {
        public DateTime Ahora = T0;
        public DateTime Siguiente() => Ahora = Ahora.AddSeconds(4);
    }

    /// <summary>Calentamiento + las ventanas de referencia: tras esto el control acaba de probar +2.</summary>
    private static ControlConexionesDescarga HastaPrimeraPrueba(Reloj reloj, double velocidadReferencia = 10 * MB)
    {
        var control = new ControlConexionesDescarga();
        for (int i = 0; i < ControlConexionesDescarga.VentanasCalentamiento + ControlConexionesDescarga.VentanasPorMedicion; i++)
            control.EvaluarVentana(velocidadReferencia, Inicio, Techo, reloj.Siguiente());
        return control;
    }

    /// <summary>Ventana de asentamiento + las de comparación con las velocidades dadas.</summary>
    private static string? Comparar(ControlConexionesDescarga control, Reloj reloj, params double[] velocidades)
    {
        control.EvaluarVentana(0, Inicio, Techo, reloj.Siguiente()); // asentando: se descarta
        string? resultado = null;
        foreach (var v in velocidades) resultado = control.EvaluarVentana(v, Inicio, Techo, reloj.Siguiente());
        return resultado;
    }

    [Fact]
    public void Permitidas_AlEmpezar_EsElRepartoJusto()
    {
        new ControlConexionesDescarga().Permitidas(Inicio, Techo).Should().Be(Inicio);
    }

    [Fact]
    public void EvaluarVentana_SinMedidaDeReferenciaCompleta_NoPrueba()
    {
        var control = new ControlConexionesDescarga();
        var reloj = new Reloj();
        for (int i = 0; i < ControlConexionesDescarga.VentanasCalentamiento + ControlConexionesDescarga.VentanasPorMedicion - 1; i++)
            control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente());

        control.Permitidas(Inicio, Techo).Should().Be(Inicio);
    }

    [Fact]
    public void EvaluarVentana_ConElServidorAlTope_NoPruebaMasConexiones()
    {
        // Log real: con 4 episodios de a3 el servidor ya no aceptaba más; las pruebas daban "0,02 → 0,26 MB/s" (ruido).
        var control = new ControlConexionesDescarga();
        var reloj = new Reloj();
        for (int i = 0; i < 20; i++)
            control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente(), servidorAlTope: true);

        control.Permitidas(Inicio, Techo).Should().Be(Inicio);
    }

    [Theory]
    [InlineData(null, 3)]    // sin medidas aún
    [InlineData(0.8, 3)]     // a4.mp4upload.com: responde rápido
    [InlineData(7.0, 4)]     // 2 × 7 s = 14 s → 4 ventanas de 4 s
    [InlineData(20.0, 8)]    // a3.mp4upload.com: 2 × 20 s = 40 s → 10, con tope en 8
    public void VentanasParaLatencia_CubrenAlMenosElDobleDeLaEsperaDelServidor(double? latenciaSegundos, int esperadas)
    {
        TimeSpan? latencia = latenciaSegundos is double s ? TimeSpan.FromSeconds(s) : null;

        ControlConexionesDescarga.VentanasParaLatencia(latencia, TimeSpan.FromSeconds(4)).Should().Be(esperadas);
    }

    [Fact]
    public void EvaluarVentana_ConServidorLento_EsperaMasVentanasAntesDeProbar()
    {
        var control = new ControlConexionesDescarga();
        var reloj = new Reloj();
        for (int i = 0; i < ControlConexionesDescarga.VentanasCalentamiento + 5; i++)
            control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente(), ventanasPorMedicion: 6);
        control.Permitidas(Inicio, Techo).Should().Be(Inicio, "con 5 de 6 ventanas la referencia aún no está completa");

        control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente(), ventanasPorMedicion: 6);
        control.Permitidas(Inicio, Techo).Should().Be(Inicio + 2);
    }

    [Fact]
    public void EvaluarVentana_EnElTramoFinal_NoDecideNada()
    {
        // Log real: "1,55 → 0,00 MB/s" al acabar la descarga (o durante la espera de conexión en a3).
        var reloj = new Reloj();
        var control = HastaPrimeraPrueba(reloj);
        control.EvaluarVentana(0, Inicio, Techo, reloj.Siguiente()); // asentando

        for (int i = 0; i < 5; i++)
            control.EvaluarVentana(0, Inicio, Techo, reloj.Siguiente(), enFinal: true).Should().BeNull();

        control.Permitidas(Inicio, Techo).Should().Be(Inicio + 2, "las ventanas del final no cuentan como 'no aceleró'");
    }

    [Fact]
    public void EvaluarVentana_TrasMedirLaReferencia_PruebaDosConexionesMas()
    {
        var control = HastaPrimeraPrueba(new Reloj());

        control.Permitidas(Inicio, Techo).Should().Be(Inicio + ControlConexionesDescarga.PasoSondeo);
    }

    [Fact]
    public void EvaluarVentana_SiLaMediaSube_LasMantieneAunqueUnaVentanaSueltaBaje()
    {
        // Caso real: MP4Upload varía mucho. Una ventana suelta puede bajar aunque la media suba.
        var reloj = new Reloj();
        var control = HastaPrimeraPrueba(reloj);

        var resultado = Comparar(control, reloj, 9.5 * MB, 13 * MB, 13 * MB); // media 11,8 (+18 %)

        resultado.Should().Contain("aceleraron");
        control.Permitidas(Inicio, Techo).Should().Be(Inicio + 2);
    }

    [Fact]
    public void EvaluarVentana_TrasUnaMejora_VuelveAProbarSinEsperarOtraReferencia()
    {
        var reloj = new Reloj();
        var control = HastaPrimeraPrueba(reloj);
        Comparar(control, reloj, 12 * MB, 12 * MB, 12 * MB);

        control.EvaluarVentana(12 * MB, Inicio, Techo, reloj.Siguiente());

        control.Permitidas(Inicio, Techo).Should().Be(Inicio + 4);
    }

    [Fact]
    public void EvaluarVentana_SiLaMediaNoSube_LasQuitaYNoVuelveAProbarEnUnRato()
    {
        var reloj = new Reloj();
        var control = HastaPrimeraPrueba(reloj);

        var resultado = Comparar(control, reloj, 10 * MB, 10.5 * MB, 10.2 * MB); // +2 %: no vale

        resultado.Should().Contain("no aceleraron");
        control.Permitidas(Inicio, Techo).Should().Be(Inicio);

        for (int i = 0; i < 5; i++) control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente()); // en enfriamiento
        control.Permitidas(Inicio, Techo).Should().Be(Inicio);

        reloj.Ahora += ControlConexionesDescarga.Enfriamiento;
        control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente());
        control.Permitidas(Inicio, Techo).Should().Be(Inicio + 2, "pasado el enfriamiento vuelve a probar");
    }

    [Fact]
    public void EvaluarVentana_NuncaPasaDelTecho()
    {
        var control = new ControlConexionesDescarga();
        var reloj = new Reloj();
        double velocidad = 10 * MB;

        for (int i = 0; i < 80; i++)
        {
            velocidad *= 1.2; // siempre "más rápido" → siempre acepta
            control.EvaluarVentana(velocidad, Inicio, Techo, reloj.Siguiente());
        }

        control.Permitidas(Inicio, Techo).Should().Be(Techo);
    }

    [Fact]
    public void Reducir_AlPedirCalmaElServidor_BajaALaMitadYSeRecuperaPocoAPoco()
    {
        var control = new ControlConexionesDescarga();

        control.Reducir(Inicio, Techo, T0);
        control.Permitidas(Inicio, Techo).Should().Be(4);
        control.Reducir(Inicio, Techo, T0);
        control.Reducir(Inicio, Techo, T0);
        control.Permitidas(Inicio, Techo).Should().Be(ControlConexionesDescarga.Minimo);

        for (int i = 0; i < ControlConexionesDescarga.TrozosParaRecuperar; i++) control.RegistrarTrozoCompletado();
        control.Permitidas(Inicio, Techo).Should().Be(ControlConexionesDescarga.Minimo + 1);
    }

    [Fact]
    public void EvaluarVentana_TrasUnRecortePorSaturacion_NoPruebaMas()
    {
        var reloj = new Reloj();
        var control = new ControlConexionesDescarga();
        control.Reducir(Inicio, Techo, reloj.Ahora);
        reloj.Ahora += ControlConexionesDescarga.Enfriamiento;

        for (int i = 0; i < 10; i++) control.EvaluarVentana(10 * MB, Inicio, Techo, reloj.Siguiente());

        control.Permitidas(Inicio, Techo).Should().Be(4, "mientras se recupera del recorte no abre conexiones de más");
    }
}
