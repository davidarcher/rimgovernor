using System;
using RimGovernor.Host.Core;

namespace RimGovernor.Host;

internal sealed class MainThreadDispatcher : IGameThreadDispatcher
{
    public bool IsMainThread => NativeMainThread.IsMainThread;

    public T Invoke<T>(Func<T> func, int timeoutMs)
    {
        return NativeMainThread.Invoke(func, timeoutMs);
    }

    public void Invoke(Action action, int timeoutMs)
    {
        NativeMainThread.Invoke(action, timeoutMs);
    }
}
