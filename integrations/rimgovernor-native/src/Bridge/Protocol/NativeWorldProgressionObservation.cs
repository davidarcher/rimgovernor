#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Answers rimgovernor/observations_read_world_progression behind the typed
    // ReadScope boundary (observations.proto's WorldProgressionRequest/Reply,
    // WorldMap, FactionState, CaravanState, CaravanAssembly and QuestState).
    // Stateless: every read recomputes from current native state, per
    // native-static-state.md. Read-only; no orders, no UI, no camera.
    internal static class NativeWorldProgressionObservation
    {
        internal const string ToolName = "rimgovernor/observations_read_world_progression";


        private static Obs.ResourceStock StoredItemRow(IGrouping<ThingDef, Thing> group) => new Obs.ResourceStock
        { Definition = new Obs.DefinitionRef { DefName = group.Key.defName }, Units = group.Sum(t => (long)t.stackCount) };

        private static List<Obs.WorldMap> Maps(Common.ObservationContext context, bool includeStorage)
        {
            var rows = new List<Obs.WorldMap>();
            foreach (var m in Find.Maps)
            {
                var row = new Obs.WorldMap { Id = m.uniqueID, Tile = m.Tile.tileId, Home = m.IsPlayerHome, Label = m.Parent?.Label ?? "" };
                row = row with { Pawns = [.. row.Pawns, .. m.mapPawns.FreeColonistsSpawned.Select(p => NativeObservationTools.PawnRow(p, false, context))] };
                row = row with { QuestWorkers = [.. row.QuestWorkers, .. NativeQuestWorkers.Read(m)] };
                if (includeStorage)
                {
                    var stored = m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.IsInValidStorage())
                        .GroupBy(t => t.def).Select(StoredItemRow);
                    row = row with { StoredItems = [.. row.StoredItems, .. stored] };
                }
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
                row = row with { Relation = f.IsPlayer ? "Player" : f.PlayerRelationKind.ToString() };
                if (!f.IsPlayer) row = row with { Goodwill = f.PlayerGoodwill };
                rows.Add(row);
            }
            return rows;
        }

        // Per-caravan home routes: one
        // route fact per player-home map, so Go can tell whether a caravan
        // could path home without depending on the caravan formation catalog
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
                    try { route = route with { EstimatedTicks = (long)CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, m.Tile, caravan) }; }
                    catch (Exception) { /* Estimate is a diagnostic extra; omission does not affect route freshness. */ }
                }
                rows.Add(route);
            }
            return rows;
        }

        private static List<Obs.WorldRoute> SettlementRoutes(Caravan caravan)
        {
            var rows = new List<Obs.WorldRoute>();
            foreach (var settlement in Find.WorldObjects.Settlements.Where(s => s.Faction != null && !s.Faction.IsPlayer).OrderBy(s => s.GetUniqueLoadID()))
            {
                var row = new Obs.WorldRoute { SettlementId = settlement.GetUniqueLoadID(), Destination = settlement.Tile.tileId,
                    Reachable = caravan.CanReach(settlement.Tile), Faction = NativeRef.Of(settlement.Faction), Hostile = settlement.Faction.HostileTo(Faction.OfPlayer), Goodwill = settlement.Faction.PlayerGoodwill };
                if (row.Reachable.GetValueOrDefault())
                    try { row = row with { EstimatedTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, settlement.Tile, caravan) }; }
                    catch (Exception) { /* Unknown estimate cannot admit a safe mission. */ }
                rows.Add(row);
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
                if (c.pather.Moving) row = row with { Destination = c.pather.Destination.tileId };
                row = row with { Pawns = [.. row.Pawns, .. c.PawnsListForReading.Select(p => NativeObservationTools.PawnRow(p, false, context))] };
                row = row with { HomeRoutes = [.. row.HomeRoutes, .. HomeRoutes(c)] };
                row = row with { SettlementRoutes = [.. row.SettlementRoutes, .. SettlementRoutes(c)] };
                row = row with { Inventory = [.. row.Inventory, .. Inventory(c)] };
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
                    row = row with { Pawns = [.. row.Pawns, .. l.ownedPawns.Select(NativeRef.Thing)] };
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
                    if (indexed.choice.rewards.Count == 0) rows.Add(new Obs.QuestReward { ChoiceIndex = (uint)indexed.index });
                    foreach (var reward in indexed.choice.rewards)
                    {
                        var row = new Obs.QuestReward { ChoiceIndex = (uint)indexed.index, Kind = reward.GetType().Name };
                        row = row with { Label = reward.GetDescription(default(RewardsGeneratorParams)) ?? "" };
                        if (reward is Reward_RoyalFavor favor) row = row with { Favor = favor.amount };
                        if (reward is Reward_Goodwill goodwill) { row = row with { Goodwill = goodwill.amount }; if (goodwill.faction != null) row = row with { FactionId = goodwill.faction.GetUniqueLoadID() }; }
                        if (reward is Reward_BestowingCeremony ceremony)
                        {
                            row = row with { Psylink = ceremony.givePsylink ? 1 : 0 };
                            if (ceremony.awardingFaction != null) row = row with { FactionId = ceremony.awardingFaction.GetUniqueLoadID() };
                            if (ceremony.royalTitle != null)
                            {
                                row = row with { TitleDef = ceremony.royalTitle.defName }; row = row with { PermitPoints = ceremony.royalTitle.permitPointsAwarded };
                                if (ceremony.royalTitle.permits != null) row = row with { Permits = [.. row.Permits, .. ceremony.royalTitle.permits.Select(p => p.defName)] };
                            }
                        }
                        if (reward is Reward_Items items)
                        {
                            row = row with { Items = [.. row.Items, .. items.items.Select(t => new Obs.Quantity { DefName = t.def.defName, Units = t.stackCount })] };
                            row = row with { Psylink = items.items.Where(t => t.def == ThingDefOf.PsychicAmplifier).Sum(t => t.stackCount) };
                        }
                        rows.Add(row);
                    }
                }
            }
            var choiceParts = new HashSet<QuestPart>(quest.PartsListForReading.OfType<QuestPart_Choice>()
                .SelectMany(p => p.choices).SelectMany(c => c.questParts));
            foreach (var favor in quest.PartsListForReading.OfType<QuestPart_GiveRoyalFavor>().Where(p => !choiceParts.Contains(p) && p.amount > 0))
                rows.Add(new Obs.QuestReward { Kind = nameof(QuestPart_GiveRoyalFavor), Favor = favor.amount });
            return rows;
        }

        private static readonly AccessTools.FieldRef<QuestPart_ThingsProduced, int> Produced = AccessTools.FieldRefAccess<QuestPart_ThingsProduced, int>("produced");
        private static readonly AccessTools.FieldRef<QuestPart_PlantsHarvested, int> Harvested = AccessTools.FieldRefAccess<QuestPart_PlantsHarvested, int>("harvested");
        private static readonly AccessTools.FieldRef<QuestPart_PawnsKilled, int> Killed = AccessTools.FieldRefAccess<QuestPart_PawnsKilled, int>("killed");

        private static List<Obs.QuestObjective> Objectives(Quest quest)
        {
            var rows = new List<Obs.QuestObjective>();
            rows.AddRange(NativeQuestGravEngine.Read(quest));
            rows.AddRange(NativeQuestSurvey.Read(quest));
            rows.AddRange(NativeQuestSitePeople.Read(quest));
            var decree = quest.root?.defName.StartsWith("Decree_", StringComparison.Ordinal) == true;
            var deadline = decree ? quest.PartsListForReading.OfType<QuestPart_Delay>().Where(p => p.isBad && p.State == QuestPartState.Enabled)
                .Select(p => (long?)Math.Max(0, (long)Find.TickManager.TicksGame + p.TicksLeft)).Min() : null;
            foreach (var part in quest.PartsListForReading)
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Unknown };
                if (part is QuestPartActivable activable) row = row with { Active = activable.State == QuestPartState.Enabled };
                switch (part)
                {
                    case QuestPart_ThingsProduced p:
                        row = row with { Kind = Obs.QuestObjectiveKind.ProduceItem }; row = row with { Def = p.def?.defName ?? "" }; row = row with { Stuff = p.stuff?.defName ?? "" };
                        row = row with { Count = p.count }; row = row with { Produced = Produced(p) }; break;
                    case QuestPart_PlantsHarvested p:
                        row = row with { Kind = Obs.QuestObjectiveKind.HarvestPlant }; row = row with { Def = p.plant?.defName ?? "" };
                        row = row with { Count = p.count }; row = row with { Produced = Harvested(p) }; break;
                    case QuestPart_PawnsKilled p when p.race?.race?.Animal == true:
                        row = row with { Kind = Obs.QuestObjectiveKind.KillAnimals }; row = row with { Def = p.race.defName };
                        row = row with { Count = p.count }; row = row with { Produced = Killed(p) }; break;
                    case QuestPart_RequirementsToAccept p:
                        var report = p.CanAccept();
                        if (report.Accepted) continue;
                        row = row with { Kind = Obs.QuestObjectiveKind.AcceptRequirementUnmet }; row = row with { UnmetRequirement = report.Reason ?? "" }; break;
                    case QuestPart_RequiredShuttleThings p:
                        row = row with { Kind = Obs.QuestObjectiveKind.LoadPawns };
                        if (p.requiredColonistCount >= 0) row = row with { Count = p.requiredColonistCount };
                        var shuttle = p.shuttle?.TryGetComp<CompShuttle>();
                        if (shuttle != null && shuttle.requiredPawns.Count > 0)
                        {
                            row = row with { Kind = Obs.QuestObjectiveKind.LoadNamedPawns };
                            row = row with { PawnIds = [.. row.PawnIds, .. shuttle.requiredPawns.Select(pawn => pawn.GetUniqueLoadID())] };
                        }
                        break;
                    case QuestPart_DropMonumentMarkerCopy _:
                        row = row with { Kind = Obs.QuestObjectiveKind.Monument };
                        var monument = NativeQuestMonuments.Read(quest); if (monument != null) row = row with { Monument = monument };
                        var timer = quest.PartsListForReading.OfType<QuestPart_Delay>().Where(p => p.isBad &&
                            (decree || p.inSignalDisable?.EndsWith(".MonumentCompleted", StringComparison.Ordinal) == true))
                            .OrderBy(p => p.delayTicks).FirstOrDefault();
                        if (timer != null)
                        {
                            if (timer.State == QuestPartState.Enabled) row = row with { DeadlineTicks = Math.Max(0, (long)Find.TickManager.TicksGame + timer.TicksLeft) };
                            else if (quest.State == QuestState.NotYetAccepted && timer.State == QuestPartState.NeverEnabled) row = row with { DurationTicks = timer.delayTicks };
                        }
                        break;
                    case QuestPart_PawnsArrive p when p.pawns.Any(pawn => (pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest))):
                        row = row with { Kind = Obs.QuestObjectiveKind.HostLodgers };
                        row = row with { PawnIds = [.. row.PawnIds, .. p.pawns.Where(pawn => (pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest))).Select(pawn => pawn.GetUniqueLoadID())] };
                        row = NativeQuestShuttles.LodgerMood(quest, row, p.pawns.Where(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)));
                        row = row with { Count = row.PawnIds.Count }; break;
                }
                if (deadline.HasValue && (row.Kind.GetValueOrDefault() == Obs.QuestObjectiveKind.ProduceItem || row.Kind.GetValueOrDefault() == Obs.QuestObjectiveKind.HarvestPlant || row.Kind.GetValueOrDefault() == Obs.QuestObjectiveKind.KillAnimals)) row = row with { DeadlineTicks = deadline.Value };
                var workload = NativeQuestWorkload.Read(row, quest); if (workload != null) row = row with { Workload = workload };
                rows.Add(row);
            }
            var hosted = quest.PartsListForReading.OfType<QuestPart_ExtraFaction>().SelectMany(p => p.affectedPawns)
                .Where(p => p.HasExtraHomeFaction(quest) || p.HasExtraMiniFaction(quest))
                .Concat(quest.State == QuestState.NotYetAccepted
                    ? quest.PartsListForReading.OfType<QuestPart_RequirementsToAcceptBedroom>().SelectMany(p => p.targetPawns)
                    : Enumerable.Empty<Pawn>()).Distinct().ToArray();
            if (hosted.Length > 0 && !rows.Any(r => r.Kind.GetValueOrDefault() == Obs.QuestObjectiveKind.HostLodgers))
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HostLodgers, Count = hosted.Length };
                row = row with { PawnIds = [.. row.PawnIds, .. hosted.Select(p => p.GetUniqueLoadID())] };
                row = NativeQuestShuttles.LodgerMood(quest, row, hosted);
                rows.Add(row);
            }
            if (quest.TicksUntilExpiry >= 0 && quest.State == QuestState.NotYetAccepted)
                rows.Add(new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Expiry, DeadlineTicks = (long)Find.TickManager.TicksGame + quest.TicksUntilExpiry });
            rows.AddRange(NativeQuestRefugees.Read(quest));
            rows.AddRange(NativeQuestHackGift.Read(quest));
            return rows;
        }

        // Only a still-active request is reported: once the live
        // TradeRequestComp is fulfilled, the QuestPart_InitiateTradeRequest itself
        // typically remains on the quest (removing it is the responsibility
        // of whatever quest-script listener chain reacts to the settlement's
        // fulfillment signal, which a minimal quest need not carry), so
        // reporting on the part's mere presence would keep the objective
        // live forever after a real fulfillment.
        private static List<Obs.QuestTradeRequest> TradeRequests(Quest quest)
        {
            var rows = new List<Obs.QuestTradeRequest>();
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>())
            {
                if (part.settlement?.GetComponent<TradeRequestComp>()?.ActiveRequest != true) continue;
                var row = new Obs.QuestTradeRequest { Resource = part.requestedThingDef?.defName ?? "", Count = part.requestedCount };
                row = row with { Destination = part.settlement.Tile.tileId };
                rows.Add(row);
            }
            return rows;
        }

        // First map any look target of the quest sits on (a map parent's own
        // map counts); absent for a quest anchored only to a world object.
        private static int? QuestMapId(Quest quest)
        {
            foreach (var target in quest.QuestLookTargets)
            {
                var map = target.Map ?? (target.HasWorldObject ? (target.WorldObject as MapParent)?.Map : null);
                if (map != null) return map.uniqueID;
            }
            return null;
        }

        private static List<Obs.QuestState> Quests(Common.ObservationContext context)
        {
            var rows = new List<Obs.QuestState>();
            foreach (var q in Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI))
            {
                var row = new Obs.QuestState
                {
                    Id = q.GetUniqueLoadID(), Label = q.name ?? "", Description = q.description.ToString() ?? "",
                    State = NativeEnums.Quest(q.State), AcceptedTick = q.acceptanceTick, ExpiresInTicks = q.TicksUntilExpiry,
                    RequiresAccepter = q.RequiresAccepter, ScriptDef = q.root?.defName ?? "",
                    CanAccept = q.State == QuestState.NotYetAccepted && QuestUtility.CanAcceptQuest(q).Accepted,
                    ChoicePartCount = q.PartsListForReading.OfType<QuestPart_Choice>().Count(),
                    // The quest row's identity-and-state token.
                    Snapshot = new Obs.SnapshotRef { Context = context, EntityId = q.GetUniqueLoadID(), Token = NativeQuestOperations.Token(q) },
                };
                var faction = q.InvolvedFactions.FirstOrDefault(f => f != null && !f.IsPlayer);
                if (faction != null) row = row with { FactionId = faction.GetUniqueLoadID() };
                var mapId = QuestMapId(q);
                if (mapId.HasValue) row = row with { MapId = mapId.Value };
                row = row with { EligiblePawns = [.. row.EligiblePawns, .. Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).Select(NativeRef.Thing)] };
                row = row with { TradeRequests = [.. row.TradeRequests, .. TradeRequests(q)] };
                var asker = q.root?.defName.StartsWith("Decree_", StringComparison.Ordinal) == true
                    ? q.PartsListForReading.OfType<QuestPart_SituationalThought>().Where(p => p.def?.defName == "DecreeUnmet").Select(p => p.pawn).Distinct().ToArray() : Array.Empty<Pawn>();
                if (asker.Length == 1 && asker[0] != null)
                {
                    row = row with { AskerPawnId = asker[0].GetUniqueLoadID() };
                    if (asker[0].Faction != null) row = row with { AskerFactionPlayer = asker[0].Faction.IsPlayer };
                }
                row = row with { ViolentQuestsAllowed = Find.Storyteller.difficulty.allowViolentQuests };
                var threatPoints = NativeQuestThreats.Read(q);
                if (threatPoints.HasValue) row = row with { ThreatPoints = threatPoints.Value };
                row = row with { Shuttles = [.. row.Shuttles, .. NativeQuestShuttles.Read(q)] };
                row = row with { DeparturePawnIds = [.. row.DeparturePawnIds, .. q.PartsListForReading.OfType<QuestPart_LendColonistsToFaction>()
                    .SelectMany(p => p.LentColonistsListForReading).OfType<Pawn>().Select(p => p.GetUniqueLoadID()).Distinct()] };
                row = row with { Objectives = [.. row.Objectives, .. Objectives(q)] };
                row = row with { Rewards = [.. row.Rewards, .. Rewards(q)] };
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
            var quests = Quests(context);
            var snapshot = new Obs.WorldProgressionSnapshot
            {
                Context = context,
            };
            snapshot = snapshot with { Maps = [.. snapshot.Maps, .. maps] };
            snapshot = snapshot with { Factions = [.. snapshot.Factions, .. factions] };
            snapshot = snapshot with { Caravans = [.. snapshot.Caravans, .. caravans] };
            snapshot = snapshot with { Assemblies = [.. snapshot.Assemblies, .. assemblies] };
            snapshot = snapshot with { Quests = [.. snapshot.Quests, .. quests] };
            snapshot = snapshot with { Sites = [.. snapshot.Sites, .. NativeWorldSites.Read(context)] };
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
            if (!ProtoBoundary.TryParse(ctx, NativeWorldProgressionObservation.ToolName, request!, out Obs.WorldProgressionRequest? parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Outcome = new Obs.WorldProgressionReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleReadWorldProgression(parsed), cancellationToken).ConfigureAwait(false);
        }

        internal static Obs.WorldProgressionReply HandleReadWorldProgression(Obs.WorldProgressionRequest request)
        {
            if (!Validate(request, out var failure))
                return new Obs.WorldProgressionReply { Outcome = new Obs.WorldProgressionReplyOutcome.FailureCase(failure) };
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                return new Obs.WorldProgressionReply { Outcome = new Obs.WorldProgressionReplyOutcome.FailureCase(failure) };
            try
            {
                var snapshot = NativeWorldProgressionObservation.Build(context, request.IncludeStorage.GetValueOrDefault());
                var reply = new Obs.WorldProgressionReply { Outcome = snapshot };
                return reply;
            }
            catch (Exception) { return new Obs.WorldProgressionReply { Outcome =
                new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native world progression could not be read completely." } }; }
        }

        private static bool Validate(Obs.WorldProgressionRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity are required.");
            return request?.Scope?.ExpectedIdentity != null;
        }
    }
}
