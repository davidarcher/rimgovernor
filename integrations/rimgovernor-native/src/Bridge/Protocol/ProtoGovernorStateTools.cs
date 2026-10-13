#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

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
            if (!ProtoBoundary.TryParse<Lifecycle.GovernorStateRequest>(ctx, "rimgovernor/lifecycle_read_governor_state", request,
                out _, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Outcome = new Lifecycle.GovernorStateReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleRead(), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the read, shared with the in-process facade.
        internal static Lifecycle.GovernorStateReply HandleRead() => Reply();

        [Tool("rimgovernor/lifecycle_put_governor_state_batch", Title = "Put governor state batch",
            Description = "Replace the whole opaque ASCII governor-state blob set in one call; keys absent from the batch are removed. Runs off the game thread. Durable once the game next saves (#2357). The reply carries no blobs.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.GovernorStateReply.", Always = true)]
        public async Task<object> PutBatch(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle PutGovernorStateBatchRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Lifecycle.PutGovernorStateBatchRequest>(ctx, "rimgovernor/lifecycle_put_governor_state_batch", request,
                out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Outcome = new Lifecycle.GovernorStateReplyOutcome.FailureCase(failure) });
            var reply = TryPut(parsed, out var set);
            if (reply != null) return ProtoBoundary.Encode(reply);
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => PutOnGameThread(set), cancellationToken).ConfigureAwait(false);
        }

        // Off the game thread: validates the batch and stages it when the game already carries the component.
        // Null means a save that predates the component needs the game thread to attach it: PutOnGameThread(set).
        internal static Lifecycle.GovernorStateReply? TryPut(Lifecycle.PutGovernorStateBatchRequest request, out System.Collections.Generic.Dictionary<string, string> set)
        {
            // An empty blob means absent.
            set = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in request.Blobs)
            {
                if (string.IsNullOrEmpty(pair.Key) || !Ascii(pair.Key) || !Ascii(pair.Value))
                    return new Lifecycle.GovernorStateReply { Outcome = new Common.Failure {
                        Code = Common.FailureCode.InvalidRequest, Detail = "key must be non-empty ASCII and blob ASCII" } };
                if (pair.Value.Length > 0) set[pair.Key] = pair.Value;
            }
            var game = Current.Game;
            if (game == null) return NotLoaded();
            // Staging is lock-guarded, so the worker thread never waits for the
            // frame loop. Only a save that predates the component needs the
            // game thread, to attach it.
            var state = game.GetComponent<GovernorState>();
            if (state == null) return null;
            state.Stage(set);
            return Staged();
        }

        // On the game thread: attaches the component to an older save and stages the batch.
        internal static Lifecycle.GovernorStateReply PutOnGameThread(System.Collections.Generic.Dictionary<string, string> set)
        {
            if (Current.Game == null) return NotLoaded();
            GovernorState.For(Current.Game).Stage(set);
            return Staged();
        }

        private static Lifecycle.GovernorStateReply Staged() => new Lifecycle.GovernorStateReply { Outcome = new Lifecycle.GovernorStateBlobs() };

        private static Lifecycle.GovernorStateReply NotLoaded() =>
            new Lifecycle.GovernorStateReply { Outcome = new Common.Unavailable {
                Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };

        private static bool Ascii(string value) => value.All(c => c < 128);

        private static Lifecycle.GovernorStateReply Reply()
        {
            if (Current.Game == null)
                return new Lifecycle.GovernorStateReply { Outcome = new Common.Unavailable {
                    Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var loaded = new Lifecycle.GovernorStateBlobs { Blobs = new System.Collections.Generic.Dictionary<string, string>(GovernorState.For(Current.Game).Blobs) };
            return new Lifecycle.GovernorStateReply { Outcome = loaded };
        }
    }
}
