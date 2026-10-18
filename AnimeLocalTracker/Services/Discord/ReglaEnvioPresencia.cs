using System;

namespace AnimeLocalTracker.Services.Discord;

/// <summary>
/// Cuándo merece la pena volver a mandar el estado a Discord. El reproductor informa cada 250 ms; solo se reenvía si cambia
/// lo que Discord enseña: otro episodio, pausa/reanudación, la duración que llega tarde o un salto en el video (la posición
/// se aparta de la que Discord está calculando por su cuenta).
/// </summary>
internal static class ReglaEnvioPresencia
{
    /// <summary>Discord admite unas 5 actualizaciones cada 20 s; con una cada 5 s nunca se llega al límite.</summary>
    public static readonly TimeSpan IntervaloMinimo = TimeSpan.FromSeconds(5);

    public const double DesvioMaximoSeg = 3;

    public static bool HayCambio(EstadoPresencia? enviado, DateTimeOffset enviadoEn, EstadoPresencia nuevo, DateTimeOffset ahora)
    {
        if (enviado is null) return true;
        if (enviado.AnimeId != nuevo.AnimeId || enviado.Episodio != nuevo.Episodio || enviado.Reproduciendo != nuevo.Reproduciendo) return true;
        if (Math.Abs(enviado.DuracionSeg - nuevo.DuracionSeg) > 1) return true;
        if (!nuevo.Reproduciendo) return false;

        // ponytail: supone velocidad 1×; a otra velocidad el desvío crece y se reenvía como mucho cada IntervaloMinimo.
        double esperada = enviado.PosicionSeg + (ahora - enviadoEn).TotalSeconds;
        return Math.Abs(nuevo.PosicionSeg - esperada) > DesvioMaximoSeg;
    }
}
