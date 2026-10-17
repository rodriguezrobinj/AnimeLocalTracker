using System;
using System.Globalization;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>La cuadrícula de constancia de Estadísticas: 53 semanas que terminan hoy, fila a fila, con niveles fijos.</summary>
public class CalculadorMapaCalorTests
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // Miércoles 14 de octubre de 2026.
    private static readonly DateTime Hoy = new(2026, 10, 14);

    private static DiaCalor Celda(MapaCalor mapa, DateTime fecha) => mapa.Celdas.Single(c => c.Fecha == fecha);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(30, 4)]
    public void NivelDe_UsaLosUmbralesFijos(int episodios, int nivel) =>
        CalculadorMapaCalor.NivelDe(episodios).Should().Be(nivel);

    [Fact]
    public void Celdas_SonSieteFilasDeCincuentaYTresSemanas_FilaAFila()
    {
        var mapa = CalculadorMapaCalor.Calcular([], Hoy, Es);
        var inicio = CalculadorMapaCalor.InicioDelRango(Hoy, DayOfWeek.Monday);

        mapa.Celdas.Should().HaveCount(7 * CalculadorMapaCalor.Semanas);
        mapa.Meses.Should().HaveCount(CalculadorMapaCalor.Semanas);
        mapa.Dias.Should().HaveCount(7);
        for (int fila = 0; fila < 7; fila++)
            for (int columna = 0; columna < CalculadorMapaCalor.Semanas; columna++)
                mapa.Celdas[fila * CalculadorMapaCalor.Semanas + columna].Fecha.Should().Be(inicio.AddDays(columna * 7 + fila));
    }

    [Theory]
    [InlineData("es-ES", 2026, 10, 12, DayOfWeek.Monday)] // hoy es lunes: primer día de la semana en español
    [InlineData("en-US", 2026, 10, 11, DayOfWeek.Sunday)] // hoy es domingo: primer día de la semana en inglés
    [InlineData("es-ES", 2026, 10, 14, DayOfWeek.Monday)]
    [InlineData("en-US", 2026, 10, 14, DayOfWeek.Sunday)]
    public void Hoy_EstaEnLaUltimaColumna_EnLaFilaDeSuDia_YLoQueSigueNoSePinta(string cultura, int a, int m, int d, DayOfWeek primerDia)
    {
        var hoy = new DateTime(a, m, d);
        var mapa = CalculadorMapaCalor.Calcular([], hoy, CultureInfo.GetCultureInfo(cultura));

        int indice = mapa.Celdas.ToList().FindIndex(c => c.Fecha == hoy);
        (indice % CalculadorMapaCalor.Semanas).Should().Be(CalculadorMapaCalor.Semanas - 1, "hoy va en la última semana");
        (indice / CalculadorMapaCalor.Semanas).Should().Be(((int)hoy.DayOfWeek - (int)primerDia + 7) % 7);
        mapa.Celdas.Where(c => c.Fecha > hoy).Should().OnlyContain(c => c.Nivel == -1 && c.Texto == "");
        mapa.Celdas.Where(c => c.Fecha <= hoy).Should().OnlyContain(c => c.Nivel >= 0 && c.Texto != "");
        mapa.Celdas[0].Fecha.DayOfWeek.Should().Be(primerDia);
    }

    [Fact]
    public void CuentaLosEpisodiosDeCadaDia_YSoloLosDelRango()
    {
        var inicio = CalculadorMapaCalor.InicioDelRango(Hoy, DayOfWeek.Monday);
        DateTime[] dias =
        [
            Hoy, Hoy, Hoy,                 // 3 hoy
            inicio,                        // 1 el primer día del rango
            inicio.AddDays(-1),            // fuera: anterior al rango
            Hoy.AddDays(1),                // fuera: fecha futura (reloj cambiado)
            Hoy.AddHours(23).AddMinutes(30) // se toma solo la fecha: cuenta hoy
        ];

        var mapa = CalculadorMapaCalor.Calcular(dias, Hoy, Es);

        Celda(mapa, Hoy).Episodios.Should().Be(4);
        Celda(mapa, Hoy).Nivel.Should().Be(3);
        Celda(mapa, inicio).Episodios.Should().Be(1);
        mapa.TotalEpisodios.Should().Be(5);
        mapa.DiasActivos.Should().Be(2);
    }

    [Fact]
    public void Texto_LlevaElNumeroYLaFechaLargaDeLaCultura()
    {
        var mapa = CalculadorMapaCalor.Calcular([Hoy, Hoy, Hoy], Hoy, En);

        Celda(mapa, Hoy).Texto.Should().Contain("3").And.Contain(Hoy.ToString("D", En));
        Celda(mapa, Hoy.AddDays(-1)).Texto.Should().Contain(Hoy.AddDays(-1).ToString("D", En));
    }

    [Fact]
    public void Meses_SeEtiquetanEnLaSemanaDondeEmpiezan_YNoSiElDiaUnoEsFuturo()
    {
        // Miércoles 28 de octubre de 2026: su semana (lunes 26 – domingo 1) contiene el 1 de noviembre, que aún no llegó.
        var hoy = new DateTime(2026, 10, 28);
        var mapa = CalculadorMapaCalor.Calcular([], hoy, Es);
        var inicio = CalculadorMapaCalor.InicioDelRango(hoy, DayOfWeek.Monday);

        int columnaOctubre = (int)((new DateTime(2026, 10, 1) - inicio).TotalDays / 7);
        mapa.Meses[columnaOctubre].Should().Be(Es.DateTimeFormat.GetAbbreviatedMonthName(10).TrimEnd('.'));
        mapa.Meses[CalculadorMapaCalor.Semanas - 1].Should().BeEmpty("el 1 de noviembre es futuro");
        mapa.Meses.Count(m => m != "").Should().BeInRange(11, 12);
    }

    [Theory]
    [InlineData("es-ES", 0, 2, 4)] // lun, mié, vie
    [InlineData("en-US", 1, 3, 5)] // Mon, Wed, Fri
    public void Dias_SeEtiquetanLunesMiercolesYViernes(string nombreCultura, int a, int b, int c)
    {
        var cultura = CultureInfo.GetCultureInfo(nombreCultura);
        var mapa = CalculadorMapaCalor.Calcular([], Hoy, cultura);

        mapa.Dias.Select((t, i) => (t, i)).Where(x => x.t != "").Select(x => x.i).Should().Equal(a, b, c);
        mapa.Dias[a].Should().Be(cultura.DateTimeFormat.GetAbbreviatedDayName(DayOfWeek.Monday).TrimEnd('.'));
    }
}
