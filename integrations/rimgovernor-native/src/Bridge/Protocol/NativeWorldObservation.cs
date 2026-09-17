#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Closes the observations_read_world gap: WorldRequest/WorldReply and the
    // Settlement message (including its snapshot/faction_snapshot CAS tokens)
    // were already fully defined with no native handler and no Go consumer
    // (confirmed by exhaustive search before writing this file) -- the same
    // "proto ahead of consumer" pattern NativeWorldProgressionObservation and
    // NativeCaravanCatalog already closed for their own requests. This handler
    // only surfaces settlements near a requested tile with fresh CAS tokens;
    // WorldTile terrain facts remain unimplemented (a distinct, unrelated
    // follow-up, not needed by any current consumer).
    //
    // Settlement/faction tokens are self-computed, the same known pattern
    // NativeTradeOperations documents for its own session/trader/negotiator
    // tokens: no other observation exposes a per-settlement CAS snapshot yet.
    // Go's settlement-gift boundary is this handler's first consumer, reading
    // the settlement at an already-known caravan tile (from
    // ReadWorldProgression) to learn whether -- and to which faction -- that
    // caravan currently sits.
    internal static class NativeWorldObservation
    {
        internal const string ToolName = "rimgovernor/observations_read_world";

        private static string Hash(string text)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Recomputable from already-observed FactionState facts (id, goodwill,
        // hostile, player) alone: a caller can reproduce this hash from an
        // ordinary world-progression/faction read without depending on this
        // handler for the value, only for confirming freshness against native.
        internal static string FactionToken(Faction faction) => "faction-" + Hash(string.Join("|",
            faction.GetUniqueLoadID(), faction.IsPlayer.ToString(),
            faction.IsPlayer ? "" : faction.PlayerGoodwill.ToString(), faction.HostileTo(Faction.OfPlayer).ToString()));

        // Recomputable the same way from Settlement facts (id, tile, faction id,
        // CanTradeNow); GiftCaravanSilver's Execute recomputes and compares this
        // exact token, so a settlement that changed trade eligibility since the
        // read is refused rather than acted on.
        internal static string SettlementToken(Settlement settlement) => "settlement-" + Hash(string.Join("|",
            settlement.GetUniqueLoadID(), settlement.Tile.tileId.ToString(),
            settlement.Faction != null ? settlement.Faction.GetUniqueLoadID() : "", settlement.CanTradeNow.ToString()));

        internal static Obs.Settlement SettlementRow(Common.ObservationContext context, Settlement settlement, PlanetTile? from)
        {
            var faction = settlement.Faction;
            var row = new Obs.Settlement
            {
                Id = settlement.GetUniqueLoadID(), Label = settlement.Label ?? "", Tile = settlement.Tile.tileId,
                Player = faction != null && faction.IsPlayer,
                Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = settlement.GetUniqueLoadID(), Token = SettlementToken(settlement) },
            };
            if (from.HasValue) { try { row.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(from.Value, settlement.Tile); } catch (Exception) { } }
            if (faction != null)
            {
                row.FactionId = faction.GetUniqueLoadID();
                row.FactionDefName = faction.def.defName;
                row.Relation = faction.IsPlayer ? "player" : faction.HostileTo(Faction.OfPlayer) ? "hostile" : "neutral";
                if (!faction.IsPlayer) row.Goodwill = faction.PlayerGoodwill;
                row.FactionSnapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = faction.GetUniqueLoadID(), Token = FactionToken(faction) };
            }
            return row;
        }

        internal static Obs.Completeness Complete(int count) => new Obs.Completeness
        { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
    }

    public sealed class NativeWorldObservationTools
    {
        [Tool(NativeWorldObservation.ToolName, Title = "Read settlements near a world tile",
            Description = "Official WorldRequest ProtoJSON. Read-only settlements within settlement_radius tiles (default 0: exact tile) of the requested tile, each with fresh faction relation/goodwill and self-computed CAS tokens. WorldTile terrain facts are not yet implemented. Does not advance time or issue orders.")]
        [ToolResponse("payload", "string", "Official observations WorldReply ProtoJSON.", Always = true)]
        public async Task<object> ReadWorld(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a WorldRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeWorldObservation.ToolName, request!, Obs.WorldRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.WorldReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.WorldReply { Failure = failure });
                try
                {
                    var tile = parsed.HasTile ? new PlanetTile(parsed.Tile) : (PlanetTile?)null;
                    var radius = parsed.HasSettlementRadius ? parsed.SettlementRadius : 0d;
                    var snapshot = new Obs.WorldSnapshot { Context = context.Clone() };
                    if (tile.HasValue && tile.Value.Valid)
                    {
                        var rows = Find.WorldObjects.Settlements
                            .Where(s => Find.WorldGrid.ApproxDistanceInTiles(tile.Value, s.Tile) <= radius)
                            .Select(s => NativeWorldObservation.SettlementRow(context, s, tile)).ToList();
                        snapshot.Settlements.Add(rows);
                    }
                    snapshot.Completeness = NativeWorldObservation.Complete(snapshot.Settlements.Count);
                    var reply = new Obs.WorldReply { Observed = snapshot };
                    if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes)
                        return ProtoBoundary.Encode(new Obs.WorldReply { Unavailable =
                            new Common.Unavailable { Reason = Common.UnavailableReason.LimitExceeded, Detail = "World settlement census exceeds 1MiB." } });
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.WorldReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native world read could not complete." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Obs.WorldRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and a nonnegative settlement_radius are required.");
            return request?.Scope?.ExpectedIdentity != null
                && (!request.HasSettlementRadius || request.SettlementRadius >= 0)
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length == 0));
        }
    }
}
