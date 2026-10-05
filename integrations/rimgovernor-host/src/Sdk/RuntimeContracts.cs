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
