using System;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquesta el seek nativo contra el <see cref="Player"/> de Flyleaf: coalescing "último-gana"
/// (enviar CurTime más rápido de lo que Flyleaf procesa los seeks los encola y pueden aplicarse
/// fuera de orden) y el diferido-hasta-que-el-Player-esté-listo (aplicar CurTime durante la
/// apertura interrumpe la creación del contexto de video y deja la pantalla en negro).
/// </summary>
public interface IPlaybackSeekCoordinator : IDisposable
{
    /// <summary>True durante la ventana de "settle" tras un seek — el bucle de tracking no debe
    /// repintar la posición en este lapso (evita que la barra de progreso "rebote").</summary>
    bool EnVentanaDeSettle { get; }

    void IniciarVentanaDeSettle();

    /// <summary>
    /// Coalescing "último-gana": aplica de inmediato si pasó el intervalo mínimo, si no programa
    /// el objetivo más reciente para cuando venza. <paramref name="haCompletadoOpen"/> es un
    /// delegado (no un bool) a propósito: el seek diferido por el debounce debe leer el estado
    /// VIGENTE del Player al momento de aplicarse, no un valor capturado cuando se pidió el seek.
    /// No-op si <paramref name="player"/> es null o ya se liberó.
    /// </summary>
    void SolicitarSeek(Player? player, double segundos, Func<bool> haCompletadoOpen);

    /// <summary>Entrega y limpia el seek que quedó diferido porque el Player no estaba listo
    /// cuando se pidió. Null si no hay ninguno pendiente.</summary>
    double? ConsumirSeekPendienteAlAbrir();

    /// <summary>Nuevo episodio: cancela el coalescing en curso y limpia el seek diferido-al-abrir.</summary>
    void Reiniciar();

    /// <summary>Solo cancela el coalescing en curso (p. ej. al liberar la sesión de reproducción).</summary>
    void CancelarPendiente();
}
