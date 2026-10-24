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
using PawnId = RimGovernor.Brain.Domain.PawnId;
using QuestId = RimGovernor.Brain.Domain.QuestId;

namespace HomeBridge.BridgeTools
{
    // Answers rimgovernor/observations_read_world_progression behind the typed
    // ReadScope boundary (observations.proto's WorldProgressionRequest/Reply,
    // WorldMap, CaravanState, CaravanAssembly and QuestState).
    // Stateless: every read recomputes from current native state, per
    // native-static-state.md. Read-only; no orders, no UI, no camera.
    internal static class NativeWorldProgressionObservation
    {


        private static List<Obs.WorldMap> Maps(Common.ObservationContext context)
        {
            var rows = new List<Obs.WorldMap>();
            foreach (var m in Find.Maps)
            {
                rows.Add(new Obs.WorldMap
                {
                    Id = m.uniqueID, Tile = m.Tile.tileId, Home = m.IsPlayerHome,
                    // A copy: the game refills FreeColonistsSpawned in place, and a row's reads ask for it again.
                    Pawns = [.. m.mapPawns.FreeColonistsSpawned.ToList().Select(p => NativePawnObservationTools.Row(p, new NativePawnObservationTools.MapFacts(m), context))],
                    QuestWorkers = [.. NativeQuestWorkers.Read(m)],
                });
            }
            return rows;
        }

        // The quest's first non-player faction and its relation to the player.
        private static Obs.QuestFaction QuestFaction(Quest quest) =>
            quest.InvolvedFactions.FirstOrDefault(f => f != null && !f.IsPlayer) is { } faction
                ? new Obs.QuestFaction.Involved(faction.HostileTo(Faction.OfPlayer))
                : new Obs.QuestFaction.NoFaction();

        // Per-caravan home routes: one route per player-home map, so the brain can tell whether a caravan could path
        // home without depending on the caravan formation catalog (which only ever evaluates one destination at a time).
        private static List<Obs.WorldRoute> HomeRoutes(Caravan caravan) =>
            [.. Find.Maps.Where(m => m.IsPlayerHome).Select(m => new Obs.WorldRoute
            {
                Destination = m.Tile.tileId,
                Route = caravan.CanReach(m.Tile)
                    ? new Obs.PackRoute.Reachable(CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, m.Tile, caravan))
                    : new Obs.PackRoute.Unreachable(),
            })];

        private static List<Obs.Quantity> Inventory(Caravan caravan) =>
            [.. CaravanInventoryUtility.AllInventoryItems(caravan).GroupBy(t => t.def)
                .Select(group => new Obs.Quantity { DefName = group.Key.defName, Units = group.Sum(t => (long)t.stackCount) })];

        // A figure the game cannot give as a finite number is a read failure, never an omitted field.
        private static double Finite(float value, string what, Caravan caravan) =>
            float.IsNaN(value) || float.IsInfinity(value) ? throw new InvalidOperationException("Caravan " + caravan.GetUniqueLoadID() + " " + what + " is not a finite number.") : value;

