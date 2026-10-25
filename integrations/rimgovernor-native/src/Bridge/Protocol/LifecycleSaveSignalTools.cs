using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimGovernor.Native
{
    // Save-time handshake. The brain owns saves; a vanilla save (menu,
    // permadeath save-and-quit) must first let the brain flush its blobs. The
    // SaveGame prefix never cancels: when the brain holds a lifecycle_wait_save_signal
    // call and the save is not brain-initiated, it hands that call a pre_save
    // token, parks the game thread until lifecycle_flush_done acks the token or
    // AckWaitMs pass, then lets the save run. Signals are not durable: with no
    // held call nothing waits. The two tools run on the bridge worker thread
    // and touch only this class's lock; a game-thread hop would stall behind
    // the parked prefix until its timeout.
    internal static class LifecycleSaveSignalTools
    {
        private const int MaxWaitMs = 60000;

        // Off the game thread: the typed core, shared with the in-process facade.
        internal static async Task<RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply> WaitCore(RimGovernor.Brain.GameOrders.Lifecycle.WaitSaveSignalRequest parsed, CancellationToken cancellationToken)
        {
            var wait = Math.Min(Math.Max(parsed.TimeoutMs, 1), MaxWaitMs);
            var token = await SaveHandshake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            // No signal is a timeout or the caller's own cancellation; neither is a failure to log.
            return token != null
                ? new RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply { Signal = new RimGovernor.Brain.GameFacts.Lifecycle.SaveSignal { Token = token } }
                : new RimGovernor.Brain.GameFacts.Lifecycle.WaitSaveSignalReply();
        }
        // Off the game thread: the typed core, shared with the in-process facade.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply FlushDoneCore(RimGovernor.Brain.GameOrders.Lifecycle.FlushDoneRequest parsed)
        {
            if (parsed.Token is null || (parsed.Token ?? "").Length == 0)
                return new RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply { Failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A token is required.") };
            if (!SaveHandshake.Ack((parsed.Token ?? "")))
                return new RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply { Failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Stale or unknown save token.") };
            return new RimGovernor.Brain.GameFacts.Lifecycle.FlushDoneReply();
        }
    }
}
