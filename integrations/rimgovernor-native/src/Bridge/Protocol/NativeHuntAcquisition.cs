#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

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
        // may take them).
        private static bool SafePrey(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal
            && !prey.InMentalState
            && prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true && prey.RaceProps.corpseDef != null;
        // A pest is a wild animal hunted for what it destroys, not for meat
        //: a race that eats trees (RaceProperties.Eats(Tree), the
        // alphabeaver) defoliates the map and no census otherwise answers it
        // (wild, factionless, not hostile, not a predator). The brain reads the same
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
        // The brain multiplies it by the meat's nutrition off the def rows.
        private static double MeatAmount(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount));
        private static double Nutrition(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount)) * prey.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
        private static string Token(Pawn prey, Common.ObservationContext context) => NativeAcquisitionToken.Hunt(context.Identity,
            prey.GetUniqueLoadID(), prey.RaceProps.corpseDef.defName, Designated(prey));
        // A hunt row is a wild animal that bears a corpse and is a food source or a pest, whatever
        // policy then decides (policy.HuntGate): native states facts and keeps only physical validity.
        private static bool Candidate(Pawn prey) => prey.Spawned && !prey.Dead && prey.Faction == null && prey.RaceProps.Animal
            && prey.RaceProps.corpseDef != null && (PestRace(prey.RaceProps) || prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true);
        internal static void Read(NativePlantAcquisition.Facts result, Map map, IntVec3 center, Obs.ColonyFactsRequest request, Common.ObservationContext context)
        {
            result.PendingHunts = (uint)Pending(map);
            // Wild animals by distance from the colony anchor, food prey then every pest: a pest row is
            // a hunt of one unit of nothing edible (food false, no nutrition), so the food and wood
            // selections pass it over and only the pest concern takes it. The brain orders its own selection.
            var prey = map.mapPawns.AllPawnsSpawned.Where(p => !PestRace(p.RaceProps) && Candidate(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber)
                .Concat(map.mapPawns.AllPawnsSpawned.Where(p => PestRace(p.RaceProps) && Candidate(p)).OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber))
                .ToList();
            // The herd size counts same-race animals within the request's herd radius, the prey included.
            var herdSquared = (long)request.HerdRadius * request.HerdRadius;
            foreach (var animal in prey)
            {
                var designated = Designated(animal);
                var herd = (uint)map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && p.def == animal.def && p.Position.DistanceToSquared(animal.Position) <= herdSquared);
                result.Rows.Add(new Obs.AcquisitionFacts {
                    Source = NativeRef.Thing(animal), Token = Token(animal, context),
                    Resource = animal.RaceProps.corpseDef.defName, Yield = 1,
                    Designation = ResourceAcquisitionTools.Designation(animal, designated),
                    Taken = ResourceAcquisitionTools.Taken(animal),
                    Kind = new Obs.AcquisitionKind.Hunt(MeatAmount(animal), herd, animal.Downed, !animal.Awake(), animal.Position.Fogged(map), animal.InMentalState) });
            }
            result.HuntCensus = Census(map);
            result.PendingFoodNutrition += map.mapPawns.AllPawnsSpawned.Where(p => Designated(p) && p.RaceProps.meatDef != null).Sum(Nutrition);
        }
        // Raw facts per free colonist; policy decides who can hunt what.
        private static Obs.HuntCensus Census(Map map)
        {
            var hunters = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Select(pawn => new Obs.HunterFacts {
                Pawn = new RimGovernor.Brain.Domain.PawnId(pawn.GetUniqueLoadID()),
                Position = new RimGovernor.Brain.Domain.Cell(pawn.Position.x, pawn.Position.z),
                Downed = pawn.Downed, InMentalState = pawn.InMentalState,
                HuntingActive = pawn.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true,
                WeaponDef = pawn.equipment?.Primary?.def.defName,
                RangedBlockingShield = WorkGiver_HunterHunt.HasShieldAndRangedWeapon(pawn) });
            return new Obs.HuntCensus { Hunters = RimGovernor.Brain.Domain.EquatableArray.From(hunters) };
        }
        internal const string Kind = "Hunt";
        // Prepare is the apply-time precondition list for hunt (action-contracts.md): physical
        // validity of the exact animal (spawned, alive, not fogged, not designated, the native
        // designator accepts it) plus the request's resource, one rule at a time. Whether a hunt is
        // wanted (butcher bill, hunter, weapon, pending cap) is brain policy (policy.HuntGate). The
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
