using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    internal static class NativePlantAcquisition
    {
        private static bool Eligible(Plant plant) => GameThread.IsLoaded(plant.Map) && ResourceAcquisitionTools.Eligible(plant, plant.Map)
            && plant.Map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, plant));
        // Cutter has no distance reach: the measured cost (#2567) is the
        // all-plants eligibility scan, which the reach never bounded.
        internal static string Token(Plant plant, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAcquisitionToken.Plant(context.Identity, plant.GetUniqueLoadID(),
            plant.def.plant.harvestedThingDef.defName, plant.Position.x, plant.Position.z, plant.HarvestableNow, ResourceAcquisitionTools.Designated(plant));
        // The acquisition rows and pending values of one colony read; a read it cannot make throws.
        internal sealed class Facts
        {
            internal List<RimGovernor.Brain.GameFacts.Observations.AcquisitionFacts> Rows = new List<RimGovernor.Brain.GameFacts.Observations.AcquisitionFacts>();
            internal double PendingFoodNutrition;
            internal uint PendingHunts;
            internal RimGovernor.Brain.GameFacts.Observations.HuntCensus HuntCensus = new RimGovernor.Brain.GameFacts.Observations.HuntCensus { Hunters = [] };
        }
        internal static Facts Read(Map map, IntVec3 center, Func<ThingDef, bool> humanFood, RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var result = new Facts();
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant.harvestedThingDef != null).ToArray();
            // The candidate pool is every eligible plant on the map; pending yield below covers all designations.
            // Medicine-yielding wild plants (healroot) join trees and food so medical/reserves can harvest.
            // YieldNow rounds randomly, so one sample per plant decides both eligibility and the row: a plant
            // whose sample is zero (below harvest growth, or a fractional yield rounded down) is not a source.
            var selected = plants.Where(p => (p.def.plant.IsTree || humanFood(p.def.plant.harvestedThingDef) || p.def.plant.harvestedThingDef.IsMedicine) && Eligible(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber)
                .Select(p => (plant: p, yield: p.YieldNow())).Where(s => s.yield > 0).ToArray();
            foreach (var (plant, yield) in selected)
            {
                var resource = plant.def.plant.harvestedThingDef;
                result.Rows.Add(new RimGovernor.Brain.GameFacts.Observations.AcquisitionFacts {
                    Source = NativeRef.Thing(plant), Token = Token(plant, context),
                    Resource = resource.defName, Yield = yield,
                    Designation = ResourceAcquisitionTools.Designation(plant, ResourceAcquisitionTools.Designated(plant)),
                    Taken = ResourceAcquisitionTools.Taken(plant),
                    Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(humanFood(resource), plant.Growth, ResourceAcquisitionTools.Plantation(plant)) });
            }
            var pending = plants.Where(ResourceAcquisitionTools.Designated).ToArray();
            result.PendingFoodNutrition = pending.Where(p => humanFood(p.def.plant.harvestedThingDef)).Sum(p => (double)p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition));
            NativeHuntAcquisition.Read(result, map, center, request, context);
            return result;
        }
        internal const string Kind = "Plant acquisition";
        // Cutter is the colonist rule shared by the census (Eligible) and the
        // apply-time check: someone must be able to do the work now.
        internal static bool Cutter(Pawn p, Plant plant) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(plant, PathEndMode.Touch, Danger.None);
        // Prepare is the apply-time precondition list for cut/harvest
        // (action-contracts.md): the conjunction is Eligible plus the
        // request's own cell, resource and designation rules, evaluated one
        // rule at a time so a refusal names the fact that moved.
        internal static bool Prepare(AcquireRequest command, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Plant? plant, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            plant = null; failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Acquisition requires an exact safe mature wild plant snapshot.");
            var map = GameThread.LoadedMap(context);
            var found = RefIndex.Thing<Plant>(map, command.SourceId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned && GameThread.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found != null && (found.Position.x == command.Cell.X && found.Position.z == command.Cell.Z), "the plant is not at the expected cell")
                .Require(() => found != null && (found.def.plant.harvestedThingDef?.defName == command.ResourceDefName), "the plant no longer yields the expected resource")
                .Require(() => found != null && (!found.Position.Fogged(map)), "the plant's cell is fogged")
                .Require(() => !found.IsForbidden(Faction.OfPlayer), "the plant is forbidden")
                .Require(() => found != null && (found.HarvestableNow), "the plant is not harvestable now")
                .Require(() => found != null && (!(map.zoneManager.ZoneAt(found.Position) is Zone_Growing) || ResourceAcquisitionTools.Plantation(found)), "the plant stands in a growing zone and is not a sown tree")
                .Require(() => found != null && (!ResourceAcquisitionTools.Designated(found)), "the plant is already designated")
                .Require(() => found != null && (ResourceAcquisitionTools.DesignatorFor(found).CanDesignateThing(found).Accepted), "the native designator refuses the plant")
                .Require(() => found != null && (map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, found))), "no free colonist with plant cutting enabled can reach the plant");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            plant = found;
            return true;
        }
    }

    // One acquisition request: the census source, its resource and cell.
    internal sealed class AcquireRequest
    {
        internal string SourceId = "", ResourceDefName = "";
        internal RimGovernor.Brain.Domain.Cell Cell = new RimGovernor.Brain.Domain.Cell();
        internal bool Withdraw;
    }

    // The acquisition guard's resolver: a Designate of
    // HARVEST_PLANT, HUNT or MINE on one census source (target, cell,
    // expected_def the resource), checked live by the per-kind Prepare
    // rules; with withdraw, that designation removed (the planner's stall
    // withdraw; a hunter already on the prey is stopped, a plant's CutPlant
    // goes too).
    // A source already in the requested state applies again, and a withdraw
    // of a source that is gone applies too: nothing is designated. Applied
    // means ordered; the next census reads progress.
    internal static class NativeAcquire
    {
        private static AcquireRequest? Request(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) =>
            intent.Target != null && GameThread.IsIdentifier(intent.Target.Id) && intent.ExpectedDef is not null && GameThread.IsIdentifier(intent.ExpectedDef)
            && intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At location && location.Cell.X >= 0 && location.Cell.Z >= 0
            && intent.ThingId is null && !intent.ReplaceWithWall
                ? new AcquireRequest { SourceId = intent.Target.Id, ResourceDefName = intent.ExpectedDef, Cell = location.Cell, Withdraw = intent.Withdraw } : null;

        // The designation a source takes: hunt an animal, mine a rock,
        // harvest (or chop) a plant.
        private static RimGovernor.Brain.GameOrders.Operations.ThingDesignation DesignationOf(Thing? source) => source is Pawn ? RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Hunt
            : source is Mineable ? RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine : RimGovernor.Brain.GameOrders.Operations.ThingDesignation.HarvestPlant;

        // Source is the live thing the intent names: a mineable, a plant or an
        // animal, found by identity.
        private static Thing? Source(Map map, string id) =>
            (RefIndex.Thing(map, id) is Thing t && (t is Plant || t is Mineable) ? t : null)
            ?? map.mapPawns.AllPawnsSpawned.ById(id);

        private static bool Designated(Thing thing) => thing is Pawn prey ? NativeHuntAcquisition.Designated(prey) : ResourceAcquisitionTools.Designated(thing);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.DesignateIntent designate, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out AcquireRequest? intent, out Thing? source)
        {
            source = null;
            intent = Request(designate);
            if (intent == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The acquisition guard requires an exact target, its resource as expected_def and a cell.");
            var map = GameThread.LoadedMap(context);
            source = Source(map, intent.SourceId);
            if (source != null && DesignationOf(source) != designate.Designation)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The source takes the " + DesignationOf(source) + " designation.");
            if (intent.Withdraw || source != null && source.Spawned && Designated(source)) return null;
            RimGovernor.Brain.GameFacts.Common.Failure failure;
            var ok = source is Pawn ? NativeHuntAcquisition.Prepare(intent, context, out _, out failure)
                : source is Mineable ? NativeMineAcquisition.Prepare(intent, context, out _, out failure)
                : NativePlantAcquisition.Prepare(intent, context, out _, out failure);
            return ok ? null : failure;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.DesignateIntent designate, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(designate, context, out var intent, out var source);
            if (failure != null || intent == null) throw new InvalidOperationException(failure?.Detail);
            var live = source != null && source.Spawned && !source.Destroyed;
            if (intent.Withdraw)
            {
                if (live && source is Pawn prey) NativeHuntAcquisition.Withdraw(prey);
                else if (live)
                {
                    var manager = (source ?? throw new System.InvalidOperationException("Required native value is unavailable: source.")).Map.designationManager;
                    if (source is Mineable) { if (manager.DesignationAt(source.Position, DesignationDefOf.Mine) is Designation mine) manager.RemoveDesignation(mine); }
                    else foreach (var def in new[] { DesignationDefOf.HarvestPlant, DesignationDefOf.CutPlant })
                        if (manager.DesignationOn(source, def) is Designation designation) manager.RemoveDesignation(designation);
                }
                if (live && Designated((source ?? throw new System.InvalidOperationException("Required native value is unavailable: source.")))) throw new InvalidOperationException("Acquisition designation survived the withdraw.");
            }
            else
            {
                if (source == null) throw new InvalidOperationException("The acquisition source is unavailable.");
                if (Designated(source)) return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.AcquisitionCase() };
                if (source is Pawn prey) new Designator_Hunt().DesignateThing(prey);
                else if (source is Mineable rock) { NativeMineAcquisition.Guard(rock); new Designator_Mine().DesignateThing(rock); }
                else ResourceAcquisitionTools.DesignatorFor(source).DesignateThing(source);
                if (!Designated(source)) throw new InvalidOperationException("Native acquisition designation was not observed.");
            }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.AcquisitionCase() };
        }
    }
}
