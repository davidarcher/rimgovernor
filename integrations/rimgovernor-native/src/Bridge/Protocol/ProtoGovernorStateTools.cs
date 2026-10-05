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

        [Tool("rimgovernor/lifecycle_put_governor_state", Title = "Put governor state",
            Description = "Replace one opaque ASCII governor-state blob; an empty blob deletes it. Durable once the game next saves (#882). The reply carries no blobs (#1362).")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.GovernorStateReply.", Always = true)]
        public async Task<object> Put(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle PutGovernorStateRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/lifecycle_put_governor_state", request,
                Lifecycle.PutGovernorStateRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.GovernorStateReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () =>
            {
                if (string.IsNullOrEmpty(parsed.Key) || !Ascii(parsed.Key) || !Ascii(parsed.Blob))
                    return new Lifecycle.GovernorStateReply { Failure = new Common.Failure {
                        Code = Common.FailureCode.InvalidRequest, Detail = "key must be non-empty ASCII and blob ASCII" } };
                if (Current.Game == null) return Reply();
                var blobs = GovernorState.For(Current.Game).Blobs;
                if (parsed.Blob.Length == 0) blobs.Remove(parsed.Key);
                else blobs[parsed.Key] = parsed.Blob;
                return new Lifecycle.GovernorStateReply { Loaded = new Lifecycle.GovernorStateBlobs() };
            }, cancellationToken).ConfigureAwait(false);
        }

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
