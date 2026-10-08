#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // The handshake state and the SaveGame prefix.
    public static class SaveHandshake
    {
        public const int AckWaitMs = 3000;

        private static readonly object Gate = new object();
        private static TaskCompletionSource<string?>? waiter;
        private static string? pendingToken;
        private static ManualResetEventSlim? pendingAck;
        private static int goInitiated;

        public static void Install()
        {
            var original = AccessTools.Method(typeof(GameDataSaveLoader), "SaveGame");
            if (original == null) { ModLog.Warn("startup", "save handshake patch target missing."); return; }
            new Harmony("rimgovernor.save-handshake").Patch(original,
                prefix: new HarmonyMethod(typeof(SaveHandshake), nameof(BeforeSave)));
            // Go owns autosaves; vanilla's never runs, connected or not.
            var tick = AccessTools.Method(typeof(Autosaver), "AutosaverTick");
            if (tick == null) { ModLog.Warn("startup", "autosaver patch target missing."); return; }
            new Harmony("rimgovernor.save-handshake").Patch(tick,
                prefix: new HarmonyMethod(typeof(SaveHandshake), nameof(SkipAutosave)));
        }

        private static bool SkipAutosave() => false;

        // Go's own saves (lifecycle_save, the new-colony start) raise no signal.
        public static void BeginGoSave() => Interlocked.Increment(ref goInitiated);
        public static void EndGoSave() => Interlocked.Decrement(ref goInitiated);

        // Game thread. Never cancels the save.
        private static void BeforeSave()
        {
            try { Raise(); }
            catch (Exception error) { ModLog.Warn("lifecycle", "pre_save signal failed: " + error); }
        }

        internal static void Raise()
        {
            if (Volatile.Read(ref goInitiated) > 0) return;
            var ack = new ManualResetEventSlim(false);
            string token;
            lock (Gate)
            {
                if (waiter == null || pendingToken != null) return;
                token = Guid.NewGuid().ToString("N");
                pendingToken = token;
                pendingAck = ack;
                var held = waiter;
                waiter = null;
                held.TrySetResult(token);
            }
            var acked = ack.Wait(AckWaitMs);
            lock (Gate)
            {
                if (ReferenceEquals(pendingAck, ack)) { pendingToken = null; pendingAck = null; }
            }
            if (!acked) ModLog.Warn("lifecycle", "pre_save ack timed out after " + AckWaitMs + " ms; saving anyway.");
        }

        // Worker thread. Returns the raised token, or null on timeout, cancel
        // or when a newer wait superseded this one.
        public static async Task<string?> WaitAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            var mine = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string?>? previous;
            lock (Gate) { previous = waiter; waiter = mine; }
            previous?.TrySetResult(null);
            try { await Task.WhenAny(mine.Task, Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            lock (Gate) { if (ReferenceEquals(waiter, mine)) waiter = null; }
            // A signal raised at the instant of timeout is still delivered:
            // the parked save is waiting on this very call.
            return mine.Task.IsCompleted ? mine.Task.Result : null;
        }

        public static bool Ack(string token)
        {
            lock (Gate)
            {
                if (pendingToken == null || pendingToken != token) return false;
                var ack = pendingAck;
                pendingToken = null;
                ack?.Set();
                return true;
            }
        }
    }
}
