using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>Un día de la cuadrícula de constancia. <paramref name="Nivel"/> −1 = fuera del rango (futuro): no se pinta.</summary>
public sealed record DiaCalor(DateTime Fecha, int Episodios, int Nivel, string Texto)
{
    /// <summary>Un lector de pantalla nombra cada celda con el ToString del dato (no con el de su Border): leería el volcado del record.</summary>
    public override string ToString() => Texto;
}

/// <summary>
/// La cuadrícula ya lista para pintar: <see cref="Celdas"/> fila a fila (todas las semanas del primer día de la semana,
/// luego las del segundo…), que es el orden en que las coloca un UniformGrid de 7 filas.
/// </summary>
public sealed record MapaCalor(IReadOnlyList<DiaCalor> Celdas, IReadOnlyList<string> Meses, IReadOnlyList<string> Dias,
    int TotalEpisodios, int DiasActivos);

/// <summary>
/// Mapa de calor de las últimas 53 semanas (docs/investigacion-mapa-calor-visionado.md). Cálculo puro: recibe los días
/// LOCALES de cada episodio visto (uno por episodio) y la cultura, que da el primer día de la semana y los nombres.
/// </summary>
public static class CalculadorMapaCalor
{
    public const int Semanas = 53;

    /// <summary>Niveles fijos y no relativos al máximo: "7 o más" es siempre una maratón de unas 3 h.</summary>
    public static int NivelDe(int episodios) => episodios switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 6 => 3,
        _ => 4
    };

    /// <summary>Primer día de la primera columna: la semana de hoy es la última de las <see cref="Semanas"/>.</summary>
    public static DateTime InicioDelRango(DateTime hoy, DayOfWeek primerDia)
    {
        var inicioSemanaHoy = hoy.Date.AddDays(-(((int)hoy.DayOfWeek - (int)primerDia + 7) % 7));
        return inicioSemanaHoy.AddDays(-7 * (Semanas - 1));
    }

    public static MapaCalor Calcular(IEnumerable<DateTime> diasLocales, DateTime hoy, CultureInfo cultura)
    {
        hoy = hoy.Date;
        var formato = cultura.DateTimeFormat;
        var inicio = InicioDelRango(hoy, formato.FirstDayOfWeek);

        // Lo anterior al rango y lo posterior a hoy (reloj cambiado, viaje entre zonas) no entra.
        var porDia = diasLocales
            .Select(d => d.Date)
            .Where(d => d >= inicio && d <= hoy)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var celdas = new DiaCalor[7 * Semanas];
        for (int fila = 0; fila < 7; fila++)
        {
            for (int columna = 0; columna < Semanas; columna++)
            {
                var fecha = inicio.AddDays(columna * 7 + fila);
                int episodios = porDia.GetValueOrDefault(fecha);
                celdas[fila * Semanas + columna] = fecha > hoy
                    ? new DiaCalor(fecha, 0, -1, string.Empty)
                    : new DiaCalor(fecha, episodios, NivelDe(episodios), TextoDe(episodios, fecha, cultura));
            }
        }

        // Cada mes se etiqueta en la semana que contiene su día 1, si ese día ya llegó.
        var meses = new string[Semanas];
        for (int columna = 0; columna < Semanas; columna++)
        {
            var semana = inicio.AddDays(columna * 7);
            var diaUno = Enumerable.Range(0, 7).Select(i => semana.AddDays(i)).FirstOrDefault(d => d.Day == 1 && d <= hoy);
            meses[columna] = diaUno == default ? string.Empty : formato.GetAbbreviatedMonthName(diaUno.Month).TrimEnd('.');
        }

        // Como GitHub: solo lunes, miércoles y viernes llevan nombre, empiece la semana en lunes o en domingo.
        var dias = new string[7];
        for (int fila = 0; fila < 7; fila++)
        {
            var dia = (DayOfWeek)(((int)formato.FirstDayOfWeek + fila) % 7);
            dias[fila] = dia is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday
                ? formato.GetAbbreviatedDayName(dia).TrimEnd('.')
                : string.Empty;
        }

        return new MapaCalor(celdas, meses, dias, porDia.Values.Sum(), porDia.Count);
    }

    private static string TextoDe(int episodios, DateTime fecha, CultureInfo cultura)
    {
        string dia = fecha.ToString("D", cultura);
        return episodios switch
        {
            0 => string.Format(LocalizationService.T("Stats_MapaDiaNingunoFormato"), dia),
            1 => string.Format(LocalizationService.T("Stats_MapaDiaUnoFormato"), dia),
            _ => string.Format(LocalizationService.T("Stats_MapaDiaVariosFormato"), episodios, dia)
        };
    }
}
