#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Contracts.Wire.Placement;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class PlacementPreviewsTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static PlacementReply HandlePreview(PlacementRequest parsed, CancellationToken cancellationToken)
        {
            if (!PlacementProtocol.Validate(parsed, out var invalidRequest))
                return new PlacementReply { Outcome = invalidRequest };
            var map = Find.CurrentMap;
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, map, out var context, out var identityFailure))
                return new PlacementReply { Outcome = identityFailure };
            var batch = new PlacementBatch { Context = context, Results = [] };
            foreach (var candidate in parsed.Placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new PlacementQuery(candidate.DefName, candidate.X, candidate.Z,
                    PlacementProtocol.RotationName(candidate.Rotation), candidate.Stuff, candidate.IgnoreNaturalRock);
                batch = batch with { Results = [.. batch.Results, PlacementProtocol.Map(PlacementPreviewOperation.Evaluate(map, query), context)] };
            }
            return new PlacementReply { Outcome = batch };
        }
    }
}
