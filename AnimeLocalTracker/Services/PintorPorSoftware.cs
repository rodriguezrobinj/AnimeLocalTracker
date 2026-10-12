using System;
using System.Runtime.InteropServices;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>
/// libmpv pintando por software en un búfer de memoria, sin OpenGL: el modo de compatibilidad para equipos donde el lienzo no
/// arranca o se ve mal (máquina virtual, escritorio remoto, controlador antiguo). No sabe nada de WPF: quien lo usa
/// (<c>Controls/SuperficieMpv</c>) le da el búfer de su imagen. Se pinta y se suelta desde un mismo hilo.
/// </summary>
internal sealed class PintorPorSoftware : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvisoDePintado(nint contexto);

    private readonly MotorMpv _motor;
    private readonly AvisoDePintado _aviso; // vivo mientras exista el pintor: libmpv guarda el puntero
    private nint _pintor;

    private PintorPorSoftware(MotorMpv motor, nint pintor, Action hayFotograma)
    {
        _motor = motor;
        _pintor = pintor;
        _aviso = _ => hayFotograma();
        LibMpv.mpv_render_context_set_update_callback(pintor, Marshal.GetFunctionPointerForDelegate(_aviso), 0);
    }

    /// <summary>Null si libmpv lo rechaza. Si lo acepta, el motor ya tiene dónde pintar y carga el archivo que estuviera esperando.</summary>
    /// <param name="hayFotograma">Llega desde un hilo de libmpv: solo puede pedir un pintado para después, no pintar ahí mismo.</param>
    public static PintorPorSoftware? Crear(MotorMpv motor, Action hayFotograma)
    {
        nint api = Marshal.StringToHGlobalAnsi("sw");
        try
        {
            int resultado = LibMpv.mpv_render_context_create(out nint pintor, motor.Manejador, [new() { Tipo = LibMpv.ParametroTipoApi, Datos = api }, default]);
            if (resultado < 0)
            {
                AppLogger.Warn("PintorPorSoftware", $"libmpv no pudo pintar por software: {LibMpv.TextoDeError(resultado)}.");
                return null;
            }
            var nuevo = new PintorPorSoftware(motor, pintor, hayFotograma);
            motor.PintorCreado();
            return nuevo;
        }
        finally
        {
            Marshal.FreeHGlobal(api);
        }
    }

    /// <summary>Pinta el fotograma del momento en un búfer de 4 bytes por píxel (azul, verde, rojo y uno sin usar: el Bgr32 de WPF).</summary>
    /// <returns>True si lo pintado es un fotograma nuevo.</returns>
    public unsafe bool Pintar(nint bufer, int ancho, int alto, int paso)
    {
        if (_pintor == 0) return false;

        bool hayFotogramaNuevo = (LibMpv.mpv_render_context_update(_pintor) & LibMpv.HayFotogramaNuevo) != 0;
        int* tamano = stackalloc int[2] { ancho, alto };
        nuint bytesPorFila = (nuint)paso;
        int bloquear = 0; // no parar el hilo de la interfaz esperando al momento exacto del fotograma
        fixed (byte* formato = "bgr0\0"u8)
        {
            int resultado = LibMpv.mpv_render_context_render(_pintor,
            [
                new() { Tipo = LibMpv.ParametroTamanoSw, Datos = (nint)tamano },
                new() { Tipo = LibMpv.ParametroFormatoSw, Datos = (nint)formato },
                new() { Tipo = LibMpv.ParametroPasoSw, Datos = (nint)(&bytesPorFila) },
                new() { Tipo = LibMpv.ParametroPunteroSw, Datos = bufer },
                new() { Tipo = LibMpv.ParametroBloquearHastaSuMomento, Datos = (nint)(&bloquear) },
                default
            ]);
            return hayFotogramaNuevo && resultado >= 0;
        }
    }

    /// <summary>
    /// El tamaño al que se pinta: el de la zona de video en píxeles, con tope y la misma proporción. Por software el coste crece con
    /// los píxeles: en una ventana mayor que el tope libmpv pinta a ese tamaño y WPF amplía.
    /// </summary>
    public static (int Ancho, int Alto) Tamano(double anchoPx, double altoPx, int topeAncho = 1920, int topeAlto = 1080)
    {
        if (!(anchoPx >= 1) || !(altoPx >= 1)) return (16, 16); // aún sin medir (o no es un número)
        double escala = Math.Min(1, Math.Min(topeAncho / anchoPx, topeAlto / altoPx));
        return (Math.Max(16, (int)Math.Round(anchoPx * escala)), Math.Max(16, (int)Math.Round(altoPx * escala)));
    }

    /// <summary>Antes de que se cierre el motor: libmpv exige soltar el pintor primero.</summary>
    public void Dispose()
    {
        nint pintor = _pintor;
        if (pintor == 0) return;
        _pintor = 0;
        _motor.PintorSoltado();
        try
        {
            LibMpv.mpv_render_context_set_update_callback(pintor, 0, 0);
            LibMpv.mpv_render_context_free(pintor);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PintorPorSoftware", $"No se pudo soltar el pintor de libmpv: {ex.Message}");
        }
    }
}
