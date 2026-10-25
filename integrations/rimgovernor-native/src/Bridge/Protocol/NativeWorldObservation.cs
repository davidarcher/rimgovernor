using System;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // observations_read_world: the scope map's tile and the settlements near a
    // requested tile, each with its faction's relation and whether it trades now.
    internal static class NativeWorldObservationTools
    {
        // On the game thread. A refused request or a world that cannot be read completely throws.
        internal static Obs.WorldSnapshot HandleReadWorld(Obs.WorldRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out _, out var invalid))
                throw NativeFaults.Refused(invalid);
            try
            {
                var settlements = request.Settlements is { } search && new PlanetTile(search.Tile) is var tile && tile.Valid
                    ? Find.WorldObjects.Settlements
                        .Where(s => Find.WorldGrid.ApproxDistanceInTiles(tile, s.Tile) <= search.Radius)
                        .Select(SettlementRow).ToArray()
                    : Array.Empty<Obs.Settlement>();
                var info = map.TileInfo ?? throw new InvalidOperationException("The map's world tile is unavailable.");
                return new Obs.WorldSnapshot
                {
                    Tile = new Obs.WorldTile { Tile = map.Tile.tileId, Hilliness = info.hilliness.ToString() },
                    Settlements = [.. settlements],
                };
            }
            catch (Exception thrown)
            {
                ModLog.Error("observe", "World read failed: " + thrown);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Native world read could not complete: " + thrown.GetType().Name + ": " + thrown.Message });
            }
        }

        private static Obs.Settlement SettlementRow(Settlement settlement)
        {
            var faction = settlement.Faction ?? throw new InvalidOperationException("Settlement " + settlement.GetUniqueLoadID() + " has no faction.");
            return new Obs.Settlement
            {
                Id = settlement.GetUniqueLoadID(),
                Tile = settlement.Tile.tileId,
                Relation = faction.IsPlayer ? Obs.SettlementRelation.Player : faction.HostileTo(Faction.OfPlayer) ? Obs.SettlementRelation.Hostile : Obs.SettlementRelation.Neutral,
                CanTrade = settlement.CanTradeNow,
                TraderKind = settlement.TraderKind?.defName ?? "",
            };
        }

        private static bool Validate(Obs.WorldRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and a nonnegative settlement search radius are required.");
            return request?.Scope?.ExpectedIdentity != null && (request.Settlements is null || request.Settlements.Radius >= 0);
        }
    }
}
