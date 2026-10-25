using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Core;
using RimGovernor.Host.Sdk;

namespace RimGovernor.Host;

/// <summary>
/// Carries the <see cref="INativeContext"/> of the tool invocation that is
/// running, across the async scheduler's continuations.
/// </summary>
internal static class NativeSdkHost
{
    private sealed class Scope : IDisposable
    {
        private readonly INativeContext _previous;
        private bool _disposed;

        public Scope(INativeContext previous)
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

    private static readonly AsyncLocal<INativeContext> CurrentContext = new();

    public static INativeContext Capture()
    {
        return CurrentContext.Value;
    }

    public static IDisposable Push(INativeContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    public static INativeContext CreateContext(OperationContextSnapshot snapshot, IDictionary<string, object> arguments = null)
    {
        snapshot ??= new OperationContextSnapshot();
        return new NativeContext(
            snapshot.OperationId ?? string.Empty,
            snapshot.CapabilityId ?? string.Empty,
            arguments,
            new NativeMainThreadClient());
    }
}

internal sealed class NativeContext : INativeContext
{
    public NativeContext(
        string operationId,
        string capabilityId,
        IDictionary<string, object> arguments,
        INativeMainThread mainThread)
    {
        OperationId = operationId ?? string.Empty;
        CapabilityId = capabilityId ?? string.Empty;
        Arguments = arguments ?? new Dictionary<string, object>(StringComparer.Ordinal);
        MainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
    }

    public string OperationId { get; }

    public string CapabilityId { get; }

    public IDictionary<string, object> Arguments { get; }

    public INativeMainThread MainThread { get; }
}

internal sealed class NativeMainThreadClient : INativeMainThread
{
    public bool IsMainThread => NativeMainThread.IsMainThread;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        return NativeMainThread.InvokeAsync(func, cancellationToken);
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        return NativeMainThread.InvokeAsync(action, cancellationToken);
    }
}
