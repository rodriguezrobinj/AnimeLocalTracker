using System;
using System.Collections.Generic;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Decide cuántos trozos consecutivos se piden en UNA petición. MP4Upload responde
/// <c>Connection: close</c>: cada petición abre una conexión nueva y paga de nuevo la negociación
/// segura. Medido en uso real: ~0,8 s en a4.mp4upload.com (despreciable) pero 14–26 s en
/// a3.mp4upload.com, donde un trozo de 4 MB a ~170 KB/s tarda ~24 s → casi la mitad del tiempo se
/// iba en conectar. Si el servidor tarda en responder, se piden 2–3 trozos juntos (menos esperas);
/// si responde rápido, uno solo (el reparto entre conexiones es más fino y el final no se alarga).
/// Lógica pura para poder testearla sin red.
/// </summary>
internal sealed class AgrupadorTrozos
{
    internal const int MaxTrozosPorPeticion = 3;

    private readonly TimeSpan _umbralLento;
    private readonly object _lock = new();
    private double? _latenciaMediaSegundos;

    public AgrupadorTrozos(TimeSpan umbralLento) => _umbralLento = umbralLento;

    public TimeSpan? LatenciaMedia
    {
        get { lock (_lock) return _latenciaMediaSegundos is double s ? TimeSpan.FromSeconds(s) : null; }
    }

    /// <summary>Tiempo que tardó el servidor en contestar (conectar + negociar + primeras cabeceras).</summary>
    public void RegistrarLatencia(TimeSpan latencia)
    {
        lock (_lock)
        {
            double s = latencia.TotalSeconds;
            _latenciaMediaSegundos = _latenciaMediaSegundos is double previa ? 0.7 * previa + 0.3 * s : s;
        }
    }

    /// <summary>
    /// Trozos a pedir juntos. Con el servidor lento, 2 (o 3 si es muy lento); pero nunca tantos que
    /// alguna conexión se quede sin trabajo al final (<paramref name="restantes"/> repartidos entre
    /// <paramref name="conexiones"/>).
    /// </summary>
    public int TrozosPorPeticion(int restantes, int conexiones)
    {
        var latencia = LatenciaMedia;
        if (latencia is not TimeSpan l || l < _umbralLento) return 1;

        int k = l >= _umbralLento * 2 ? MaxTrozosPorPeticion : 2;
        while (k > 1 && restantes < Math.Max(1, conexiones) * k) k--;
        return k;
    }

    /// <summary>
    /// Parte los trozos [primero..ultimo] en tramos que se pueden pedir con un solo rango: se saltan
    /// los ya completos (al reanudar) y un trozo a medias solo puede ir al principio de un tramo
    /// (los siguientes deben estar sin empezar, para que el rango sea continuo desde su byte actual).
    /// </summary>
    public static List<List<SegmentState>> ArmarTramos(IReadOnlyList<SegmentState> trozos, int primero, int ultimo)
    {
        var tramos = new List<List<SegmentState>>();
        List<SegmentState>? actual = null;
        for (int i = primero; i <= ultimo && i < trozos.Count; i++)
        {
            var trozo = trozos[i];
            if (trozo.CurrentOffset > trozo.End)
            {
                actual = null; // completo: corta el tramo
                continue;
            }

            bool empezado = trozo.CurrentOffset != trozo.Start;
            if (actual == null || empezado)
            {
                actual = new List<SegmentState>();
                tramos.Add(actual);
            }
            actual.Add(trozo);
        }
        return tramos;
    }
}
