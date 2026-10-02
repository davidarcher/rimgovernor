#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomeBridge.BridgeTools
{
    // Long-poll support for clock_read_events. A waiter is registered inside
    // the same Gate section that found the journal empty past its cursor, so
    // a row appended between the read and the wait cannot be missed: Append
    // signals under the same lock. Slots are bounded well under the bridge's
    // concurrent-call ceiling so waiting readers never starve other tools.
    internal static partial class Supervisor
    {
        internal const int MaxWaiters = 4;
        private sealed class Waiter
        {
            internal readonly long After;
            internal readonly TaskCompletionSource<bool> Source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Waiter(long after) { After = after; }
        }
        private static readonly List<Waiter> Waiters = new List<Waiter>();

        // Caller holds Gate and has just observed the journal empty after
        // `after`. Returns false when every slot is taken; the reader then
        // answers immediately with the empty page as before.
        private static bool TryRegisterWaiter(long after, out Task<bool> wake)
        {
            wake = Task.FromResult(false);
            if (Waiters.Count >= MaxWaiters) return false;
            var waiter = new Waiter(after);
            Waiters.Add(waiter);
            wake = waiter.Source.Task;
            return true;
        }
        private static void SignalWaiters(long newest)
        {
            if (Waiters.Count == 0) return;
            for (int i = Waiters.Count - 1; i >= 0; i--)
            {
                var waiter = Waiters[i];
                if (waiter.After >= newest) continue;
                Waiters.RemoveAt(i);
                waiter.Source.TrySetResult(true);
            }
        }
        // Timed-out or cancelled waiters release their slot themselves.
        private static void ReleaseWaiter(Task<bool> wake)
        {
            lock (Gate)
            {
                for (int i = Waiters.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(Waiters[i].Source.Task, wake)) { Waiters.RemoveAt(i); return; }
            }
        }
        // Off the main thread: wait for a row past the cursor, the timeout or
        // cancellation, whichever is first. Returns whether a row arrived.
        internal static async Task<bool> AwaitWake(Task<bool> wake, int waitMs, CancellationToken token)
        {
            var finished = await Task.WhenAny(wake, Task.Delay(waitMs, token)).ConfigureAwait(false);
            if (ReferenceEquals(finished, wake)) return wake.Result;
            ReleaseWaiter(wake);
            token.ThrowIfCancellationRequested();
            return false;
        }
    }
}
