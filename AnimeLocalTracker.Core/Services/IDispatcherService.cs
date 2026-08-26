using System;

namespace AnimeLocalTracker.Core.Services;

public interface IDispatcherService
{
    void Invoke(Action action);
}
