using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Core;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Host;

internal static class RimBridgeAsyncScheduler
{
    private sealed class QueuedContinuation
    {
        public SendOrPostCallback Callback { get; set; }

        public object State { get; set; }

        public OperationContextSnapshot OperationContext { get; set; }

        public IRimBridgeContext SdkContext { get; set; }
    }

    private sealed class SchedulerSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object state)
        {
            Enqueue(d, state, OperationContext.Capture(), RimBridgeSdkHost.Capture());
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (RimBridgeMainThread.IsMainThread)
            {
                d(state);
                return;
            }

            RimBridgeMainThread.Invoke(() => d(state), timeoutMs: 5000);
        }
    }

    private static readonly object Sync = new();
    private static readonly Queue<QueuedContinuation> Pending = [];
    private static readonly SchedulerSynchronizationContext Context = new();

    public static Task<object> RunAsync(
        Func<Task<object>> flow,
        IRimBridgeContext sdkContext,
        OperationContextSnapshot operationContext,
        CancellationToken cancellationToken)
    {
        if (flow == null)
            throw new ArgumentNullException(nameof(flow));
        if (sdkContext == null)
            throw new ArgumentNullException(nameof(sdkContext));

        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => completion.TrySetCanceled());
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                return completion.Task;
            }
        }

        Enqueue(async _ =>
        {
            if (completion.Task.IsCompleted)
                return;

            var previousSynchronizationContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                using var operationScope = OperationContext.Restore(operationContext);
                using var sdkScope = RimBridgeSdkHost.Push(sdkContext);
                var result = await flow().ConfigureAwait(true);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
            }
        }, null, operationContext, sdkContext);

        return completion.Task;
    }

    public static void Pump()
    {
        while (true)
        {
            QueuedContinuation item;
            lock (Sync)
            {
                if (Pending.Count == 0)
                    break;

                item = Pending.Dequeue();
            }

            try
            {
                var previousSynchronizationContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(Context);
                try
                {
                    using var operationScope = OperationContext.Restore(item.OperationContext);
                    using var sdkScope = RimBridgeSdkHost.Push(item.SdkContext);
                    item.Callback(item.State);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] Async continuation failed: {ex}");
            }
        }
    }

    private static void Enqueue(
        SendOrPostCallback callback,
        object state,
        OperationContextSnapshot operationContext,
        IRimBridgeContext sdkContext)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));

        lock (Sync)
        {
            Pending.Enqueue(new QueuedContinuation
            {
                Callback = callback,
                State = state,
                OperationContext = operationContext,
                SdkContext = sdkContext
            });
        }
    }
}
