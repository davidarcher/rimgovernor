#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Bridge.Contracts;
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


        private static List<Obs.WorldMap> Maps(Common.ObservationContext context)
        {
            var rows = new List<Obs.WorldMap>();
            foreach (var m in Find.Maps)
            {
                var row = new Obs.WorldMap { Id = m.uniqueID, Tile = m.Tile.tileId, Home = m.IsPlayerHome };
                row = row with { Pawns = [.. row.Pawns, .. m.mapPawns.FreeColonistsSpawned.Select(p => NativeObservationTools.PawnRow(p, context))] };
                row = row with { QuestWorkers = [.. row.QuestWorkers, .. NativeQuestWorkers.Read(m)] };
                rows.Add(row);
            }
            return rows;
        }

        private static List<Obs.FactionState> Factions()
        {
            var rows = new List<Obs.FactionState>();
            foreach (var f in Find.FactionManager.AllFactionsVisible)
            {
                var row = new Obs.FactionState { Id = f.GetUniqueLoadID(), Player = f.IsPlayer, Hostile = f.HostileTo(Faction.OfPlayer) };
                rows.Add(row);
            }
            return rows;
        }

        // Per-caravan home routes: one
        // route fact per player-home map, so the brain can tell whether a caravan
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
                    Caravan = new Obs.EntityRef { Id = c.GetUniqueLoadID(), DefName = c.def.defName, Label = c.Label ?? "" },
                    Tile = c.Tile.tileId, Moving = c.pather.Moving,
                    MassUsage = c.MassUsage, MassCapacity = c.MassCapacity,
                    FoodDays = c.DaysWorthOfFood.days, FoodRotDays = c.DaysWorthOfFood.tillRot,
                };
                if (c.pather.Moving) row = row with { Destination = c.pather.Destination.tileId };
                row = row with { Pawns = [.. row.Pawns, .. c.PawnsListForReading.Select(p => NativeObservationTools.PawnRow(p, context))] };
                row = row with { HomeRoutes = [.. row.HomeRoutes, .. HomeRoutes(c)] };
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
                    var row = new Obs.CaravanAssembly { Id = l.GetUniqueLoadID(), MapId = m.uniqueID };
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
                        var row = new Obs.QuestReward { ChoiceIndex = (uint)indexed.index };
                                                if (reward is Reward_RoyalFavor favor) row = row with { Favor = favor.amount };
                        if (reward is Reward_Goodwill goodwill) row = row with { Goodwill = goodwill.amount };
                        if (reward is Reward_BestowingCeremony ceremony)
                        {
                            row = row with { Psylink = ceremony.givePsylink ? 1 : 0 };
                            if (ceremony.royalTitle != null)
                            {
                                row = row with { PermitPoints = ceremony.royalTitle.permitPointsAwarded };
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
                rows.Add(new Obs.QuestReward { Favor = favor.amount });
            return rows;
        }

        private static readonly AccessTools.FieldRef<QuestPart_ThingsProduced, int> Produced = AccessTools.FieldRefAccess<QuestPart_ThingsProduced, int>("produced");
        private static readonly AccessTools.FieldRef<QuestPart_PlantsHarvested, int> Harvested = AccessTools.FieldRefAccess<QuestPart_PlantsHarvested, int>("harvested");
        private static readonly AccessTools.FieldRef<QuestPart_PawnsKilled, int> Killed = AccessTools.FieldRefAccess<QuestPart_PawnsKilled, int>("killed");

        // The objective one quest part states; null for a part that states none the brain reads.
        private static Obs.QuestObjective? Objective(Quest quest, QuestPart part, bool decree)
        {
            switch (part)
            {
                case QuestPart_ThingsProduced p:
                    return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.ProduceItem, Def = p.def?.defName ?? "", Stuff = p.stuff?.defName ?? "",
                        Count = p.count, Produced = Produced(p) };
                case QuestPart_PlantsHarvested p:
                    return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HarvestPlant, Def = p.plant?.defName ?? "", Count = p.count, Produced = Harvested(p) };
                case QuestPart_PawnsKilled p when p.race?.race?.Animal == true:
                    return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.KillAnimals, Def = p.race.defName, Count = p.count, Produced = Killed(p) };
                case QuestPart_RequirementsToAccept p:
                    var report = p.CanAccept();
                    return report.Accepted ? null : new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.AcceptRequirementUnmet, UnmetRequirement = report.Reason ?? "" };
                case QuestPart_RequiredShuttleThings p:
                    var shuttle = p.shuttle?.TryGetComp<CompShuttle>();
                    var named = shuttle != null && shuttle.requiredPawns.Count > 0;
                    var load = new Obs.QuestObjective { Kind = named ? Obs.QuestObjectiveKind.LoadNamedPawns : Obs.QuestObjectiveKind.LoadPawns };
                    if (p.requiredColonistCount >= 0) load = load with { Count = p.requiredColonistCount };
                    if (named) load = load with { PawnIds = [.. shuttle!.requiredPawns.Select(pawn => pawn.GetUniqueLoadID())] };
                    return load;
                case QuestPart_DropMonumentMarkerCopy _:
                    var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Monument };
                    var monument = NativeQuestMonuments.Read(quest); if (monument != null) row = row with { Monument = monument };
                    var timer = quest.PartsListForReading.OfType<QuestPart_Delay>().Where(d => d.isBad &&
                        (decree || d.inSignalDisable?.EndsWith(".MonumentCompleted", StringComparison.Ordinal) == true))
                        .OrderBy(d => d.delayTicks).FirstOrDefault();
                    if (timer != null)
                    {
                        if (timer.State == QuestPartState.Enabled) row = row with { DeadlineTicks = Math.Max(0, (long)Find.TickManager.TicksGame + timer.TicksLeft) };
                        else if (quest.State == QuestState.NotYetAccepted && timer.State == QuestPartState.NeverEnabled) row = row with { DurationTicks = timer.delayTicks };
                    }
                    return row;
                case QuestPart_PawnsArrive p when p.pawns.Any(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)):
                    var lodgers = p.pawns.Where(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)).ToArray();
                    var host = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HostLodgers, PawnIds = [.. lodgers.Select(pawn => pawn.GetUniqueLoadID())], Count = lodgers.Length };
                    return NativeQuestShuttles.LodgerMood(quest, host, lodgers);
                default:
                    return null;
            }
        }

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
                var row = Objective(quest, part, decree);
                if (row == null) continue;
                if (part is QuestPartActivable activable) row = row with { Active = activable.State == QuestPartState.Enabled };
                if (deadline.HasValue && row.Kind is Obs.QuestObjectiveKind.ProduceItem or Obs.QuestObjectiveKind.HarvestPlant or Obs.QuestObjectiveKind.KillAnimals) row = row with { DeadlineTicks = deadline.Value };
                var workload = NativeQuestWorkload.Read(row, quest); if (workload != null) row = row with { Workload = workload };
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
                    Id = q.GetUniqueLoadID(),
                    State = NativeEnums.Quest(q.State), AcceptedTick = q.acceptanceTick, ExpiresInTicks = q.TicksUntilExpiry,
                    RequiresAccepter = q.RequiresAccepter, ScriptDef = q.root?.defName ?? "",
                    CanAccept = q.State == QuestState.NotYetAccepted && QuestUtility.CanAcceptQuest(q).Accepted,
                    ChoicePartCount = q.PartsListForReading.OfType<QuestPart_Choice>().Count(),
                };
                var faction = q.InvolvedFactions.FirstOrDefault(f => f != null && !f.IsPlayer);
                if (faction != null) row = row with { FactionId = faction.GetUniqueLoadID() };
                var mapId = QuestMapId(q);
                if (mapId.HasValue) row = row with { MapId = mapId.Value };
                row = row with { EligiblePawns = [.. row.EligiblePawns, .. Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).Select(NativeRef.Thing)] };
                var asker = q.root?.defName.StartsWith("Decree_", StringComparison.Ordinal) == true
                    ? q.PartsListForReading.OfType<QuestPart_SituationalThought>().Where(p => p.def?.defName == "DecreeUnmet").Select(p => p.pawn).Distinct().ToArray() : Array.Empty<Pawn>();
                if (asker.Length == 1 && asker[0]?.Faction != null) row = row with { AskerFactionPlayer = asker[0].Faction.IsPlayer };
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

        internal static Obs.WorldProgressionSnapshot Build(Common.ObservationContext context)
        {
            var maps = Maps(context);
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

    internal static class NativeWorldProgressionObservationTools
    {
        // On the game thread. A refused request or a census that cannot be read completely throws.
        internal static Obs.WorldProgressionSnapshot HandleReadWorldProgression(Obs.WorldProgressionRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out _, out var context, out var invalid))
                throw NativeFaults.Refused(invalid);
            try
            {
                return NativeWorldProgressionObservation.Build(context);
            }
            catch (Exception thrown)
            {
                ModLog.Error("observe", "World progression read failed: " + thrown);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Native world progression could not be read completely: " + thrown.GetType().Name + ": " + thrown.Message });
            }
        }

        private static bool Validate(Obs.WorldProgressionRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity are required.");
            return request?.Scope?.ExpectedIdentity != null;
        }
    }
}
