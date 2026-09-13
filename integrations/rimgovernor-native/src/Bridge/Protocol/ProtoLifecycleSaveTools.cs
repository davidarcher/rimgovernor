using System;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Trusted single-shot checkpoint save only. Native must already be paused;
    // Go's boundary owns pausing/draining writers before calling this tool. Load,
    // reconnect and media/control ownership are separate, unimplemented capability.
    public sealed class ProtoLifecycleSaveTools
    {
        private const string ToolName = "rimgovernor/lifecycle_save";

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
                return new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName, ObservedContext = context,
                    Detail = "Colony, load, map or tick changed before save."
                } };

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
                return new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName,
                    Detail = "Colony state could not be re-observed after save: " + afterUnavailable.Detail
                } };

            bool pausedAfter = Find.TickManager != null && Find.TickManager.Paused;
            if (!after.Identity.Equals(context.Identity) || after.Tick != context.Tick || !pausedAfter)
                return new Lifecycle.SaveReply { Uncertain = new Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName, ObservedContext = after,
                    Detail = "Colony, load, map, tick or pause changed during save."
                } };

            return new Lifecycle.SaveReply { Completed = new Lifecycle.SaveCompleted
            {
                RequestId = player.RequestId, SaveName = request.SaveName, Context = after,
                Paused = pausedAfter, PlayerDirection = player.PlayerDirection
            } };
        }

        private static bool ValidIdentity(Common.Identity identity) =>
            identity != null && identity.HasColonyId && identity.HasLoadToken && identity.HasMapId
            && ProtoBoundary.IsIdentifier(identity.ColonyId) && ProtoBoundary.IsIdentifier(identity.LoadToken) && identity.MapId >= 0;
    }
}
