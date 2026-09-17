#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimGovernor.Protocol.Placement;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class PlacementPreviewsTools
    {
        internal const string ToolName = "rimgovernor/placement_preview";

        [Tool(ToolName, Title = "Placement preview",
            Description = "Read ordinary native placement facts for 1..16 ordered candidates in one main-thread turn. request is the official ProtoJSON PlacementRequest string. No orders, clock, camera or god mode effects.")]
        public async Task<object> Preview(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw transport value; must be a ProtoJSON PlacementRequest string with exact identity and 1..16 placements.", Required = true)] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, PlacementRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new PlacementReply { Failure = failure });
            if (!PlacementProtocol.Validate(parsed, out failure))
                return ProtoBoundary.Encode(new PlacementReply { Failure = failure });

            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, map, out var context, out var identityFailure))
                    return new PlacementReply { Failure = identityFailure };
                var batch = new PlacementBatch { Context = context };
                foreach (var candidate in parsed.Placements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var query = new PlacementQuery(candidate.DefName, candidate.X, candidate.Z,
                        PlacementProtocol.RotationName(candidate.Rotation), candidate.HasStuff ? candidate.Stuff : null);
                    batch.Results.Add(PlacementProtocol.Map(PlacementPreviewOperation.Evaluate(map, query), context));
                }
                return PlacementProtocol.Bounded(new PlacementReply { Batch = batch });
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