        private static List<Obs.CaravanState> Caravans(Common.ObservationContext context)
        {
            var rows = new List<Obs.CaravanState>();
            foreach (var c in Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled))
            {
                var food = c.DaysWorthOfFood;
                rows.Add(new Obs.CaravanState
                {
                    // A caravan is a world object: it is on no map.
                    Caravan = new Obs.EntityRef { Id = c.GetUniqueLoadID(), DefName = c.def.defName, Label = c.Label ?? throw new InvalidOperationException("Caravan label unavailable."), Place = new Obs.EntityPlace.Unplaced() },
                    Tile = c.Tile.tileId,
                    Course = c.pather.Moving ? new Obs.CaravanCourse.Moving(c.pather.Destination.tileId) : new Obs.CaravanCourse.Stopped(),
                    MassUsage = Finite(c.MassUsage, "mass usage", c),
                    MassCapacity = Finite(c.MassCapacity, "mass capacity", c),
                    FoodDays = Finite(food.days, "food days", c),
                    FoodRotDays = Finite(food.tillRot, "food rot days", c),
                    Pawns = [.. c.PawnsListForReading.Select(p => NativePawnObservationTools.Row(p, new NativePawnObservationTools.MapFacts(null), context))],
                    HomeRoutes = [.. HomeRoutes(c)],
                    Inventory = [.. Inventory(c)],
                });
            }
            return rows;
        }

        private static List<Obs.CaravanAssembly> Assemblies() =>
            [.. Find.Maps.SelectMany(m => m.lordManager.lords.Where(l => l.LordJob is LordJob_FormAndSendCaravan)
                .Select(l => new Obs.CaravanAssembly
                {
                    MapId = m.uniqueID,
                    Pawns = [.. l.ownedPawns.Select(p => new RimGovernor.Brain.Domain.PawnId(p.GetUniqueLoadID()))],
                }))];

        // One row per option of the quest's reward choice, in the game's order; an option's rows total every reward it
        // grants. A quest with several choice parts lists them index by index (the brain refuses such a quest).
        private static List<Obs.QuestReward> Rewards(Quest quest)
        {
            var rows = new List<Obs.QuestReward>();
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_Choice>())
            {
                for (var index = 0; index < part.choices.Count; index++)
                {
                    var option = Reward(part.choices[index]);
                    if (index < rows.Count)
                    {
                        var row = rows[index];
                        option = new Obs.QuestReward([.. row.Items, .. option.Items], row.Favor + option.Favor, row.Goodwill + option.Goodwill,
                            row.Psylink + option.Psylink, row.PermitPoints + option.PermitPoints, [.. row.Permits, .. option.Permits]);
                        rows[index] = option;
                    }
                    else rows.Add(option);
                }
            }
            return rows;
        }

        private static Obs.QuestReward Reward(QuestPart_Choice.Choice choice)
        {
            var items = new List<Obs.QuestRewardItem>();
            var permits = new List<string>();
            int favor = 0, goodwill = 0, psylink = 0, permitPoints = 0;
            foreach (var reward in choice.rewards)
            {
                switch (reward)
                {
                    case Reward_RoyalFavor royal:
                        favor += royal.amount;
                        break;
                    case Reward_Goodwill good:
                        goodwill += good.amount;
                        break;
                    case Reward_BestowingCeremony ceremony:
                        if (ceremony.givePsylink) psylink++;
                        if (ceremony.royalTitle != null)
                        {
                            permitPoints += ceremony.royalTitle.permitPointsAwarded;
                            if (ceremony.royalTitle.permits != null) permits.AddRange(ceremony.royalTitle.permits.Select(p => p.defName));
                        }
                        break;
                    case Reward_Items stacks:
                        items.AddRange(stacks.items.Select(t => new Obs.QuestRewardItem(t.def.defName, t.stackCount)));
                        psylink += stacks.items.Where(t => t.def == ThingDefOf.PsychicAmplifier).Sum(t => t.stackCount);
                        break;
                }
            }
            return new Obs.QuestReward([.. items], favor, goodwill, psylink, permitPoints, [.. permits]);
        }

        // The royal favor the quest grants whatever option is taken: its favor parts outside every choice.
        private static int QuestFavor(Quest quest)
        {
            var choiceParts = new HashSet<QuestPart>(quest.PartsListForReading.OfType<QuestPart_Choice>()
                .SelectMany(p => p.choices).SelectMany(c => c.questParts));
            return quest.PartsListForReading.OfType<QuestPart_GiveRoyalFavor>().Where(p => !choiceParts.Contains(p) && p.amount > 0).Sum(p => p.amount);
        }

        private static readonly AccessTools.FieldRef<QuestPart_ThingsProduced, int> Produced = AccessTools.FieldRefAccess<QuestPart_ThingsProduced, int>("produced");
        private static readonly AccessTools.FieldRef<QuestPart_PlantsHarvested, int> Harvested = AccessTools.FieldRefAccess<QuestPart_PlantsHarvested, int>("harvested");
        private static readonly AccessTools.FieldRef<QuestPart_PawnsKilled, int> Killed = AccessTools.FieldRefAccess<QuestPart_PawnsKilled, int>("killed");

        private static readonly Obs.QuestObjectiveGoal Uncounted = new Obs.QuestObjectiveGoal.Uncounted();
        private static readonly Obs.QuestObjectiveTiming Untimed = new Obs.QuestObjectiveTiming.Untimed();

        // A hosted-lodgers objective: the lodgers, their mood floor and the moods the game tracks.
        private static Obs.QuestObjective HostLodgers(Quest quest, Pawn[] lodgers)
        {
            var (floor, moods) = NativeQuestShuttles.LodgerMood(quest, lodgers);
            return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HostLodgers, Goal = new Obs.QuestObjectiveGoal.Pawns(lodgers.Length), Timing = Untimed,
                PawnIds = [.. lodgers.Select(pawn => new PawnId(pawn.GetUniqueLoadID()))], Active = true, MinimumMood = floor, LodgerMoods = moods };
        }

        // An objective that counts things of one def; a decree's counted objectives are due at its bad delay.
        private static Obs.QuestObjective Counted(Obs.QuestObjectiveKind kind, string def, string? stuff, long count, long done, long? decreeDeadline, bool active) =>
            new Obs.QuestObjective { Kind = kind, Goal = new Obs.QuestObjectiveGoal.Things(def, stuff, count, done),
                Timing = decreeDeadline.HasValue ? new Obs.QuestObjectiveTiming.Deadline(decreeDeadline.Value) : Untimed,
                PawnIds = [], Active = active, MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };

        // The objective one quest part states; null for a part that states none the brain reads. A part without an
        // enable state is in force while the quest lists it.
        private static Obs.QuestObjective? Objective(Quest quest, QuestPart part, bool decree, long? decreeDeadline)
        {
            var active = !(part is QuestPartActivable activable) || activable.State == QuestPartState.Enabled;
            switch (part)
            {
                case QuestPart_ThingsProduced p:
                    return Counted(Obs.QuestObjectiveKind.ProduceItem, p.def?.defName ?? throw new InvalidOperationException("A produce objective names no thing."),
                        p.stuff?.defName, p.count, Produced(p), decreeDeadline, active);
                case QuestPart_PlantsHarvested p:
                    return Counted(Obs.QuestObjectiveKind.HarvestPlant, p.plant?.defName ?? throw new InvalidOperationException("A harvest objective names no plant."),
                        null, p.count, Harvested(p), decreeDeadline, active);
                case QuestPart_PawnsKilled p when p.race?.race?.Animal == true:
                    return Counted(Obs.QuestObjectiveKind.KillAnimals, p.race.defName, null, p.count, Killed(p), decreeDeadline, active);
                case QuestPart_RequirementsToAccept p:
                    return p.CanAccept().Accepted ? null : new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.AcceptRequirementUnmet, Goal = Uncounted, Timing = Untimed,
                        PawnIds = [], Active = active, MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                case QuestPart_RequiredShuttleThings p:
                    // requiredColonistCount is -1 when the shuttle asks for no colonist count.
                    var shuttle = p.shuttle?.TryGetComp<CompShuttle>();
                    var named = shuttle != null && shuttle.requiredPawns.Count > 0;
                    return new Obs.QuestObjective { Kind = named ? Obs.QuestObjectiveKind.LoadNamedPawns : Obs.QuestObjectiveKind.LoadPawns,
                        Goal = p.requiredColonistCount >= 0 ? new Obs.QuestObjectiveGoal.Pawns(p.requiredColonistCount) : Uncounted, Timing = Untimed,
                        PawnIds = named ? [.. shuttle!.requiredPawns.Select(pawn => new PawnId(pawn.GetUniqueLoadID()))] : [],
                        Active = active, MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                case QuestPart_DropMonumentMarkerCopy _:
                    var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Monument, Goal = Uncounted, Timing = Untimed,
                        PawnIds = [], Active = active, MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                    var monument = NativeQuestMonuments.Read(quest); if (monument != null) row = row with { Monument = monument };
                    var timer = quest.PartsListForReading.OfType<QuestPart_Delay>().Where(d => d.isBad &&
                        (decree || d.inSignalDisable?.EndsWith(".MonumentCompleted", StringComparison.Ordinal) == true))
                        .OrderBy(d => d.delayTicks).FirstOrDefault();
                    if (timer != null)
                    {
                        if (timer.State == QuestPartState.Enabled) row = row with { Timing = new Obs.QuestObjectiveTiming.Deadline(Math.Max(0, (long)Find.TickManager.TicksGame + timer.TicksLeft)) };
                        else if (quest.State == QuestState.NotYetAccepted && timer.State == QuestPartState.NeverEnabled) row = row with { Timing = new Obs.QuestObjectiveTiming.Duration(timer.delayTicks) };
                    }
                    return row;
                case QuestPart_PawnsArrive p when p.pawns.Any(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)):
                    return HostLodgers(quest, p.pawns.Where(pawn => pawn.HasExtraHomeFaction(quest) || pawn.HasExtraMiniFaction(quest)).ToArray());
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
            var decree = quest.root.defName.StartsWith("Decree_", StringComparison.Ordinal);
            var deadline = decree ? quest.PartsListForReading.OfType<QuestPart_Delay>().Where(p => p.isBad && p.State == QuestPartState.Enabled)
                .Select(p => (long?)Math.Max(0, (long)Find.TickManager.TicksGame + p.TicksLeft)).Min() : null;
            foreach (var part in quest.PartsListForReading)
            {
                var row = Objective(quest, part, decree, deadline);
                if (row == null) continue;
                var workload = NativeQuestWorkload.Read(row, quest); if (workload != null) row = row with { Workload = workload };
                rows.Add(row);
            }
            var hosted = quest.PartsListForReading.OfType<QuestPart_ExtraFaction>().SelectMany(p => p.affectedPawns)
                .Where(p => p.HasExtraHomeFaction(quest) || p.HasExtraMiniFaction(quest))
                .Concat(quest.State == QuestState.NotYetAccepted
                    ? quest.PartsListForReading.OfType<QuestPart_RequirementsToAcceptBedroom>().SelectMany(p => p.targetPawns)
                    : Enumerable.Empty<Pawn>()).Distinct().ToArray();
            if (hosted.Length > 0 && !rows.Any(r => r.Kind == Obs.QuestObjectiveKind.HostLodgers))
                rows.Add(HostLodgers(quest, hosted));
            if (quest.TicksUntilExpiry >= 0 && quest.State == QuestState.NotYetAccepted)
                rows.Add(new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.Expiry, Goal = Uncounted,
                    Timing = new Obs.QuestObjectiveTiming.Deadline((long)Find.TickManager.TicksGame + quest.TicksUntilExpiry),
                    PawnIds = [], Active = true, MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] });
            rows.AddRange(NativeQuestRefugees.Read(quest));
            rows.AddRange(NativeQuestHackGift.Read(quest));
            return rows;
        }

        // First map any look target of the quest sits on (a map parent's own map counts); off map for a quest anchored
        // only to world objects.
        private static Obs.QuestPlace QuestPlace(Quest quest)
        {
            foreach (var target in quest.QuestLookTargets)
            {
                var map = target.Map ?? (target.HasWorldObject ? (target.WorldObject as MapParent)?.Map : null);
                if (map != null) return new Obs.QuestPlace.OnMap(map.uniqueID);
            }
            return new Obs.QuestPlace.OffMap();
        }

        // Whose decree the quest is: the faction of the one pawn its DecreeUnmet thought sits on.
        private static Obs.DecreeAsker Asker(Quest quest)
        {
            if (!quest.root.defName.StartsWith("Decree_", StringComparison.Ordinal)) return Obs.DecreeAsker.NotDecree;
            var asker = quest.PartsListForReading.OfType<QuestPart_SituationalThought>().Where(p => p.def?.defName == "DecreeUnmet").Select(p => p.pawn).Distinct().ToArray();
            if (asker.Length != 1 || asker[0]?.Faction == null) return Obs.DecreeAsker.NoSingleAsker;
            return asker[0].Faction.IsPlayer ? Obs.DecreeAsker.PlayerFaction : Obs.DecreeAsker.OtherFaction;
        }

        private static List<Obs.QuestState> Quests()
        {
            var rows = new List<Obs.QuestState>();
            foreach (var q in Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI))
            {
                if (q.root == null) throw new InvalidOperationException("Quest " + q.GetUniqueLoadID() + " has no root script.");
                if (!NativeQuestThreats.TryRead(q, out var threatPoints, out var threatFound))
                    throw new InvalidOperationException("Quest " + q.GetUniqueLoadID() + " has a threat budget or combat power the game does not hold as a finite, non-negative number.");
                rows.Add(new Obs.QuestState
                {
                    Id = new QuestId(q.GetUniqueLoadID()),
                    State = NativeEnums.Quest(q.State),
                    Acceptance = q.acceptanceTick >= 0 ? new Obs.QuestAcceptance.Accepted(q.acceptanceTick) : new Obs.QuestAcceptance.NotAccepted(),
                    Expiry = q.TicksUntilExpiry >= 0 ? new Obs.QuestExpiry.ExpiresIn(q.TicksUntilExpiry) : new Obs.QuestExpiry.NoExpiry(),
                    RequiresAccepter = q.RequiresAccepter,
                    CanAccept = q.State == QuestState.NotYetAccepted && QuestUtility.CanAcceptQuest(q).Accepted,
                    EligiblePawns = [.. Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                        .Where(p => QuestUtility.CanPawnAcceptQuest(p, q)).Select(p => new PawnId(p.GetUniqueLoadID()))],
                    Rewards = [.. Rewards(q)],
                    Favor = QuestFavor(q),
                    ScriptDef = q.root.defName,
                    Faction = QuestFaction(q),
                    Place = QuestPlace(q),
                    Objectives = [.. Objectives(q)],
                    ChoicePartCount = q.PartsListForReading.OfType<QuestPart_Choice>().Count(),
                    Asker = Asker(q),
                    ViolentQuestsAllowed = Find.Storyteller.difficulty.allowViolentQuests,
                    Shuttles = [.. NativeQuestShuttles.Read(q)],
                    Threat = threatFound ? new Obs.QuestThreat.Points(threatPoints) : new Obs.QuestThreat.NoThreat(),
                    DeparturePawnIds = [.. q.PartsListForReading.OfType<QuestPart_LendColonistsToFaction>()
                        .SelectMany(p => p.LentColonistsListForReading).OfType<Pawn>().Select(p => p.GetUniqueLoadID()).Distinct().Select(id => new PawnId(id))],
                });
            }
            return rows;
        }

        internal static Obs.WorldProgressionSnapshot Build(Common.ObservationContext context) => new Obs.WorldProgressionSnapshot
        {
            Context = context,
            Maps = [.. Maps(context)],
            Caravans = [.. Caravans(context)],
            Assemblies = [.. Assemblies()],
            Quests = [.. Quests()],
            Sites = [.. NativeWorldSites.Read(context)],
        };
    }

    internal static class NativeWorldProgressionObservationTools
    {
        // On the game thread. A refused request or a census that cannot be read completely throws.
        internal static Obs.WorldProgressionSnapshot HandleReadWorldProgression(Obs.WorldProgressionRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out _, out var context, out var invalid))
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
            return request.Scope.ExpectedIdentity != null;
        }
    }
}
