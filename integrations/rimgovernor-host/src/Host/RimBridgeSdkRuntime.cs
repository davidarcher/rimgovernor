using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Core;
using RimGovernor.Host.Sdk;

namespace RimGovernor.Host;

/// <summary>
/// Carries the <see cref="IRimBridgeContext"/> of the tool invocation that is
/// running, across the async scheduler's continuations.
/// </summary>
internal static class RimBridgeSdkHost
{
    private sealed class Scope : IDisposable
    {
        private readonly IRimBridgeContext _previous;
        private bool _disposed;

        public Scope(IRimBridgeContext previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentContext.Value = _previous;
            _disposed = true;
        }
    }

    private static readonly AsyncLocal<IRimBridgeContext> CurrentContext = new();

    public static IRimBridgeContext Capture()
    {
        return CurrentContext.Value;
    }

    public static IDisposable Push(IRimBridgeContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    public static IRimBridgeContext CreateContext(OperationContextSnapshot snapshot, IDictionary<string, object> arguments = null)
    {
        snapshot ??= new OperationContextSnapshot();
        return new RimBridgeContext(
            snapshot.OperationId ?? string.Empty,
            snapshot.CapabilityId ?? string.Empty,
            arguments,
            new RimBridgeMainThreadClient());
    }
}

internal sealed class RimBridgeContext : IRimBridgeContext
{
    public RimBridgeContext(
        string operationId,
        string capabilityId,
        IDictionary<string, object> arguments,
        IRimBridgeMainThread mainThread)
    {
        OperationId = operationId ?? string.Empty;
        CapabilityId = capabilityId ?? string.Empty;
        Arguments = arguments ?? new Dictionary<string, object>(StringComparer.Ordinal);
        MainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
    }

    public string OperationId { get; }

    public string CapabilityId { get; }

    public IDictionary<string, object> Arguments { get; }

    public IRimBridgeMainThread MainThread { get; }
}

internal sealed class RimBridgeMainThreadClient : IRimBridgeMainThread
{
    public bool IsMainThread => RimBridgeMainThread.IsMainThread;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        return RimBridgeMainThread.InvokeAsync(func, cancellationToken);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        return RimBridgeMainThread.InvokeAsync(action, cancellationToken);
    }
}
