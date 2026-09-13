#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Ports the legacy home/world_progression JSON tool (WorldProgressionTools
    // in ../WorldProgressionTool.cs) behind the typed ReadScope boundary, the
    // same "proto ahead of use" closing pattern already used for
    // NativeCaravanCatalog. observations.proto's WorldProgressionRequest/
    // Reply, WorldMap, FactionState, CaravanState, CaravanAssembly and
    // QuestState were already fully defined; this closes the gap that no
    // native handler yet answered rimgovernor/observations_read_world_progression
    // (confirmed by exhaustive search of the C# mod before writing this file).
    // Stateless: every read recomputes from current native state, per
    // native-static-state.md. Read-only; no orders, no UI, no camera.
    internal static class NativeWorldProgressionObservation
    {
        internal const string ToolName = "rimgovernor/observations_read_world_progression";

        private static Obs.Completeness Complete(int count) => new Obs.Completeness
        { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };

        private static Obs.EntityRef PawnEntity(Pawn pawn, int? mapId) => new Obs.EntityRef
        { Id = pawn.GetUniqueLoadID(), DefName = pawn.def.defName, Label = pawn.LabelShort, MapId = mapId ?? -1 };

        private static Obs.ResourceStock StoredItemRow(IGrouping<ThingDef, Thing> group) => new Obs.ResourceStock
        { Definition = new Obs.DefinitionRef { DefName = group.Key.defName, Label = group.Key.label ?? "" }, Units = group.Sum(t => (long)t.stackCount) };

        private static List<Obs.WorldMap> Maps(Common.ObservationContext context, bool includeStorage)
        {
            var rows = new List<Obs.WorldMap>();
            foreach (var m in Find.Maps)
            {
                var row = new Obs.WorldMap { Id = m.uniqueID, Tile = m.Tile.tileId, Home = m.IsPlayerHome, Label = m.Parent?.Label ?? "" };
                row.Pawns.Add(m.mapPawns.FreeColonistsSpawned.Select(p => NativeObservationTools.PawnRow(p, false, context)));
                if (includeStorage)
                {
                    var stored = m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.IsInValidStorage())
                        .GroupBy(t => t.def).Select(StoredItemRow);
                    row.StoredItems.Add(stored);
                }
                row.Completeness = Complete(row.Pawns.Count + row.StoredItems.Count);
                rows.Add(row);
            }
            return rows;
        }

        private static List<Obs.FactionState> Factions()
        {
            var rows = new List<Obs.FactionState>();
            foreach (var f in Find.FactionManager.AllFactionsVisible)
            {
                var row = new Obs.FactionState { Id = f.GetUniqueLoadID(), Label = f.Name ?? "", Player = f.IsPlayer, Hostile = f.HostileTo(Faction.OfPlayer) };
                row.Relation = f.IsPlayer ? "Player" : f.PlayerRelationKind.ToString();
                if (!f.IsPlayer) row.Goodwill = f.PlayerGoodwill;
                rows.Add(row);
            }
            return rows;
        }

        // Mirrors legacy WorldProgressionTools' per-caravan homeRoutes: one
        // route fact per player-home map, so Go can tell whether a caravan
        // could path home without depending on the FormCaravan catalog
        // (which only ever evaluates one destination at a time).
        private static List<Obs.WorldRoute> HomeRoutes(Caravan caravan)
        {
            var rows = new List<Obs.WorldRoute>();
            foreach (var m in Find.Maps.Where(m => m.IsPlayerHome))
            {
                var reachable = caravan.CanReach(m.Tile);
                var route = new Obs.WorldRoute { Destination = m.Tile.tileId, Reachable = reachable };
                if (reachable)
                {
                    try { route.EstimatedTicks = (long)CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, m.Tile, caravan); }
                    catch (Exception) { /* Estimate is a diagnostic extra; omission does not affect route freshness. */ }
                }
                rows.Add(route);
            }
            return rows;
        }

        private static List<Obs.Quantity> Inventory(Caravan caravan)
        {
            var rows = new List<Obs.Quantity>();
            foreach (var group in CaravanInventoryUtility.AllInventoryItems(caravan).GroupBy(t => t.def))
                rows.Add(new Obs.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) });
            return rows;
        }

        private static List<Obs.CaravanState> Caravans(Common.ObservationContext context)
        {
            var rows = new List<Obs.CaravanState>();
            foreach (var c in Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled))
            {
                var row = new Obs.CaravanState
                {
                    Caravan = new Obs.EntityRef { Id = c.GetUniqueLoadID(), Label = c.Label ?? "" },
                    Tile = c.Tile.tileId, Moving = c.pather.Moving, MovingNow = c.pather.MovingNow, Resting = c.NightResting,
                    MassUsage = c.MassUsage, MassCapacity = c.MassCapacity,
                    FoodDays = c.DaysWorthOfFood.days, FoodRotDays = c.DaysWorthOfFood.tillRot,
                };
                if (c.pather.Moving) row.Destination = c.pather.Destination.tileId;
                row.Pawns.Add(c.PawnsListForReading.Select(p => NativeObservationTools.PawnRow(p, false, context)));
                row.HomeRoutes.Add(HomeRoutes(c));
                row.Inventory.Add(Inventory(c));
                row.Completeness = Complete(row.Pawns.Count);
                rows.Add(row);
            }
            return rows;
        }

        private static List<Obs.CaravanAssembly> Assemblies()
        {
            var rows = new List<Obs.CaravanAssembly>();
            foreach (var m in Find.Maps)
            {
                foreach (var l in m.lordManager.lords.Where(l => l.LordJob is LordJob_FormAndSendCaravan))
                {
                    var job = (LordJob_FormAndSendCaravan)l.LordJob;
                    var row = new Obs.CaravanAssembly { Id = l.GetUniqueLoadID(), MapId = m.uniqueID, Status = job.Status.ToString(), GatheringItems = job.GatheringItemsNow };
                    row.Pawns.Add(l.ownedPawns.Select(p => PawnEntity(p, m.uniqueID)));
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static List<Obs.QuestReward> Rewards(Quest quest)
        {
            var rows = new List<Obs.QuestReward>();
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_Choice>())
            {
                foreach (var indexed in part.choices.Select((choice, index) => (choice, index)))
                {
                    foreach (var reward in indexed.choice.rewards)
                    {
                        var row = new Obs.QuestReward { ChoiceIndex = (uint)indexed.index, Kind = reward.GetType().Name };
                        row.Label = BridgeCommon.SafeString(() => reward.GetDescription(default(RewardsGeneratorParams))) ?? "";
                        if (reward is Reward_Items items)
                            row.Items.Add(items.items.Select(t => new Obs.Quantity { DefName = t.def.defName, Units = t.stackCount }));
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        private static List<Obs.QuestTradeRequest> TradeRequests(Quest quest)
        {
            var rows = new List<Obs.QuestTradeRequest>();
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>())
            {
                var row = new Obs.QuestTradeRequest { Resource = part.requestedThingDef?.defName ?? "", Count = part.requestedCount };
                if (part.settlement != null) row.Destination = part.settlement.Tile.tileId;
                rows.Add(row);
            }
            return rows;
        }

        private static List<Obs.QuestState> Quests()
        {
            var rows = new List<Obs.QuestState>();
            foreach (var q in Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI))
            {
                var row = new Obs.QuestState
                {
                    Id = q.GetUniqueLoadID(), Label = q.name ?? "", Description = q.description.ToString() ?? "",
                    State = q.State.ToString(), AcceptedTick = q.acceptanceTick, ExpiresInTicks = q.TicksUntilExpiry,
                    RequiresAccepter = q.RequiresAccepter,
                    CanAccept = q.State == QuestState.NotYetAccepted && QuestUtility.CanAcceptQuest(q).Accepted,
                };
                row.EligiblePawns.Add(Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).Select(p => PawnEntity(p, p.MapHeld?.uniqueID)));
                row.TradeRequests.Add(TradeRequests(q));
                row.Rewards.Add(Rewards(q));
                rows.Add(row);
            }
            return rows;
        }

        internal static Obs.WorldProgressionSnapshot Build(Common.ObservationContext context, bool includeStorage)
        {
            var maps = Maps(context, includeStorage);
            var factions = Factions();
            var caravans = Caravans(context);
            var assemblies = Assemblies();
            var quests = Quests();
            var snapshot = new Obs.WorldProgressionSnapshot
            {
                Context = context.Clone(),
                Completeness = Complete(maps.Count + factions.Count + caravans.Count + assemblies.Count + quests.Count),
            };
            snapshot.Maps.Add(maps);
            snapshot.Factions.Add(factions);
            snapshot.Caravans.Add(caravans);
            snapshot.Assemblies.Add(assemblies);
            snapshot.Quests.Add(quests);
            return snapshot;
        }
    }

    public sealed class NativeWorldProgressionObservationTools
    {
        [Tool(NativeWorldProgressionObservation.ToolName, Title = "Read caravans, assemblies, quests and factions",
            Description = "Official WorldProgressionRequest ProtoJSON. Read-only census of every map's colonists and (optionally) stored items, player caravans and their home-route reachability/inventory, native caravan-assembly jobs and visible non-hidden quests. Does not advance time or issue orders.")]
        [ToolResponse("payload", "string", "Official observations WorldProgressionReply ProtoJSON.", Always = true)]
        public async Task<object> ReadWorldProgression(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a WorldProgressionRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeWorldProgressionObservation.ToolName, request!, Obs.WorldProgressionRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Failure = failure });
                try
                {
                    var snapshot = NativeWorldProgressionObservation.Build(context, parsed.IncludeStorage);
                    var reply = new Obs.WorldProgressionReply { Observed = snapshot };
                    if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes)
                        return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Unavailable =
                            new Common.Unavailable { Reason = Common.UnavailableReason.LimitExceeded, Detail = "World progression census exceeds 1MiB." } });
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native world progression could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Obs.WorldProgressionRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and page1..256 are required.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length == 0));
        }
    }
}
