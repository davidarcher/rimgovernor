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
        private static bool OrdinaryWeapon(Pawn pawn)
        {
            var weapon = pawn.equipment?.Primary;
            if (weapon?.def.IsRangedWeapon != true) return false;
            return OrdinaryVerbs(weapon.def.Verbs);
        }
        internal static bool OrdinaryVerbs(IEnumerable<VerbProperties> definitions)
        {
            var verbs = definitions.Where(v => !v.IsMeleeAttack && v.ai_IsWeapon).ToArray();
            return verbs.Length > 0 && verbs.All(v => v.defaultProjectile?.thingClass == typeof(Bullet) && v.defaultProjectile.projectile.explosionRadius == 0 && v.defaultProjectile.projectile.damageDef?.workerClass != typeof(DamageWorker_Flame));
        }
        private static bool ButcherReady(Pawn prey) => prey.Map.listerThings.AllThings.OfType<Building>().Any(b =>
            b is IBillGiver giver && !b.IsForbidden(Faction.OfPlayer) && giver.CurrentlyUsableForBills()
            && giver.BillStack.Bills.OfType<Bill_Production>().Any(bill => bill.recipe.defName == "ButcherCorpseFlesh"
                && !bill.suspended && !bill.paused && bill.ingredientFilter.Allows(prey.RaceProps.corpseDef)
                && (bill.repeatMode == BillRepeatModeDefOf.Forever
                    || bill.repeatMode == BillRepeatModeDefOf.RepeatCount && bill.repeatCount > 0
                    || bill.repeatMode == BillRepeatModeDefOf.TargetCount && BillCommon.ProductCount(bill) is int count && count < bill.targetCount))
            && prey.Map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.InMentalState
                && DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking") is WorkTypeDef cooking && p.workSettings?.WorkIsActive(cooking) == true && p.CanReach(b, PathEndMode.InteractionCell, Danger.None)));
        // Food prey is wild and edible. Revenge and predation are policy costs
        // (the census flags them; a lone hunter never designates either, a squad
        // may take them); RouteSafe still rejects hazards and non-ordinary death
        // actions.
        private static bool SafePrey(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal
            && !prey.InMentalState
            && prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true && prey.RaceProps.corpseDef != null;
        // PestDefinitions are the wild animals hunted for what they destroy,
        // not for meat (#247): an alphabeaver pack defoliates the map and
        // no census otherwise answers it (wild, factionless, not hostile,
        // not a predator). Go recognises the same names in the wild-animal
        // census (policy.PestDefinition), so the two lists move together.
        internal static readonly HashSet<string> PestDefinitions = new HashSet<string>(StringComparer.Ordinal) { "Alphabeaver" };
        // Pest is the pest rule: a wild, living, undowned animal of a pest
        // definition. It waives the docility (manhunterOnDamageChance) and
        // butcher-bill rules of SafePrey: the point is the kill, and the
        // pack turning manhunter on a hit is the emergency census's threat
        // to answer, not a reason to leave the trees to them.
        internal static bool Pest(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal && !prey.InMentalState
            && prey.RaceProps.corpseDef != null && PestDefinitions.Contains(prey.def.defName);
        // Meleeable prey (#260): safe prey no bigger than the hunter, which
        // flees rather than fights back, so a colonist with a melee weapon
        // or bare hands can run it down. This is the wiki's day-one interim
        // food; it never covers a pest or anything the safe-prey rule rejects.
        internal static bool Meleeable(Pawn prey) => SafePrey(prey) && !prey.RaceProps.predator && (prey.Downed || prey.RaceProps.manhunterOnDamageChance == 0 && prey.BodySize <= 1.0f);
        private static bool MeleeArmed(Pawn p, Pawn prey) => Meleeable(prey) && (p.equipment?.Primary == null || p.equipment.Primary.def.IsMeleeWeapon);
        // Hunter is the colonist rule (a drafted squad still counts: its own
        // draft must not withdraw the prey it is hunting): hunting enabled, an ordinary bullet
        // weapon (or a melee weapon or bare hands against meleeable prey),
        // within 100 cells over a safe route. A pest is hunted wherever it
        // is on the map (the pack arrives at the edge and works inward);
        // the route still has to be safe.
        private static bool Hunter(Pawn p, Pawn prey) => !p.Downed && !p.Drafted && !p.InMentalState
            && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true && (OrdinaryWeapon(p) || MeleeArmed(p, prey))
            && (Pest(prey) || p.Position.DistanceToSquared(prey.Position) <= 10000) && HuntingSafety.RouteSafe(p, prey);
        // Ineligible names the first hunt rule the prey (or every colonist
        // against it) fails, null when the census would offer it: what the
        // test fixtures report when a staged animal never becomes a row.
        internal static string? Ineligible(Pawn prey)
        {
            if (!prey.Spawned) return "not spawned";
            if (prey.Dead) return "dead";
            if (prey.Position.Fogged(prey.Map)) return "fogged";
            if (!Pest(prey))
            {
                if (!SafePrey(prey)) return "not safe prey";
                if (!ButcherReady(prey)) return "no butcher bill";
            }
            var colonists = prey.Map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count == 0) return "no colonist";
            var reasons = colonists.Select(p =>
                p.Downed ? "downed" : p.InMentalState ? "mental state"
                : p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) != true ? "hunting inactive"
                : !OrdinaryWeapon(p) && !MeleeArmed(p, prey) ? "no ordinary ranged weapon (" + (p.equipment?.Primary?.def.defName ?? "unarmed") + ")" + (Meleeable(prey) ? "" : " and the prey is not meleeable")
                : !(Pest(prey) || p.Position.DistanceToSquared(prey.Position) <= 10000) ? "too far"
                : !HuntingSafety.RouteSafe(p, prey) ? "no safe route" : (string?)null).ToList();
            if (reasons.Any(r => r == null)) return null;
            return "no hunter: " + string.Join("; ", colonists.Zip(reasons, (p, r) => p.GetUniqueLoadID() + " " + r));
        }
        private static bool Eligible(Pawn prey)
        {
            if (!prey.Spawned || prey.Dead || prey.Position.Fogged(prey.Map)) return false;
            if (!Pest(prey) && (!SafePrey(prey) || !ButcherReady(prey))) return false;
            return prey.Map.mapPawns.FreeColonistsSpawned.Any(p => Hunter(p, prey));
        }
        private static double Nutrition(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount)) * prey.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
        private static Obs.SnapshotRef Snapshot(Pawn prey, Common.ObservationContext context) => new Obs.SnapshotRef {
            // A hunt follows its animal (#321): the token binds the animal,
            // its corpse and its designation, not the cell or health the
            // census read, which move every tick under a running clock.
            Context = context.Clone(), EntityId = prey.GetUniqueLoadID(), Token = NativeAcquisitionToken.Token(context.Identity,
                prey.GetUniqueLoadID(), prey.RaceProps.corpseDef.defName, 0, 0, 0, 1, Designated(prey)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center)
        {
            result.PendingHunts = (uint)Pending(map);
            // Food prey within 100 cells of the colony (the hunter rule's own
            // reach; tribal8's game grazes 60-100 cells out, #260), then every pest on
            // the map: a pest row is a hunt of one unit of nothing edible
            // (food false, no nutrition), so the food and wood selections
            // pass it over and only the pest goal takes it.
            var candidates = map.mapPawns.AllPawnsSpawned.Where(p => !Pest(p) && p.Position.DistanceTo(center) <= 100 && Eligible(p))
                .OrderByDescending(p => p.BodySize / (1 + p.Position.DistanceTo(center) / 25)).ThenBy(p => p.thingIDNumber)
                .Concat(map.mapPawns.AllPawnsSpawned.Where(p => Pest(p) && Eligible(p)).OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber));
            var offered = candidates.ToList();
            LogWhyNoPrey(map, center, offered.Count);
            foreach (var prey in offered)
            {
                var designated = Designated(prey);
                var row = new Obs.AcquisitionFacts {
                Source = NativeRef.Thing(prey), SourceSnapshot = Snapshot(prey, result.Context),
                Resource = prey.RaceProps.corpseDef.defName, Tree = false, Food = !Pest(prey), Hunt = true,
                Yield = 1, NutritionYield = Pest(prey) ? 0 : Nutrition(prey), Designated = designated,
                RevengeChance = prey.RaceProps.manhunterOnDamageChance,
                HerdSize = (uint)map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && p.def == prey.def && p.Position.DistanceToSquared(prey.Position) <= 625),
                MeleeOnly = Meleeable(prey), Downed = prey.Downed,
                BodySize = prey.BodySize, Sleeping = !prey.Awake(), Predator = prey.RaceProps.predator,
                WeaponRange = map.mapPawns.FreeColonistsSpawned.Where(p => Hunter(p, prey) && OrdinaryWeapon(p))
                    .SelectMany(p => p.equipment.Primary.def.Verbs).Where(v => !v.IsMeleeAttack && v.ai_IsWeapon)
                    .Select(v => (double)v.range).DefaultIfEmpty(0).Max(), Taken = ResourceAcquisitionTools.Taken(prey) };
                var tick = ResourceAcquisitionTools.DesignatedTick(prey, designated);
                if (tick.HasValue) row.DesignatedTick = tick.Value;
                result.Acquisition.Add(row);
            }
            result.PendingFoodNutrition += map.mapPawns.AllPawnsSpawned.Where(p => Designated(p) && p.RaceProps.meatDef != null).Sum(Nutrition);
        }
        private static int lastWhyTick = int.MinValue;
        // With no hunt row offered, names once per game hour why each wild
        // animal species near the colony was left out, so "animals around but
        // no hunting" shows its rule in the game log.
        private static void LogWhyNoPrey(Map map, IntVec3 center, int offered)
        {
            if (offered > 0 || Find.TickManager.TicksGame - lastWhyTick < 2500) return;
            var wild = map.mapPawns.AllPawnsSpawned.Where(p => !Pest(p) && p.Faction == null && p.RaceProps.Animal && !p.Dead && p.Position.DistanceTo(center) <= 100).ToList();
            if (wild.Count == 0) return;
            lastWhyTick = Find.TickManager.TicksGame;
            var lines = wild.GroupBy(p => p.def.defName + ": " + (Ineligible(p) ?? "eligible")).Take(8).Select(g => g.Key + " x" + g.Count());
            Log.Message("[RimGovernor] no hunt rows with " + wild.Count + " wild animals near the colony: " + string.Join(" | ", lines));
        }
        internal const string Kind = "Hunt";
        // Prepare is the apply-time precondition list for hunt
        // (action-contracts.md): Eligible plus the request's resource and
        // designation rules, one rule at a time. The request's cell is where
        // the controller planned the animal, a hint only: the hunt follows
        // the animal by identity wherever it is on the map (#321), and the
        // hunter rule still bounds food prey to 100 cells.
        internal static bool Prepare(AcquireRequest command, Common.ObservationContext context, out Pawn? prey, out Common.Failure failure)
        {
            prey = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hunting requires an exact safe prey (or pest) snapshot, enabled hunter, butcher bill (food prey) and fewer than two outstanding hunts.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.mapPawns.AllPawnsSpawned.ById(command.SourceId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Pending(map) < 2, "two hunts are already outstanding on this map")
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned, "the exact animal is no longer spawned on this map")
                .Require(() => !found!.Dead, "the animal is dead")
                .Require(() => Pest(found!) || SafePrey(found!) && !found!.RaceProps.predator, "the animal is neither safe wild prey nor a recognised pest")
                .Require(() => found!.RaceProps.corpseDef.defName == command.ResourceDefName, "the animal's corpse is not the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the animal's cell is fogged")
                .Require(() => !Designated(found!), "the animal is already designated for hunting")
                .Require(() => new Designator_Hunt().CanDesignateThing(found!).Accepted, "the native hunt designator refuses the animal")
                .Require(() => Pest(found!) || ButcherReady(found!), "no usable butcher bill with an assigned cook accepts the corpse")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Hunter(p, found!)), "no free colonist with hunting enabled and an ordinary ranged weapon (or a melee weapon or bare hands against meleeable prey) has a safe route to the animal");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            prey = found;
            return true;
        }
    }
}
