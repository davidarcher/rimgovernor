#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Closes the observations_read_world gap: WorldRequest/WorldReply and the
    // Settlement message (including its snapshot/faction_snapshot CAS tokens)
    // were already fully defined with no native handler and no Go consumer
    // (confirmed by exhaustive search before writing this file) -- the same
    // "proto ahead of consumer" pattern NativeWorldProgressionObservation
    // already closed for its own request. This handler
    // only surfaces settlements near a requested tile with fresh CAS tokens;
    // WorldTile terrain facts remain unimplemented (a distinct, unrelated
    // follow-up, not needed by any current consumer).
    //
    // Settlement/faction tokens are self-computed: no other observation
    // exposes a per-settlement CAS snapshot yet.
    // Go's settlement-gift boundary is this handler's first consumer, reading
    // the settlement at an already-known caravan tile (from
    // ReadWorldProgression) to learn whether -- and to which faction -- that
    // caravan currently sits.
    internal static class NativeWorldObservation
    {

        // Versioned over the observed FactionState facts (player, goodwill,
        // hostile).
        internal static string FactionToken(Faction faction) => NativeSnapshotVersions.Token("faction", null, faction.GetUniqueLoadID(), string.Join("|",
            faction.IsPlayer.ToString(), faction.IsPlayer ? "" : faction.PlayerGoodwill.ToString(), faction.HostileTo(Faction.OfPlayer).ToString()));

        // Versioned over the Settlement facts (tile, faction id, CanTradeNow),
        // so a settlement whose trade eligibility changed reads a new token.
        internal static string SettlementToken(Settlement settlement) => NativeSnapshotVersions.Token("settlement", null, settlement.GetUniqueLoadID(), string.Join("|",
            settlement.Tile.tileId.ToString(), settlement.Faction != null ? settlement.Faction.GetUniqueLoadID() : "", settlement.CanTradeNow.ToString()));

        internal static Obs.Settlement SettlementRow(Common.ObservationContext context, Settlement settlement, PlanetTile? from)
        {
            var faction = settlement.Faction;
            var row = new Obs.Settlement
            {
                Id = settlement.GetUniqueLoadID(), Label = settlement.Label ?? "", Tile = settlement.Tile.tileId,
                Player = faction != null && faction.IsPlayer, CanTrade = settlement.CanTradeNow,
                Snapshot = new Obs.SnapshotRef { Context = context, EntityId = settlement.GetUniqueLoadID(), Token = SettlementToken(settlement) },
            };
            if (settlement.TraderKind != null) row = row with { TraderKind = settlement.TraderKind.defName };
            if (from.HasValue) { try { row = row with { DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(from.Value, settlement.Tile) }; } catch (Exception) { } }
            if (faction != null)
            {
                row = row with { Faction = NativeRef.Of(faction) };
                row = row with { FactionDefName = faction.def.defName };
                row = row with { Relation = faction.IsPlayer ? "player" : faction.HostileTo(Faction.OfPlayer) ? "hostile" : "neutral" };
                if (!faction.IsPlayer) row = row with { Goodwill = faction.PlayerGoodwill };
                row = row with { FactionSnapshot = new Obs.SnapshotRef { Context = context, EntityId = faction.GetUniqueLoadID(), Token = FactionToken(faction) } };
            }
            return row;
        }

    }

    internal static class NativeWorldObservationTools
    {
        internal static Obs.WorldReply HandleReadWorld(Obs.WorldRequest request)
        {
            if (!Validate(request, out var failure))
                return new Obs.WorldReply { Outcome = new Obs.WorldReplyOutcome.FailureCase(failure) };
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                return new Obs.WorldReply { Outcome = new Obs.WorldReplyOutcome.FailureCase(failure) };
            try
            {
                var tile = request.Tile is int tileId ? new PlanetTile(tileId) : (PlanetTile?)null;
                var radius = request.SettlementRadius ?? 0d;
                var snapshot = new Obs.WorldSnapshot { Context = context };
                if (tile.HasValue && tile.Value.Valid)
                {
                    var rows = Find.WorldObjects.Settlements
                        .Where(s => Find.WorldGrid.ApproxDistanceInTiles(tile.Value, s.Tile) <= radius)
                        .Select(s => NativeWorldObservation.SettlementRow(context, s, tile)).ToList();
                    snapshot = snapshot with { Settlements = [.. snapshot.Settlements, .. rows] };
                }
                var reply = new Obs.WorldReply { Outcome = snapshot };
                return reply;
            }
            catch (Exception) { return new Obs.WorldReply { Outcome =
                new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native world read could not complete." } }; }
        }

        private static bool Validate(Obs.WorldRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and a nonnegative settlement_radius are required.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.SettlementRadius is null || request.SettlementRadius.GetValueOrDefault() >= 0);
        }
    }
}
