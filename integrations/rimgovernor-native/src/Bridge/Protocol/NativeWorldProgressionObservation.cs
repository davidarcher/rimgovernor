#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

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
        { Definition = new Obs.DefinitionRef { DefName = group.Key.defName, Label = group.Key.label ?? "" }, Units = group.Sum(t => (long)t.stackCount) };

        private static List<Obs.WorldMap> Maps(Common.ObservationContext context, bool includeStorage)
        {
            var rows = new List<Obs.WorldMap>();
            foreach (var m in Find.Maps)
            {
                var row = new Obs.WorldMap { Id = m.uniqueID, Tile = m.Tile.tileId, Home = m.IsPlayerHome, Label = m.Parent?.Label ?? "" };
                row.Pawns.Add(m.mapPawns.FreeColonistsSpawned.Select(p => NativeObservationTools.PawnRow(p, false, context)));
                row.QuestWorkers.Add(NativeQuestWorkers.Read(m));
                if (includeStorage)
                {
                    var stored = m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.IsInValidStorage())
                        .GroupBy(t => t.def).Select(StoredItemRow);
                    row.StoredItems.Add(stored);
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
                row.Relation = f.IsPlayer ? "Player" : f.PlayerRelationKind.ToString();
                if (!f.IsPlayer) row.Goodwill = f.PlayerGoodwill;
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
                    row.Pawns.Add(l.ownedPawns.Select(NativeRef.Thing));
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
                        row.Label = reward.GetDescription(default(RewardsGeneratorParams)) ?? "";
                        if (reward is Reward_RoyalFavor favor) row.Favor = favor.amount;
                        if (reward is Reward_Goodwill goodwill) { row.Goodwill = goodwill.amount; if (goodwill.faction != null) row.FactionId = goodwill.faction.GetUniqueLoadID(); }
                        if (reward is Reward_BestowingCeremony ceremony)
                        {
                            row.Psylink = ceremony.givePsylink ? 1 : 0;
                            if (ceremony.awardingFaction != null) row.FactionId = ceremony.awardingFaction.GetUniqueLoadID();
                            if (ceremony.royalTitle != null)
                            {
                                row.TitleDef = ceremony.royalTitle.defName; row.PermitPoints = ceremony.royalTitle.permitPointsAwarded;
                                if (ceremony.royalTitle.permits != null) row.Permits.Add(ceremony.royalTitle.permits.Select(p => p.defName));
                            }
                        }
                        if (reward is Reward_Items items)
                        {
                            row.Items.Add(items.items.Select(t => new Obs.Quantity { DefName = t.def.defName, Units = t.stackCount }));
                            row.Psylink = items.items.Where(t => t.def == ThingDefOf.PsychicAmplifier).Sum(t => t.stackCount);
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
            var decree = quest.root?.defName.StartsWith("Decree_", StringComparison.Ordinal) == true;
            var deadline = decree ? quest.PartsListForReading.OfType<QuestPart_Delay>().Where(p => p.isBad && p.State == QuestPartState.Enabled)
                .Select(p => (long?)Math.Max(0, (long)Find.TickManager.TicksGame + p.TicksLeft)).Min() : null;
            foreach (var part in quest.PartsListForReading)
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Unknown };
                if (part is QuestPartActivable activable) row.Active = activable.State == QuestPartState.Enabled;
                switch (part)
                {
                    case QuestPart_ThingsProduced p:
                        row.Kind = Obs.QuestObjectiveKind.ProduceItem; row.Def = p.def?.defName ?? ""; row.Stuff = p.stuff?.defName ?? "";
                        row.Count = p.count; row.Produced = Produced(p); break;
                    case QuestPart_PlantsHarvested p:
                        row.Kind = Obs.QuestObjectiveKind.HarvestPlant; row.Def = p.plant?.defName ?? "";
                        row.Count = p.count; row.Produced = Harvested(p); break;
                    case QuestPart_PawnsKilled p when p.race?.race?.Animal == true:
                        row.Kind = Obs.QuestObjectiveKind.KillAnimals; row.Def = p.race.defName;
                        row.Count = p.count; row.Produced = Killed(p); break;
                    case QuestPart_RequirementsToAccept p:
                        var report = p.CanAccept();
                        if (report.Accepted) continue;
                        row.Kind = Obs.QuestObjectiveKind.AcceptRequirementUnmet; row.UnmetRequirement = report.Reason ?? ""; break;
                    case QuestPart_RequiredShuttleThings p:
                        row.Kind = Obs.QuestObjectiveKind.LoadPawns;
                        if (p.requiredColonistCount >= 0) row.Count = p.requiredColonistCount;
                        var shuttle = p.shuttle?.TryGetComp<CompShuttle>();
                        if (shuttle != null && shuttle.requiredPawns.Count > 0)
                        {
                            row.Kind = Obs.QuestObjectiveKind.LoadNamedPawns;
                            row.PawnIds.Add(shuttle.requiredPawns.Select(pawn => pawn.GetUniqueLoadID()));
                        }
                        break;
                    case QuestPart_DropMonumentMarkerCopy _:
                        row.Kind = Obs.QuestObjectiveKind.Monument;
                        var monument = NativeQuestMonuments.Read(quest); if (monument != null) row.Monument = monument;
                        var timer = quest.PartsListForReading.OfType<QuestPart_Delay>().Where(p => p.isBad &&
                            (decree || p.inSignalDisable?.EndsWith(".MonumentCompleted", StringComparison.Ordinal) == true))
                            .OrderBy(p => p.delayTicks).FirstOrDefault();
                        if (timer != null)
                        {
                            if (timer.State == QuestPartState.Enabled) row.DeadlineTicks = Math.Max(0, (long)Find.TickManager.TicksGame + timer.TicksLeft);
                            else if (quest.State == QuestState.NotYetAccepted && timer.State == QuestPartState.NeverEnabled) row.DurationTicks = timer.delayTicks;
                        }
                        break;
                    case QuestPart_PawnsArrive p when p.pawns.Any(pawn => (pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest))):
                        row.Kind = Obs.QuestObjectiveKind.HostLodgers;
                        row.PawnIds.Add(p.pawns.Where(pawn => (pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest))).Select(pawn => pawn.GetUniqueLoadID()));
                        NativeQuestShuttles.LodgerMood(quest, row, p.pawns.Where(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)));
                        row.Count = row.PawnIds.Count; break;
                }
                if (deadline.HasValue && (row.Kind == Obs.QuestObjectiveKind.ProduceItem || row.Kind == Obs.QuestObjectiveKind.HarvestPlant || row.Kind == Obs.QuestObjectiveKind.KillAnimals)) row.DeadlineTicks = deadline.Value;
                var workload = NativeQuestWorkload.Read(row, quest); if (workload != null) row.Workload = workload;
                rows.Add(row);
            }
            var hosted = quest.PartsListForReading.OfType<QuestPart_ExtraFaction>().SelectMany(p => p.affectedPawns)
                .Where(p => p.HasExtraHomeFaction(quest) || p.HasExtraMiniFaction(quest))
                .Concat(quest.State == QuestState.NotYetAccepted
                    ? quest.PartsListForReading.OfType<QuestPart_RequirementsToAcceptBedroom>().SelectMany(p => p.targetPawns)
                    : Enumerable.Empty<Pawn>()).Distinct().ToArray();
            if (hosted.Length > 0 && !rows.Any(r => r.Kind == Obs.QuestObjectiveKind.HostLodgers))
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HostLodgers, Count = hosted.Length };
                row.PawnIds.Add(hosted.Select(p => p.GetUniqueLoadID()));
                NativeQuestShuttles.LodgerMood(quest, row, hosted);
                rows.Add(row);
            }
            if (quest.TicksUntilExpiry >= 0 && quest.State == QuestState.NotYetAccepted)
                rows.Add(new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Expiry, DeadlineTicks = (long)Find.TickManager.TicksGame + quest.TicksUntilExpiry });
            rows.AddRange(NativeQuestRefugees.Read(quest));
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
                row.Destination = part.settlement.Tile.tileId;
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
                    Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = q.GetUniqueLoadID(), Token = NativeQuestOperations.Token(q) },
                };
                var faction = q.InvolvedFactions.FirstOrDefault(f => f != null && !f.IsPlayer);
                if (faction != null) row.FactionId = faction.GetUniqueLoadID();
                var mapId = QuestMapId(q);
                if (mapId.HasValue) row.MapId = mapId.Value;
                row.EligiblePawns.Add(Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).Select(NativeRef.Thing));
                row.TradeRequests.Add(TradeRequests(q));
                var asker = q.root?.defName.StartsWith("Decree_", StringComparison.Ordinal) == true
                    ? q.PartsListForReading.OfType<QuestPart_SituationalThought>().Where(p => p.def?.defName == "DecreeUnmet").Select(p => p.pawn).Distinct().ToArray() : Array.Empty<Pawn>();
                if (asker.Length == 1 && asker[0] != null)
                {
                    row.AskerPawnId = asker[0].GetUniqueLoadID();
                    if (asker[0].Faction != null) row.AskerFactionPlayer = asker[0].Faction.IsPlayer;
                }
                row.ViolentQuestsAllowed = Find.Storyteller.difficulty.allowViolentQuests;
                var threatPoints = NativeQuestThreats.Read(q);
                if (threatPoints.HasValue) row.ThreatPoints = threatPoints.Value;
                row.Shuttles.Add(NativeQuestShuttles.Read(q));
                row.DeparturePawnIds.Add(q.PartsListForReading.OfType<QuestPart_LendColonistsToFaction>()
                    .SelectMany(p => p.LentColonistsListForReading).OfType<Pawn>().Select(p => p.GetUniqueLoadID()).Distinct());
                row.Objectives.Add(Objectives(q));
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
            var quests = Quests(context);
            var snapshot = new Obs.WorldProgressionSnapshot
            {
                Context = context.Clone(),
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
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Failure = failure });
                try
                {
                    var snapshot = NativeWorldProgressionObservation.Build(context, parsed.IncludeStorage);
                    var reply = new Obs.WorldProgressionReply { Observed = snapshot };
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.WorldProgressionReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native world progression could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Obs.WorldProgressionRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity are required.");
            return request?.Scope?.ExpectedIdentity != null;
        }
    }
}
