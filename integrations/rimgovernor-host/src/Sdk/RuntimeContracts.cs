using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimGovernor.Host.Sdk;

public interface IRimBridgeContext
{
    string OperationId { get; }

    string CapabilityId { get; }

    /// <summary>
    /// Every argument the caller sent, including keys the tool method does not
    /// declare (the binder drops those from the method call). The host's own
    /// control keys are removed. Empty outside a tool invocation.
    /// </summary>
    IDictionary<string, object> Arguments { get; }

    IRimBridgeMainThread MainThread { get; }
}

public interface IRimBridgeMainThread
{
    bool IsMainThread { get; }

    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default);

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>
/// The host's game-thread access for in-process callers that are not inside a tool invocation (the native
/// facade, #3080): a tool reaches the game thread through <see cref="IRimBridgeContext.MainThread"/>, a caller
/// without a context through this. The host sets it in its mod constructor, before any tool is discovered.
/// </summary>
public static class RimBridgeMainThreadAccess
{
    private static IRimBridgeMainThread _current;

    public static IRimBridgeMainThread Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
