#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Save-time handshake (#2358). Go owns saves; a vanilla save (menu,
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
            if (!ProtoBoundary.TryParse(ctx, WaitTool, request, Lifecycle.WaitSaveSignalRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.WaitSaveSignalReply { Failure = failure });
            var wait = parsed.HasTimeoutMs ? Math.Min(Math.Max(parsed.TimeoutMs, 1), MaxWaitMs) : DefaultWaitMs;
            var token = await SaveHandshake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            if (token != null)
                return ProtoBoundary.Encode(new Lifecycle.WaitSaveSignalReply { Signal = new Lifecycle.SaveSignal { Kind = "pre_save", Token = token } });
            if (cancellationToken.IsCancellationRequested)
                return ProtoBoundary.Encode(new Lifecycle.WaitSaveSignalReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Cancelled, "Wait cancelled.") });
            return ProtoBoundary.Encode(new Lifecycle.WaitSaveSignalReply { Timeout = new Lifecycle.SaveSignalTimeout() });
        }

        [Tool(FlushTool, Title = "Ack a pre-save signal",
            Description = "Release the parked save for a pre_save token. A stale, expired or unknown token is rejected. Runs off the game thread.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.FlushDoneReply.", Always = true)]
        public Task<object> FlushDone(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle FlushDoneRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, FlushTool, request, Lifecycle.FlushDoneRequest.Parser, out var parsed, out var failure))
                return Task.FromResult<object>(ProtoBoundary.Encode(new Lifecycle.FlushDoneReply { Failure = failure }));
            if (!parsed.HasToken || parsed.Token.Length == 0)
                return Task.FromResult<object>(ProtoBoundary.Encode(new Lifecycle.FlushDoneReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A token is required.") }));
            if (!SaveHandshake.Ack(parsed.Token))
                return Task.FromResult<object>(ProtoBoundary.Encode(new Lifecycle.FlushDoneReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Stale or unknown save token.") }));
            return Task.FromResult<object>(ProtoBoundary.Encode(new Lifecycle.FlushDoneReply { Acked = new Lifecycle.FlushDoneAcked() }));
        }
    }
}
