#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativePlantAcquisition
    {
        private static bool Eligible(Plant plant) => ProtoBoundary.IsLoaded(plant.Map) && ResourceAcquisitionTools.Eligible(plant, plant.Map)
            && plant.Map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, plant));
        internal static Obs.SnapshotRef Snapshot(Plant plant, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = plant.GetUniqueLoadID(), Token = NativeAcquisitionToken.Plant(context.Identity, plant.GetUniqueLoadID(),
                plant.def.plant.harvestedThingDef.defName, plant.Position.x, plant.Position.z, plant.HarvestableNow, ResourceAcquisitionTools.Designated(plant)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, Func<ThingDef, bool> humanFood)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant.harvestedThingDef != null).ToArray();
            // The candidate pool is every eligible plant on the map; pending yield below covers all designations.
            // Medicine-yielding wild plants (healroot) join trees and food so MaintainMedicalReserves can harvest.
            // YieldNow rounds randomly, so one sample per plant decides both eligibility and the row: a plant
            // whose sample is zero (below harvest growth, or a fractional yield rounded down) is not a source.
            var selected = plants.Where(p => (p.def.plant.IsTree || humanFood(p.def.plant.harvestedThingDef) || p.def.plant.harvestedThingDef.IsMedicine) && Eligible(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber)
                .Select(p => (plant: p, yield: p.YieldNow())).Where(s => s.yield > 0).ToArray();
            foreach (var (plant, yield) in selected)
            {
                var resource = plant.def.plant.harvestedThingDef;
                var food = humanFood(resource);
                var designated = ResourceAcquisitionTools.Designated(plant);
                var row = new Obs.AcquisitionFacts {
                    Source = NativeRef.Thing(plant), SourceSnapshot = Snapshot(plant, result.Context),
                    Resource = resource.defName, Tree = plant.def.plant.IsTree, Food = food, Yield = yield,
                    Growth = plant.Growth, Plantation = ResourceAcquisitionTools.Plantation(plant),
                    NutritionYield = food ? yield * resource.GetStatValueAbstract(StatDefOf.Nutrition) : 0,
                    Designated = designated, Hunt = false, Taken = ResourceAcquisitionTools.Taken(plant) };
                var tick = ResourceAcquisitionTools.DesignatedTick(plant, designated);
                if (tick.HasValue) row.DesignatedTick = tick.Value;
                result.Acquisition.Add(row);
            }
            var pending = plants.Where(ResourceAcquisitionTools.Designated).ToArray();
            result.PendingFoodNutrition = pending.Where(p => humanFood(p.def.plant.harvestedThingDef)).Sum(p => (double)p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition));
            NativeHuntAcquisition.Read(result, map, center);
            result.PendingWoodUnits = pending.Where(p => p.def.plant.harvestedThingDef == ThingDefOf.WoodLog).Sum(p => (double)p.YieldNow());
        }
        internal const string Kind = "Plant acquisition";
        // Cutter is the colonist rule shared by the census (Eligible) and the
        // apply-time check: someone must be able to do the work now.
        internal static bool Cutter(Pawn p, Plant plant) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.Position.DistanceTo(plant.Position) <= 50
            && p.CanReach(plant, PathEndMode.Touch, Danger.None);
        // Prepare is the apply-time precondition list for cut/harvest
        // (action-contracts.md): the conjunction is Eligible plus the
        // request's own cell, resource and designation rules, evaluated one
        // rule at a time so a refusal names the fact that moved.
        internal static bool Prepare(AcquireRequest command, Common.ObservationContext context, out Plant? plant, out Common.Failure failure)
        {
            plant = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Acquisition requires an exact safe mature wild plant snapshot.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = RefIndex.Thing<Plant>(map, command.SourceId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the plant is not at the expected cell")
                .Require(() => found!.def.plant.harvestedThingDef?.defName == command.ResourceDefName, "the plant no longer yields the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the plant's cell is fogged")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the plant is forbidden")
                .Require(() => found!.HarvestableNow, "the plant is not harvestable now")
                .Require(() => !(map.zoneManager.ZoneAt(found!.Position) is Zone_Growing) || ResourceAcquisitionTools.Plantation(found), "the plant stands in a growing zone and is not a sown tree")
                .Require(() => !ResourceAcquisitionTools.Designated(found!), "the plant is already designated")
                .Require(() => ResourceAcquisitionTools.DesignatorFor(found!).CanDesignateThing(found).Accepted, "the native designator refuses the plant")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, found!)), "no free colonist with plant cutting enabled can reach the plant");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            plant = found;
            return true;
        }
    }

    // One acquisition request: the census source, its resource and cell.
    internal sealed class AcquireRequest
    {
        internal string SourceId = "", ResourceDefName = "";
        internal Common.Cell Cell = new Common.Cell();
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
        private const string Kind = "Acquire";

        private static AcquireRequest? Request(Operations.DesignateIntent intent) =>
            intent.Target != null && ProtoBoundary.IsIdentifier(intent.Target.Id) && intent.HasExpectedDef && ProtoBoundary.IsIdentifier(intent.ExpectedDef)
            && intent.Cell != null && intent.Cell.HasX && intent.Cell.HasZ && intent.Cell.X >= 0 && intent.Cell.Z >= 0
            && !intent.HasThingId && intent.ClearedGround.Count == 0 && !intent.ReplaceWithWall
                ? new AcquireRequest { SourceId = intent.Target.Id, ResourceDefName = intent.ExpectedDef, Cell = intent.Cell, Withdraw = intent.Withdraw } : null;

        // The designation a source takes: hunt an animal, mine a rock,
        // harvest (or chop) a plant.
        private static Operations.ThingDesignation DesignationOf(Thing? source) => source is Pawn ? Operations.ThingDesignation.Hunt
            : source is Mineable ? Operations.ThingDesignation.Mine : Operations.ThingDesignation.HarvestPlant;

        // Source is the live thing the intent names: a mineable, a plant or an
        // animal, found by identity.
        private static Thing? Source(Map map, string id) =>
            (RefIndex.Thing(map, id) is Thing t && (t is Plant || t is Mineable) ? t : null)
            ?? map.mapPawns.AllPawnsSpawned.ById(id);

        private static bool Designated(Thing thing) => thing is Pawn prey ? NativeHuntAcquisition.Designated(prey) : ResourceAcquisitionTools.Designated(thing);

        private static Common.Failure? Resolve(Operations.DesignateIntent designate, Common.ObservationContext context, out AcquireRequest? intent, out Thing? source)
        {
            source = null;
            intent = Request(designate);
            if (intent == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The acquisition guard requires an exact target, its resource as expected_def and a cell.");
            var map = ProtoBoundary.LoadedMap(context);
            source = Source(map, intent.SourceId);
            if (source != null && DesignationOf(source) != designate.Designation)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The source takes the " + DesignationOf(source) + " designation.");
            if (intent.Withdraw || source != null && source.Spawned && Designated(source)) return null;
            Common.Failure failure;
            var ok = source is Pawn ? NativeHuntAcquisition.Prepare(intent, context, out _, out failure)
                : source is Mineable ? NativeMineAcquisition.Prepare(intent, context, out _, out failure)
                : NativePlantAcquisition.Prepare(intent, context, out _, out failure);
            return ok ? null : failure;
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent designate, Common.ObservationContext context)
        {
            var failure = Resolve(designate, context, out var intent, out var source);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var live = source != null && source.Spawned && !source.Destroyed;
            if (intent!.Withdraw)
            {
                if (live && source is Pawn prey) NativeHuntAcquisition.Withdraw(prey);
                else if (live)
                {
                    var manager = source!.Map.designationManager;
                    if (source is Mineable) { if (manager.DesignationAt(source.Position, DesignationDefOf.Mine) is Designation mine) manager.RemoveDesignation(mine); }
                    else foreach (var def in new[] { DesignationDefOf.HarvestPlant, DesignationDefOf.CutPlant })
                        if (manager.DesignationOn(source, def) is Designation designation) manager.RemoveDesignation(designation);
                }
                if (live && Designated(source!)) throw new InvalidOperationException("Acquisition designation survived the withdraw.");
            }
            else if (!Designated(source!))
            {
                if (source is Pawn prey) new Designator_Hunt().DesignateThing(prey);
                else if (source is Mineable rock) { NativeMineAcquisition.Guard(rock); new Designator_Mine().DesignateThing(rock); }
                else ResourceAcquisitionTools.DesignatorFor(source!).DesignateThing(source);
                if (!Designated(source!)) throw new InvalidOperationException("Native acquisition designation was not observed.");
            }
            return new Receipts.EffectEvidence { Acquisition = new Receipts.AcquisitionEffect { SourceId = intent.SourceId, ResourceDef = intent.ResourceDefName,
                Cell = intent.Cell.Clone(), Designated = live && Designated(source!) } };
        }
    }
}
