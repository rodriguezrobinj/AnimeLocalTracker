using System;
using System.Windows;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Salto al hilo de la interfaz (ver wpf-mvvm.md, punto 4): los mensajes y eventos pueden llegar desde un hilo de fondo
/// y quien toque controles o colecciones observables debe hacerlo en el despachador principal.
/// </summary>
public static class HiloUi
{
    /// <summary>Ejecuta la acción ya mismo si se está en el hilo de la interfaz (o no hay aplicación, como en las pruebas);
    /// si no, la encola en el despachador principal sin esperar a que termine.</summary>
    public static void Ejecutar(Action accion)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) accion();
        else dispatcher.InvokeAsync(accion);
    }
}
