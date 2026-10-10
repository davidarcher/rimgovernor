#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // A corpse is acquired material. Only later ordinary butchering/cooking can
    // produce edible stock; expected meat nutrition never populates this receipt.
    internal static class NativeHuntAcquisition
    {
        internal static bool Designated(Pawn prey) => prey.Spawned && prey.Map.designationManager.DesignationOn(prey, DesignationDefOf.Hunt) != null;
        // Withdraw removes the hunt designation and stops a hunter already
        // on the prey: a designation removed alone leaves a running Hunt job
        // alive, and its unsafe route can stop every later clock window.
        internal static void Withdraw(Pawn prey)
        {
            var map = prey.Map;
            var designation = map.designationManager.DesignationOn(prey, DesignationDefOf.Hunt);
            if (designation != null) map.designationManager.RemoveDesignation(designation);
            foreach (var hunter in map.mapPawns.FreeColonistsSpawned.ToList())
                if (hunter.CurJobDef == JobDefOf.Hunt && hunter.CurJob.targetA.Thing == prey)
                    hunter.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }
        private static int Pending(Map map) => map.mapPawns.AllPawnsSpawned.Count(Designated);
        // Food prey is wild and edible. Revenge and predation are policy costs
        // (the census flags them; a lone hunter never designates either, a squad
        // may take them); RouteSafe still rejects hazards and non-ordinary death
        // actions.
        private static bool SafePrey(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal
            && !prey.InMentalState
            && prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true && prey.RaceProps.corpseDef != null;
        // A pest is a wild animal hunted for what it destroys, not for meat
        //: a race that eats trees (RaceProperties.Eats(Tree), the
        // alphabeaver) defoliates the map and no census otherwise answers it
        // (wild, factionless, not hostile, not a predator). Go reads the same
        // rule off the race rows (AnimalRace.Pest).
        internal static bool PestRace(RaceProperties race) => race.Animal && race.Eats(FoodTypeFlags.Tree);
        // Pest is the pest rule: a wild, living, undowned animal of a pest
        // race. It waives the docility (manhunterOnDamageChance) and
        // butcher-bill rules of SafePrey: the point is the kill, and the
        // pack turning manhunter on a hit is the emergency census's threat
        // to answer, not a reason to leave the trees to them.
        internal static bool Pest(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal && !prey.InMentalState
            && prey.RaceProps.corpseDef != null && PestRace(prey.RaceProps);
        // MeatAmount is the live animal's stat (wounds, missing parts and malnutrition scale it);
        // Go multiplies it by the meat's nutrition off the def rows.
        private static double MeatAmount(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount));
        private static double Nutrition(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount)) * prey.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
        private static Obs.SnapshotRef Snapshot(Pawn prey, Common.ObservationContext context) => new Obs.SnapshotRef {
            // A hunt follows its animal: the token binds the animal,
            // its corpse and its designation, not the cell or health the
            // census read, which move every tick under a running clock.
            Context = context.Clone(), EntityId = prey.GetUniqueLoadID(), Token = NativeAcquisitionToken.Token(context.Identity,
                prey.GetUniqueLoadID(), prey.RaceProps.corpseDef.defName, 0, 0, 0, 1, Designated(prey)) };
        // A hunt row is a wild animal that bears a corpse and is a food source or a pest, whatever
        // policy then decides (policy.HuntGate): native states facts and keeps only physical validity.
        private static bool Candidate(Pawn prey) => prey.Spawned && !prey.Dead && prey.Faction == null && prey.RaceProps.Animal
            && prey.RaceProps.corpseDef != null && (PestRace(prey.RaceProps) || prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true);
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, Obs.ColonyFactsRequest request)
        {
            result.PendingHunts = (uint)Pending(map);
            // Wild animals by distance from the colony anchor, food prey then every pest: a pest row is
            // a hunt of one unit of nothing edible (food false, no nutrition), so the food and wood
            // selections pass it over and only the pest concern takes it. Go orders its own selection.
            var prey = map.mapPawns.AllPawnsSpawned.Where(p => !PestRace(p.RaceProps) && Candidate(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber)
                .Concat(map.mapPawns.AllPawnsSpawned.Where(p => PestRace(p.RaceProps) && Candidate(p)).OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber))
                .ToList();
            // herd_size counts same-race animals within the request's herd_radius; without a radius it is
            // left unset (unknown), never 0.
            var herdSquared = request.HasHerdRadius ? (long)request.HerdRadius * request.HerdRadius : -1;
            foreach (var animal in prey)
            {
                var designated = Designated(animal);
                var row = new Obs.AcquisitionFacts {
                Source = NativeRef.Thing(animal), SourceSnapshot = Snapshot(animal, result.Context),
                Resource = animal.RaceProps.corpseDef.defName, Hunt = true,
                Yield = 1, MeatAmount = MeatAmount(animal), Designated = designated,
                Downed = animal.Downed, Sleeping = !animal.Awake(),
                Taken = ResourceAcquisitionTools.Taken(animal),
                Fogged = animal.Position.Fogged(map), InMentalState = animal.InMentalState };
                if (herdSquared >= 0)
                    row.HerdSize = (uint)map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && p.def == animal.def && p.Position.DistanceToSquared(animal.Position) <= herdSquared);
                var tick = ResourceAcquisitionTools.DesignatedTick(animal, designated);
                if (tick.HasValue) row.DesignatedTick = tick.Value;
                result.Acquisition.Add(row);
            }
            result.HuntCensus = Census(map, prey, request.HasHuntRouteBudgetMs ? request.HuntRouteBudgetMs : (uint?)null,
                request.HasHuntPredatorMarginCells ? request.HuntPredatorMarginCells : (float?)null);
            result.PendingFoodNutrition += map.mapPawns.AllPawnsSpawned.Where(p => Designated(p) && p.RaceProps.meatDef != null).Sum(Nutrition);
        }
        // Raw facts per free colonist and per butcher bench. Route evidence is skipped for a colonist
        // who is downed, in a mental state or has Hunting off: policy refuses each of those before it
        // reads a route, so the skip loses nothing.
        private static Obs.HuntCensus Census(Map map, List<Pawn> prey, uint? routeBudgetMs, float? predatorMarginCells)
        {
            var census = new Obs.HuntCensus();
            var pairs = new List<(Pawn pawn, Obs.HunterFacts hunter, Pawn animal)>();
            var corpses = prey.Select(p => p.RaceProps.corpseDef).Distinct().ToList();
            var benches = map.listerThings.AllThings.OfType<Building>()
                .Where(b => b is IBillGiver giver && giver.BillStack.Bills.OfType<Bill_Production>().Any(bill => NativeRecipeRoles.ButcherFlesh(bill.recipe)))
                .OrderBy(b => b.thingIDNumber).ToList();
            foreach (var bench in benches)
            {
                var row = new Obs.HuntButcherBench { BenchId = bench.GetUniqueLoadID(), Usable = !bench.IsForbidden(Faction.OfPlayer) && ((IBillGiver)bench).CurrentlyUsableForBills() };
                foreach (var bill in ((IBillGiver)bench).BillStack.Bills.OfType<Bill_Production>().Where(b => NativeRecipeRoles.ButcherFlesh(b.recipe)))
                {
                    var facts = new Obs.HuntButcherBill { Suspended = bill.suspended, Paused = bill.paused, RepeatMode = NativeEnums.Repeat(bill.repeatMode),
                        RepeatCount = bill.repeatCount, TargetCount = bill.targetCount };
                    if (bill.repeatMode == BillRepeatModeDefOf.TargetCount && BillCommon.ProductCount(bill) is int count) facts.ProductCount = count;
                    facts.AllowedCorpses.Add(corpses.Where(bill.ingredientFilter.Allows).Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal));
                    row.Bills.Add(facts);
                }
                census.Benches.Add(row);
            }
            var cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber))
            {
                var settings = pawn.workSettings;
                var hunter = new Obs.HunterFacts { PawnId = pawn.GetUniqueLoadID(), Position = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z },
                    Downed = pawn.Downed, InMentalState = pawn.InMentalState, Drafted = pawn.Drafted,
                    HuntingPriority = settings?.GetPriority(WorkTypeDefOf.Hunting) ?? 0, HuntingActive = settings?.WorkIsActive(WorkTypeDefOf.Hunting) == true,
                    HuntingDisabled = pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hunting),
                    CookingActive = cooking != null && settings?.WorkIsActive(cooking) == true,
                    HasHuntingWeapon = WorkGiver_HunterHunt.HasHuntingWeapon(pawn), RangedBlockingShield = WorkGiver_HunterHunt.HasShieldAndRangedWeapon(pawn) };
                if (pawn.equipment?.Primary != null) hunter.WeaponDef = pawn.equipment.Primary.def.defName;
                if (hunter.CookingActive && !pawn.Downed && !pawn.InMentalState)
                    foreach (var bench in benches.Where(b => pawn.CanReach(b, PathEndMode.InteractionCell, Danger.None))) hunter.ReachableBenches.Add(bench.GetUniqueLoadID());
                if (hunter.HuntingActive && !pawn.Downed && !pawn.InMentalState)
                    foreach (var animal in prey.Where(a => !a.Position.Fogged(map)))
                        pairs.Add((pawn, hunter, animal));
                census.Hunters.Add(hunter);
            }
            // Every eligible pair gets a verdict, pests first then nearest first, until Go's budget runs
            // out; the rest are marked skipped (never silently absent).
            var budgetTicks = routeBudgetMs.HasValue ? routeBudgetMs.Value * System.Diagnostics.Stopwatch.Frequency / 1000 : long.MaxValue;
            long spent = 0;
            foreach (var (pawn, hunter, animal) in pairs.OrderBy(p => PestRace(p.animal.RaceProps) ? 0 : 1).ThenBy(p => p.pawn.Position.DistanceToSquared(p.animal.Position)))
            {
                var route = new Obs.HuntRoute { PreyId = animal.GetUniqueLoadID() };
                // No margin from Go is an unevaluated pair, never a guessed margin.
                if (spent >= budgetTicks || !predatorMarginCells.HasValue) route.Skipped = true;
                else
                {
                    var began = System.Diagnostics.Stopwatch.GetTimestamp();
                    route.Safe = HuntingSafety.RouteSafe(pawn, animal, predatorMarginCells.Value);
                    spent += System.Diagnostics.Stopwatch.GetTimestamp() - began;
                }
                hunter.Routes.Add(route);
            }
            ObservationWork.Detail("cf.hunt.route", spent, pairs.Count);
            return census;
        }
        internal const string Kind = "Hunt";
        // Prepare is the apply-time precondition list for hunt (action-contracts.md): physical
        // validity of the exact animal (spawned, alive, not fogged, not designated, the native
        // designator accepts it) plus the request's resource, one rule at a time. Whether a hunt is
        // wanted (butcher bill, hunter, weapon, pending cap) is Go policy (policy.HuntGate). The
        // request's cell is a hint only: the hunt follows the animal by identity.
        internal static bool Prepare(AcquireRequest command, Common.ObservationContext context, out Pawn? prey, out Common.Failure failure)
        {
            prey = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hunting requires an exact safe prey (or pest) snapshot.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.mapPawns.AllPawnsSpawned.ById(command.SourceId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned, "the exact animal is no longer spawned on this map")
                .Require(() => !found!.Dead, "the animal is dead")
                .Require(() => Pest(found!) || SafePrey(found!) && !found!.RaceProps.predator, "the animal is neither safe wild prey nor a recognised pest")
                .Require(() => found!.RaceProps.corpseDef.defName == command.ResourceDefName, "the animal's corpse is not the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the animal's cell is fogged")
                .Require(() => !Designated(found!), "the animal is already designated for hunting")
                .Require(() => new Designator_Hunt().CanDesignateThing(found!).Accepted, "the native hunt designator refuses the animal");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            prey = found;
            return true;
        }
    }
}
