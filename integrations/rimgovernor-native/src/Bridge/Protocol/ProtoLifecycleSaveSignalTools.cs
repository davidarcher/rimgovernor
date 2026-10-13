#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
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
    public sealed class ProtoLifecycleSaveSignalTools
    {
        private const string WaitTool = "rimgovernor/lifecycle_wait_save_signal";
        private const string FlushTool = "rimgovernor/lifecycle_flush_done";
        private const int DefaultWaitMs = 20000;
        private const int MaxWaitMs = 60000;

        [Tool(WaitTool, Title = "Wait for a pre-save signal",
            Description = "Long-poll: returns a pre_save signal with an ack token the moment a vanilla save starts, or a timeout. Runs off the game thread.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.WaitSaveSignalReply.", Always = true)]
        public async Task<object> Wait(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle WaitSaveSignalRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, WaitTool, request, out Lifecycle.WaitSaveSignalRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.WaitSaveSignalReply { Outcome = new Lifecycle.WaitSaveSignalReplyOutcome.FailureCase(failure) });
            return ProtoBoundary.Encode(await WaitCore(parsed, cancellationToken).ConfigureAwait(false));
        }

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
        [Tool(FlushTool, Title = "Ack a pre-save signal",
            Description = "Release the parked save for a pre_save token. A stale, expired or unknown token is rejected. Runs off the game thread.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.FlushDoneReply.", Always = true)]
        public Task<object> FlushDone(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle FlushDoneRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, FlushTool, request, out Lifecycle.FlushDoneRequest? parsed, out var failure))
                return Task.FromResult<object>(ProtoBoundary.Encode(new Lifecycle.FlushDoneReply { Outcome = new Lifecycle.FlushDoneReplyOutcome.FailureCase(failure) }));
            return Task.FromResult<object>(ProtoBoundary.Encode(FlushDoneCore(parsed)));
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
