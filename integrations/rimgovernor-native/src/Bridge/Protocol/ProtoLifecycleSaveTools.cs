using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Trusted single-shot checkpoint save only. Native must already be paused;
    // Go's boundary owns pausing/draining writers before calling this tool.
    // Save itself is synchronous (completes or fails within one call); ReadSave
    // exists only so a caller who lost the original reply (a dropped
    // connection, a client restart) can re-read that request's exact outcome
    // by request_id, never to poll an in-progress save. Reconnect session
    // semantics, competing viewers and media/control ownership are separate,
    // unimplemented capability.
    public sealed class ProtoLifecycleSaveTools
    {
        private const string ToolName = "rimgovernor/lifecycle_save";
        private const string ReadToolName = "rimgovernor/lifecycle_read_save";

        // Bounded in-memory table of recent save outcomes, guarded by Lock, so
        // ReadSave can answer a request_id whose original reply the caller
        // never received. Pruned oldest-first once over MaxEntries.
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private const int MaxEntries = 32;

        private sealed class Entry
        {
            public string RequestId;
            public DateTime RecordedUtc;
            public Lifecycle.SaveReply Reply;
        }

        [Tool(ToolName, Title = "Trusted native save checkpoint",
            Description = "Verify identity/tick/pause, perform a single native save and re-verify nothing moved.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.SaveReply.", Always = true)]
        public async Task<object> Save(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle SaveRequest ProtoJSON string.")] object request = null)
        {
            Lifecycle.SaveRequest parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Lifecycle.SaveRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Lifecycle.SaveReply { Failure = failure });

            var reply = await ctx.MainThread.InvokeAsync(() => Apply(parsed), cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }

        [Tool(ReadToolName, Title = "Read a prior native save's outcome",
            Description = "Re-read a rimgovernor/lifecycle_save request_id's exact SaveCompleted/SaveUncertain/Failure outcome after a lost reply.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.SaveReply.", Always = true)]
        public async Task<object> ReadSave(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle RequestStatus ProtoJSON string.")] object request = null)
        {
            Lifecycle.RequestStatus parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, ReadToolName, request, Lifecycle.RequestStatus.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Lifecycle.SaveReply { Failure = failure });

            var reply = await ctx.MainThread.InvokeAsync(() => PollSave(parsed), cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }

        // Call only on the game thread.
        internal static Lifecycle.SaveReply Apply(Lifecycle.SaveRequest request)
        {
            var player = request?.Player;
            if (player == null || !ValidIdentity(player.Identity) || !player.HasPlayerDirection || player.PlayerDirection == 0
                || !player.HasRequestId || !ProtoBoundary.IsIdentifier(player.RequestId)
                || !request.HasSaveName || !ProtoBoundary.IsIdentifier(request.SaveName))
                return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    "Save requires a complete player identity, direction, request id and save name.") };

            Common.ObservationContext context;
            Common.Unavailable unavailable;
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out context, out unavailable))
                return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, unavailable.Detail) };

            // A mismatch here is a race with the caller's own expectation, not a
            // malformed request: report it as uncertain and never save.
            if (!player.Identity.Equals(context.Identity) || (request.HasExpectedTick && request.ExpectedTick != context.Tick))
                return Record(player.RequestId, new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName, ObservedContext = context,
                    Detail = "Colony, load, map or tick changed before save."
                } });

            if (Find.TickManager == null || !Find.TickManager.Paused)
                return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    "Native must already be paused before a trusted save.") };

            try
            {
                GameDataSaveLoader.SaveGame(request.SaveName);
            }
            catch (Exception error)
            {
                return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure,
                    "Native save failed: " + error.GetType().Name) };
            }

            Common.ObservationContext after;
            Common.Unavailable afterUnavailable;
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out after, out afterUnavailable))
                return Record(player.RequestId, new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName,
                    Detail = "Colony state could not be re-observed after save: " + afterUnavailable.Detail
                } });

            bool pausedAfter = Find.TickManager != null && Find.TickManager.Paused;
            if (!after.Identity.Equals(context.Identity) || after.Tick != context.Tick || !pausedAfter)
                return Record(player.RequestId, new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName, ObservedContext = after,
                    Detail = "Colony, load, map, tick or pause changed during save."
                } });

            return Record(player.RequestId, new Lifecycle.SaveReply { Completed = new Lifecycle.SaveCompleted
            {
                RequestId = player.RequestId, SaveName = request.SaveName, Context = after,
                Paused = pausedAfter, PlayerDirection = player.PlayerDirection
            } });
        }

        // Call only on the game thread.
        internal static Lifecycle.SaveReply PollSave(Lifecycle.RequestStatus status)
        {
            if (status == null || !status.HasRequestId || !ProtoBoundary.IsIdentifier(status.RequestId))
                return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    "ReadSave requires a request id.") };

            lock (Lock)
            {
                if (Entries.TryGetValue(status.RequestId, out var entry))
                    return entry.Reply;
            }
            return new Lifecycle.SaveReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound,
                "Unknown or expired save request id.") };
        }

        private static Lifecycle.SaveReply Record(string requestId, Lifecycle.SaveReply reply)
        {
            lock (Lock)
            {
                Prune();
                Entries[requestId] = new Entry { RequestId = requestId, RecordedUtc = DateTime.UtcNow, Reply = reply };
            }
            return reply;
        }

        private static void Prune()
        {
            if (Entries.Count < MaxEntries) return;
            string oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var pair in Entries)
            {
                if (pair.Value.RecordedUtc < oldestTime)
                {
                    oldest = pair.Key;
                    oldestTime = pair.Value.RecordedUtc;
                }
            }
            if (oldest != null)
                Entries.Remove(oldest);
        }

        private static bool ValidIdentity(Common.Identity identity) =>
            identity != null && identity.HasColonyId && identity.HasLoadToken && identity.HasMapId
            && ProtoBoundary.IsIdentifier(identity.ColonyId) && ProtoBoundary.IsIdentifier(identity.LoadToken) && identity.MapId >= 0;
    }
}
