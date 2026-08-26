using System;

namespace AnimeLocalTracker.Core.Services;

public static class CoreDispatcher
{
    public static IDispatcherService? Current { get; set; }

    public static void Invoke(Action action)
    {
        if (Current != null)
        {
            Current.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
