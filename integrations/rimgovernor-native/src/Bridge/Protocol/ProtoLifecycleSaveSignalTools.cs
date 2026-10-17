#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Save-time handshake. Go owns saves; a vanilla save (menu,
    // permadeath save-and-quit) must first let Go flush its blobs. The
    // SaveGame prefix never cancels: when Go holds a lifecycle_wait_save_signal
    // call and the save is not Go-initiated, it hands that call a pre_save
    // token, parks the game thread until lifecycle_flush_done acks the token or
    // AckWaitMs pass, then lets the save run. Signals are not durable: with no
    // held call nothing waits. The two tools run on the bridge worker thread
    // and touch only this class's lock; a game-thread hop would stall behind
    // the parked prefix until its timeout.
    internal static class ProtoLifecycleSaveSignalTools
    {
        private const int DefaultWaitMs = 20000;
        private const int MaxWaitMs = 60000;

        // Off the game thread: the typed core, shared with the in-process facade.
        internal static async Task<Lifecycle.WaitSaveSignalReply> WaitCore(Lifecycle.WaitSaveSignalRequest parsed, CancellationToken cancellationToken)
        {
            var wait = parsed.TimeoutMs is not null ? Math.Min(Math.Max(parsed.TimeoutMs.GetValueOrDefault(), 1), MaxWaitMs) : DefaultWaitMs;
            var token = await SaveHandshake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            if (token != null)
                return new Lifecycle.WaitSaveSignalReply { Outcome = new Lifecycle.SaveSignal { Kind = "pre_save", Token = token } };
            if (cancellationToken.IsCancellationRequested)
                return new Lifecycle.WaitSaveSignalReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Cancelled, "Wait cancelled.") };
            return new Lifecycle.WaitSaveSignalReply { Outcome = new Lifecycle.SaveSignalTimeout() };
        }
        // Off the game thread: the typed core, shared with the in-process facade.
        internal static Lifecycle.FlushDoneReply FlushDoneCore(Lifecycle.FlushDoneRequest parsed)
        {
            if (parsed.Token is null || (parsed.Token ?? "").Length == 0)
                return new Lifecycle.FlushDoneReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A token is required.") };
            if (!SaveHandshake.Ack((parsed.Token ?? "")))
                return new Lifecycle.FlushDoneReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Stale or unknown save token.") };
            return new Lifecycle.FlushDoneReply { Outcome = new Lifecycle.FlushDoneAcked() };        }
    }
}
