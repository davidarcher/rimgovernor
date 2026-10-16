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
/// it takes, run as a wire call runs it) for the mod's HTTP control channel, together with the tool-class instances the
/// host constructed. Null until the host's registry is built. Each tool class is constructed once, by the host on the
/// game thread (#3289): the HTTP channel waits for <see cref="Published"/> and then shares these instances instead of
/// building its own, since some tool constructors install Harmony patches and add Defs.
/// </summary>
public static class RimBridgeToolAccess
{
    private static readonly TaskCompletionSource<bool> PublishedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IRimBridgeToolInvoker _current;
    private static IReadOnlyDictionary<Type, object> _instances = new Dictionary<Type, object>();

    public static IRimBridgeToolInvoker Current => Volatile.Read(ref _current);

    /// <summary>Completes once <see cref="Publish"/> has run: <see cref="Current"/> and the instances are then set.</summary>
    public static Task Published => PublishedSource.Task;

    /// <summary>The host publishes its registry and the tool-class instances it built (types with only static tools have none).</summary>
    public static void Publish(IRimBridgeToolInvoker invoker, IReadOnlyDictionary<Type, object> instances)
    {
        Volatile.Write(ref _instances, instances ?? throw new ArgumentNullException(nameof(instances)));
        Volatile.Write(ref _current, invoker ?? throw new ArgumentNullException(nameof(invoker)));
        PublishedSource.TrySetResult(true);
    }

    /// <summary>The host's instance of tool class <paramref name="type"/>; false when the host did not construct one.</summary>
    public static bool TryGetInstance(Type type, out object instance)
        => Volatile.Read(ref _instances).TryGetValue(type, out instance);
}

public static class RimBridgeMainThreadAccess
{
    private static IRimBridgeMainThread _current;

    public static IRimBridgeMainThread Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
