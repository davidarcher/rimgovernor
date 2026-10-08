#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    public sealed class ProtoGovernorStateTools
    {
        [Tool("rimgovernor/lifecycle_read_governor_state", Title = "Read governor state",
            Description = "Read every opaque governor-state blob saved with the game (#882).")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.GovernorStateReply.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle GovernorStateRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/lifecycle_read_governor_state", request,
                Lifecycle.GovernorStateRequest.Parser, out _, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Reply(), cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/lifecycle_put_governor_state_batch", Title = "Put governor state batch",
            Description = "Replace the whole opaque ASCII governor-state blob set in one call; keys absent from the batch are removed. Runs off the game thread. Durable once the game next saves (#2357). The reply carries no blobs.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.GovernorStateReply.", Always = true)]
        public async Task<object> PutBatch(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle PutGovernorStateBatchRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/lifecycle_put_governor_state_batch", request,
                Lifecycle.PutGovernorStateBatchRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Failure = failure });
            // An empty blob means absent.
            var set = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in parsed.Blobs)
            {
                if (string.IsNullOrEmpty(pair.Key) || !Ascii(pair.Key) || !Ascii(pair.Value))
                    return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Failure = new Common.Failure {
                        Code = Common.FailureCode.InvalidRequest, Detail = "key must be non-empty ASCII and blob ASCII" } });
                if (pair.Value.Length > 0) set[pair.Key] = pair.Value;
            }
            var game = Current.Game;
            if (game == null) return ProtoBoundary.Encode(NotLoaded());
            // Staging is lock-guarded, so the worker thread never waits for the
            // frame loop. Only a save that predates the component needs the
            // game thread, to attach it.
            var state = game.GetComponent<GovernorState>();
            if (state == null)
                return await ProtoBoundary.OnMainThreadEncoded(ctx, () =>
                {
                    if (Current.Game == null) return NotLoaded();
                    GovernorState.For(Current.Game).Stage(set);
                    return Staged();
                }, cancellationToken).ConfigureAwait(false);
            state.Stage(set);
            return ProtoBoundary.Encode(Staged());
        }

        private static Lifecycle.GovernorStateReply Staged() => new Lifecycle.GovernorStateReply { Loaded = new Lifecycle.GovernorStateBlobs() };

        private static Lifecycle.GovernorStateReply NotLoaded() =>
            new Lifecycle.GovernorStateReply { Unavailable = new Common.Unavailable {
                Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };

        private static bool Ascii(string value) => value.All(c => c < 128);

        private static Lifecycle.GovernorStateReply Reply()
        {
            if (Current.Game == null)
                return new Lifecycle.GovernorStateReply { Unavailable = new Common.Unavailable {
                    Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var loaded = new Lifecycle.GovernorStateBlobs();
            loaded.Blobs.Add(GovernorState.For(Current.Game).Blobs);
            return new Lifecycle.GovernorStateReply { Loaded = loaded };
        }
    }
}
