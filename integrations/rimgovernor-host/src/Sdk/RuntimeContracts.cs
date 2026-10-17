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

/// <summary>The host's tool registry as the mod's HTTP control channel calls it (host and fixture tools).</summary>
public interface IRimBridgeToolInvoker
{
    /// <summary>The canonical tool names (<c>rimgovernor/set_time_speed</c>, <c>test/lab_start</c>) registered now.</summary>
    IReadOnlyCollection<string> ToolNames { get; }

    /// <summary>
    /// Runs <paramref name="tool"/> with the caller's named arguments and returns its result object. A tool failure,
    /// cancellation or timeout is thrown as an <see cref="InvalidOperationException"/> carrying the host's message.
    /// </summary>
    Task<object> InvokeAsync(string tool, IDictionary<string, object> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Where the host publishes <see cref="IRimBridgeToolInvoker"/> (every registered tool by name, whatever arguments
/// it takes, run as a wire call runs it) for the mod's HTTP control channel. Null until the host's registry is built.
/// </summary>
public static class RimBridgeToolAccess
{
    private static readonly TaskCompletionSource<bool> PublishedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IRimBridgeToolInvoker _current;

    public static IRimBridgeToolInvoker Current => Volatile.Read(ref _current);

    /// <summary>Completes once <see cref="Publish"/> has run and <see cref="Current"/> is set.</summary>
    public static Task Published => PublishedSource.Task;

    public static void Publish(IRimBridgeToolInvoker invoker)
    {
        Volatile.Write(ref _current, invoker ?? throw new ArgumentNullException(nameof(invoker)));
        PublishedSource.TrySetResult(true);
    }
}

/// <summary>
/// The host's game-thread access for in-process callers (the native facade, #3080). The host sets it in its mod
/// constructor, before any companion assembly loads.
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
