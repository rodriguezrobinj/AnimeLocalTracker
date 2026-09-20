using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Services.Logros;

public enum CategoriaLogro
{
    Maraton,
    Constancia,
    Coleccion,
    Generos,
    Horarios,
    Epocas,
    Secretos
}

/// <summary>Dificultad de un nivel dentro de una familia de logros (de menor a mayor).</summary>
public enum NivelLogro
{
    Bronce = 1,
    Plata = 2,
    Oro = 3,
    Platino = 4,
    Diamante = 5
}

/// <summary>
/// Una familia de logros: una métrica medible y hasta cinco umbrales ascendentes (uno por nivel).
/// Los textos viven en LocalizationService con las claves <c>Logro_{Id}_Titulo</c> y
/// <c>Logro_{Id}_Desc</c> (la descripción recibe el umbral del siguiente nivel como {0}).
/// </summary>
public sealed record LogroDefinicion(
    string Id,
    CategoriaLogro Categoria,
    string Icono,
    string Color,
    IReadOnlyList<double> Umbrales,
    bool Secreto = false)
{
    public int NivelesTotales => Umbrales.Count;
    public string ClaveTitulo => $"Logro_{Id}_Titulo";
    public string ClaveDescripcion => $"Logro_{Id}_Desc";
}

/// <summary>Puntos por nivel: el rango del usuario sale de sumarlos.</summary>
public static class Puntaje
{
    private static readonly int[] PuntosPorNivel = { 0, 1, 2, 3, 5, 8 };

    public static int DeNivel(int nivel) => PuntosPorNivel[Math.Clamp(nivel, 0, 5)];

    /// <summary>Puntos de tener el nivel N (suma de todos los niveles hasta N).</summary>
    public static int Acumulado(int nivel)
    {
        int total = 0;
        for (int n = 1; n <= Math.Clamp(nivel, 0, 5); n++) total += PuntosPorNivel[n];
        return total;
    }
}

/// <summary>Resultado de evaluar una familia contra los datos del usuario.</summary>
public sealed class LogroEstado
{
    public LogroDefinicion Definicion { get; }
    public double Valor { get; }
    public int NivelActual { get; }
    public DateTime? FechaUltimoNivelUtc { get; }

    public LogroEstado(LogroDefinicion definicion, double valor, int nivelActual, DateTime? fechaUltimoNivelUtc)
    {
        Definicion = definicion;
        Valor = valor;
        NivelActual = Math.Clamp(nivelActual, 0, definicion.NivelesTotales);
        FechaUltimoNivelUtc = fechaUltimoNivelUtc;
    }

    public string Id => Definicion.Id;
    public bool Desbloqueado => NivelActual > 0;
    public bool Completo => NivelActual >= Definicion.NivelesTotales;

    /// <summary>Los secretos no revelan nombre ni descripción hasta conseguir su primer nivel.</summary>
    public bool Oculto => Definicion.Secreto && !Desbloqueado;

    public double? SiguienteUmbral => Completo ? null : Definicion.Umbrales[NivelActual];

    /// <summary>Avance hacia el siguiente nivel, de 0 a 1 (1 si ya está completo).</summary>
    public double Progreso => Completo ? 1.0 : Math.Clamp(Valor / Definicion.Umbrales[NivelActual], 0, 1);

    public int Puntos => Puntaje.Acumulado(NivelActual);
    public int PuntosMaximos => Puntaje.Acumulado(Definicion.NivelesTotales);
}

/// <summary>Rango del usuario según sus puntos totales (índice 0 = Novato … 5 = Leyenda).</summary>
public sealed record RangoOtaku(int Indice, int PuntosMinimos, int? PuntosSiguiente)
{
    public string ClaveNombre => $"Logro_Rango_{Indice}";

    /// <summary>Avance hacia el siguiente rango, de 0 a 1 (1 en el rango máximo).</summary>
    public double ProgresoHaciaSiguiente(int puntos) =>
        PuntosSiguiente is int siguiente
            ? Math.Clamp((puntos - PuntosMinimos) / (double)(siguiente - PuntosMinimos), 0, 1)
            : 1.0;
}

public static class Rangos
{
    private static readonly int[] Minimos = { 0, 12, 40, 90, 160, 240 };

    public static RangoOtaku Para(int puntos)
    {
        int indice = 0;
        for (int i = 0; i < Minimos.Length; i++)
        {
            if (puntos >= Minimos[i]) indice = i;
        }

        int? siguiente = indice + 1 < Minimos.Length ? Minimos[indice + 1] : null;
        return new RangoOtaku(indice, Minimos[indice], siguiente);
    }
}

public sealed class ResumenLogros
{
    public IReadOnlyList<LogroEstado> Logros { get; }
    public int NivelesDesbloqueados { get; }
    public int NivelesTotales { get; }
    public int Puntos { get; }
    public int PuntosMaximos { get; }
    public RangoOtaku Rango { get; }

    /// <summary>Cuántos niveles se han conseguido de cada dificultad (Bronce…Diamante).</summary>
    public IReadOnlyDictionary<NivelLogro, int> PorNivel { get; }

    /// <summary>Los logros visibles y no completados más cercanos al siguiente nivel.</summary>
    public IReadOnlyList<LogroEstado> Proximos { get; }

    public static ResumenLogros Vacio { get; } = new(Array.Empty<LogroEstado>());

    public ResumenLogros(IReadOnlyList<LogroEstado> logros)
    {
        Logros = logros;
        NivelesDesbloqueados = logros.Sum(l => l.NivelActual);
        NivelesTotales = logros.Sum(l => l.Definicion.NivelesTotales);
        Puntos = logros.Sum(l => l.Puntos);
        PuntosMaximos = logros.Sum(l => l.PuntosMaximos);
        Rango = Rangos.Para(Puntos);

        var porNivel = Enum.GetValues<NivelLogro>().ToDictionary(n => n, _ => 0);
        foreach (var logro in logros)
        {
            for (int nivel = 1; nivel <= logro.NivelActual; nivel++) porNivel[(NivelLogro)nivel]++;
        }
        PorNivel = porNivel;

        Proximos = logros
            .Where(l => !l.Completo && !l.Oculto)
            .OrderByDescending(l => l.Progreso)
            .Take(3)
            .ToList();
    }
}
