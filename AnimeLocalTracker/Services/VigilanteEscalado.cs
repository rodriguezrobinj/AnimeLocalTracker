using System;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Decide cuándo el escalado inteligente no da abasto: varias ventanas seguidas perdiendo fotogramas. Un salto o la compilación de los
/// shaders al abrir dan un tirón suelto, que no cuenta. Medido en una Intel HD 620: el nivel medio de Anime4K pierde 9 fotogramas por
/// segundo; el ligero, ninguno.
/// </summary>
public sealed class VigilanteEscalado
{
    public TimeSpan Ventana { get; init; } = TimeSpan.FromSeconds(2);
    public int PerdidosPorVentana { get; init; } = 3;
    public int VentanasSeguidas { get; init; } = 4;

    private TimeSpan _inicio;
    private long _perdidosAlInicio = -1;
    private int _malas;

    public void Reiniciar()
    {
        _perdidosAlInicio = -1;
        _malas = 0;
    }

    /// <param name="perdidos">El contador del motor (fotogramas perdidos desde que abrió el archivo).</param>
    /// <param name="ahora">Un reloj que solo avanza.</param>
    /// <returns>True cuando toca apagar el escalado.</returns>
    public bool Anotar(long perdidos, TimeSpan ahora)
    {
        if (_perdidosAlInicio < 0 || perdidos < _perdidosAlInicio) // primera muestra, o archivo nuevo (el contador volvió a cero)
        {
            (_inicio, _perdidosAlInicio, _malas) = (ahora, perdidos, 0);
            return false;
        }
        if (ahora - _inicio < Ventana) return false;

        _malas = perdidos - _perdidosAlInicio >= PerdidosPorVentana ? _malas + 1 : 0;
        (_inicio, _perdidosAlInicio) = (ahora, perdidos);
        return _malas >= VentanasSeguidas;
    }
}
