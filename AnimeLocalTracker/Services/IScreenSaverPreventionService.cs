namespace AnimeLocalTracker.Services;

/// <summary>Evita que Windows apague la pantalla o active el protector de pantalla mientras hay
/// un video reproduciéndose. Ver <see cref="ScreenSaverPreventionService"/>.</summary>
public interface IScreenSaverPreventionService
{
    /// <summary>Solicita a Windows mantener la pantalla encendida hasta la próxima llamada a <see cref="Desactivar"/>.</summary>
    void Activar();

    /// <summary>Devuelve el comportamiento normal de ahorro de energía (protector/apagado de pantalla).</summary>
    void Desactivar();
}
