using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Tiempos de arranque y de cambio de pestaña en el registro (entradas "[Perf]"), para saber con datos de uso real
/// dónde se va el tiempo en vez de suponerlo. Cuesta un cronómetro y una línea de registro por medición.
/// </summary>
public static class MedidorRendimiento
{
    /// <summary>Un cambio de pestaña más lento que esto se registra como INFO (visible sin "Registro detallado").</summary>
    private const int UmbralNavegacionLentaMs = 250;

    private static readonly Stopwatch Reloj = Stopwatch.StartNew();
    private static readonly double MsAntesDelReloj = MsDesdeInicioDelProceso();
    private static readonly List<(string Nombre, long Ms)> Hitos = new();

    private static double MsDesdeInicioDelProceso()
    {
        try
        {
            using var proceso = Process.GetCurrentProcess();
            return Math.Max(0, (DateTime.Now - proceso.StartTime).TotalMilliseconds);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Milisegundos desde que Windows creó el proceso.</summary>
    public static long MsDesdeInicio => (long)(MsAntesDelReloj + Reloj.ElapsedMilliseconds);

    /// <summary>Marca un punto del arranque; el tiempo se cuenta desde que Windows creó el proceso.</summary>
    public static void Hito(string nombre)
    {
        long ms = MsDesdeInicio;
        lock (Hitos) Hitos.Add((nombre, ms));
    }

    /// <summary>Escribe en el registro los hitos acumulados hasta ahora (y los vacía).</summary>
    public static void RegistrarArranque(string titulo)
    {
        string texto;
        lock (Hitos)
        {
            if (Hitos.Count == 0) return;
            texto = string.Join(" · ", Hitos.Select(h => $"{h.Nombre} {h.Ms} ms"));
            Hitos.Clear();
        }
        AppLogger.Info("App", $"[Perf] {titulo}: {texto}.");
    }

    /// <summary>
    /// Mide un cambio de pantalla: cuándo quedó colocada (vista construida y medida) y cuándo la interfaz volvió a estar
    /// libre (ya pintada y sin trabajo pendiente). Sin aplicación WPF (pruebas) no hace nada.
    /// </summary>
    public static void MedirNavegacion(object? desde, object? hacia)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || hacia == null || !dispatcher.CheckAccess()) return;

        Medir($"Navegación {Nombre(desde)} → {Nombre(hacia)}", dispatcher);
    }

    /// <summary>
    /// Igual que <see cref="MedirNavegacion"/> para los cambios DENTRO de una pestaña (Biblioteca ↔ Minijuegos, abrir un
    /// minijuego…): se llama justo antes de provocar el cambio.
    /// </summary>
    public static void MedirCambio(string descripcion)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || !dispatcher.CheckAccess()) return;

        Medir(descripcion, dispatcher);
    }

    private static void Medir(string descripcion, Dispatcher dispatcher)
    {
        var reloj = Stopwatch.StartNew();
        long msColocada = 0;
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => msColocada = reloj.ElapsedMilliseconds);
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            long msLibre = reloj.ElapsedMilliseconds;
            string texto = $"[Perf] {descripcion}: colocada a los {msColocada} ms, interfaz libre a los {msLibre} ms.";
            if (msLibre >= UmbralNavegacionLentaMs) AppLogger.Info("Navegacion", texto);
            else AppLogger.Debug("Navegacion", texto);
        });
    }

    private static string Nombre(object? vista) => vista?.GetType().Name.Replace("ViewModel", string.Empty) ?? "(inicio)";
}
