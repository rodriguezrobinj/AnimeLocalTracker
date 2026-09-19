using System;
using System.Diagnostics;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Sube la prioridad del proceso mientras hay un video abierto. Cuando la ventana pierde el foco
/// (te vas a otra aplicación), Windows deja de favorecer a la app frente al resto de procesos: con
/// pocos núcleos, el hilo que alimenta el audio compite con el navegador, el reproductor de música,
/// etc. y el sonido se entrecorta. Con prioridad "por encima de lo normal" esos cortes desaparecen
/// sin acaparar el equipo (no se usa Alta/Tiempo real).
///
/// Solo se toca si el proceso está en Normal: si el usuario fijó otra prioridad a mano (Administrador
/// de tareas), se respeta. Con conteo de usos para que varios reproductores no se pisen entre sí.
/// </summary>
public static class PrioridadReproduccion
{
    private static readonly object _candado = new();
    private static int _usos;
    private static bool _elevada;

    // Sustituibles en pruebas para no tocar la prioridad real del proceso de tests.
    internal static Func<ProcessPriorityClass> Leer { get; set; } = () => Process.GetCurrentProcess().PriorityClass;
    internal static Action<ProcessPriorityClass> Escribir { get; set; } = p => Process.GetCurrentProcess().PriorityClass = p;

    public static void Elevar()
    {
        lock (_candado)
        {
            if (_usos++ > 0) return;

            try
            {
                if (Leer() != ProcessPriorityClass.Normal) return;
                Escribir(ProcessPriorityClass.AboveNormal);
                _elevada = true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PrioridadReproduccion", $"No se pudo subir la prioridad del proceso: {ex.Message}");
            }
        }
    }

    public static void Restaurar()
    {
        lock (_candado)
        {
            if (_usos == 0) return;
            if (--_usos > 0) return;

            if (!_elevada) return;
            _elevada = false;

            try
            {
                Escribir(ProcessPriorityClass.Normal);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PrioridadReproduccion", $"No se pudo restaurar la prioridad del proceso: {ex.Message}");
            }
        }
    }

    internal static void ReiniciarParaPruebas()
    {
        lock (_candado)
        {
            _usos = 0;
            _elevada = false;
            Leer = () => Process.GetCurrentProcess().PriorityClass;
            Escribir = p => Process.GetCurrentProcess().PriorityClass = p;
        }
    }
}
