using System;
using Avalonia.Threading;
using AnimeLocalTracker.Core.Services;

namespace AnimeLocalTracker.Avalonia.Services;

/// <summary>
/// Puente entre Core (servicios y ViewModels) y el hilo de UI de Avalonia.
/// Se registra en CoreDispatcher.Current para que las actualizaciones desde
/// hilos de background (descargas, sync, tracking) lleguen a la UI.
/// </summary>
public class AvaloniaDispatcherService : IDispatcherService
{
    public void Invoke(Action action)
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }
}
