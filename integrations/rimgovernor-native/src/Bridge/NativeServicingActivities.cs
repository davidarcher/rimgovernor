using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Building = Verse.Building;
using Cell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Brain.GameFacts.Observations;
namespace RimGovernor.Native;

// One native census of the colony's buildings that need servicing (repair, breakdown fix, refuel, door-to-wall swap) and
// every free colonist's measured servicing of each (task 5.1 of redefine-habitat-requirements). Conditional mechanics and
// current condition only, never new demand: the brain joins each target to its canonical building row by id. Every
// target and every standing equipment building is read, none cut: the read's legs are searched together on the path
// job workers (NativePathLegs.Batched) and kept across reads, so the colony's size costs it no game-thread searches.
internal static class NativeServicingActivities
{
    // JobDriver_Repair's private WarmupTicks and TicksBetweenRepairs, JobDriver_FixBrokenDownBuilding's private TicksDuration
    // and the 1.7 the construction drivers multiply ConstructionSpeed by (a literal in their tick actions): no typed route
    // exists to a private const or an inline literal, so the values are copied here from the decompiled drivers
    // (recorded under "Mirrored game literals: servicing drivers" in docs/developers/contracts/kept-constants.md).
    private const double RepairWarmupWork = 80, RepairWorkPerHitPoint = 20, FixTicks = 1000, WorkPerSpeed = 1.7;

    internal static Obs.ServicingActivityRead Read(Map map, NativePathLegs? legs = null)
    {
        var shared = legs ?? new(map);
        return shared.Batched(() => ReadOnce(map, shared));
    }

    // The read itself, run twice by Batched: once collecting the legs it asks, once against the searched legs.
    private static Obs.ServicingActivityRead ReadOnce(Map map, NativePathLegs legs)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal) { "servicing_routes_are_conditional_duration_estimates", "servicing_supply_nearest_stack_legs" };
        var targets = map.listerBuildings.allBuildingsColonist.Where(b => b.Spawned && Needs(b))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var roster = map.mapPawns.FreeColonistsSpawned.Where(Available).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var repair = Giver(def => typeof(WorkGiver_Repair).IsAssignableFrom(def.giverClass));
        var fix = Giver(def => typeof(WorkGiver_FixBrokenDownBuilding).IsAssignableFrom(def.giverClass));
        var deconstruct = Giver(def => typeof(WorkGiver_Deconstruct).IsAssignableFrom(def.giverClass));
        // The swap's wall is built natively from its blueprint: the frame's finishing giver's work type must be active too.
        var build = Giver(def => typeof(WorkGiver_ConstructFinishFrames).IsAssignableFrom(def.giverClass));
        var rows = new List<Obs.ServicingTarget>(); var workers = new List<Obs.ServicingWorker>();
        var consumed = new HashSet<ThingDef>();
        foreach (var building in targets)
        {
            var refuel = building.TryGetComp<CompRefuelable>();
            var wallStuff = WallStuff(building);
            rows.Add(new(building.GetUniqueLoadID(), building.def.defName, At(building.Position))
            {
                Home = map.areaManager.Home[building.Position],
                Repairable = building.def.useHitPoints && building.def.building.repairable,
                Fuel = refuel == null ? new Obs.ServicingFuel.NotRefuelable()
                    : new Obs.ServicingFuel.Refuel(refuel.IsFull, refuel.GetFuelCountToFullyRefuel(), refuel.Props.allowRefuelIfNotEmpty),
                Removal = Removal(building),
                Replacement = Replacement(building, wallStuff),
            });
            var id = building.GetUniqueLoadID();
            if (Repairs(building) && repair != null)
                foreach (var pawn in Capable(roster, repair.workType))
                    if (legs.Cost(pawn, pawn.Position, building, PathEndMode.Touch) is { } approach)
                        workers.Add(Row(id, Obs.ServicingKind.Repair, pawn, repair, approach, Construction(pawn), null));
            if (building.IsBrokenDown() && building.def.building.repairable && fix != null)
            {
                consumed.Add(ThingDefOf.ComponentIndustrial);
                var components = Stacks(map, building, t => t.def == ThingDefOf.ComponentIndustrial);
                foreach (var pawn in Capable(roster, fix.workType))
                    if (legs.Cost(pawn, pawn.Position, building, PathEndMode.Touch) is { } approach)
                        workers.Add(Row(id, Obs.ServicingKind.FixBreakdown, pawn, fix, approach, Fixing(pawn), Supply(legs, pawn, building, Nearest(components, pawn))));
            }
            if (refuel != null && !refuel.IsFull && RefuelGiver(building) is { } refueler)
            {
                consumed.UnionWith(refuel.Props.fuelFilter.AllowedThingDefs);
                var fuels = Stacks(map, building, refuel.Props.fuelFilter.Allows);
                foreach (var pawn in Capable(roster, refueler.workType))
                    if (legs.Cost(pawn, pawn.Position, building, PathEndMode.Touch) is { } approach)
                        workers.Add(Row(id, Obs.ServicingKind.Refuel, pawn, refueler, approach, new Obs.ServicingAbility.Carrying(), Supply(legs, pawn, building, Nearest(fuels, pawn))));
            }
            if (wallStuff != null && deconstruct != null && build != null)
            {
                consumed.Add(wallStuff);
                var stuffs = Stacks(map, building, t => t.def == wallStuff);
                foreach (var pawn in Capable(roster, deconstruct.workType))
                    if (legs.Cost(pawn, pawn.Position, building, PathEndMode.Touch) is { } approach)
                        workers.Add(Row(id, Obs.ServicingKind.Replace, pawn, deconstruct, approach, Construction(pawn), Supply(legs, pawn, building, Nearest(stuffs, pawn))) with
                            { WorkActive = Active(pawn, deconstruct) && Active(pawn, build) });
            }
        }
        var transitions = Transitions(map, legs, roster, deconstruct, findings);
        var stock = consumed.OrderBy(def => def.defName, StringComparer.Ordinal)
            .Select(def => new Obs.ServicingStock(def.defName, map.listerThings.ThingsOfDef(def).Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount)));
        return new()
        {
            Mechanics = new(WorkPerSpeed, RepairWarmupWork, RepairWorkPerHitPoint, FixTicks, JobDriver_Refuel.RefuelingDuration, ThingDefOf.ComponentIndustrial.defName),
            Targets = EquatableArray.From(rows), Workers = EquatableArray.From(workers), Stock = EquatableArray.From(stock), Transitions = EquatableArray.From(transitions),
            Findings = EquatableArray.From(findings),
        };
    }

    private static Cell At(IntVec3 c) => new(c.x, c.z);

    // Each standing player building a plan may transition (no wall, door, fence, conduit or frame): its uninstall and
    // deconstruct mechanics, the storage that would take it minified, and the nearest capable builder's legs.
    private static List<Obs.TransitionMechanics> Transitions(Map map, NativePathLegs legs, Pawn[] roster, WorkGiverDef? deconstruct, SortedSet<string> findings)
    {
        var standing = map.listerBuildings.allBuildingsColonist.Where(b => b.Spawned && Transitionable(b))
            .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var uninstall = Giver(def => typeof(WorkGiver_Uninstall).IsAssignableFrom(def.giverClass));
        var builders = deconstruct == null ? Array.Empty<Pawn>() : Capable(roster, deconstruct.workType).ToArray();
        var output = new List<Obs.TransitionMechanics>();
        foreach (var building in standing)
        {
            var minifiable = building.def.Minifiable && building.Faction == Faction.OfPlayer;
            var storage = minifiable ? Storage(map, building) : new Obs.TransitionStorage.None("not_minifiable");
            var room = building.GetRoom();
            var outside = room == null || !room.ProperRoom || room.PsychologicallyOutdoors;
            output.Add(new(building.GetUniqueLoadID(), building.def.defName, At(building.Position))
            {
                Beauty = building.GetBeauty(room == null || room.PsychologicallyOutdoors),
                Wealth = building.MarketValue,
                Room = outside ? new Obs.TransitionRoom.None("outdoors") : new Obs.TransitionRoom.Inside(room!.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Minifiable = minifiable,
                UninstallWork = building.def.building.uninstallWork,
                Deconstructible = building.DeconstructibleBy(Faction.OfPlayer).Accepted,
                BrokenDown = building.IsBrokenDown(),
                Removal = Removal(building),
                Storage = storage,
                Worker = TransitionWorker(map, legs, builders, building, storage, uninstall, deconstruct),
            });
        }
        if (output.Count > 0) findings.Add("transition_nearest_worker_legs");
        return output;
    }

    // Equipment, not structure: a wall, a door, a power conduit or a frame is the room's or the build side's, and a fence
    // segment (BuildingProperties.isFence) is a pen's boundary, the herd arrangement's.
    private static bool Transitionable(Building b) => b.def.building != null && !b.def.IsFrame && !b.def.IsDoor && !b.def.IsSmoothed && !b.def.building.isFence
        && !(b.def.passability == Traversability.Impassable && !b.def.Minifiable && !b.def.hasInteractionCell)
        && !(b.TryGetComp<CompPowerTransmitter>() != null && !b.def.hasInteractionCell && !b.def.Minifiable);

    // The highest-priority player slot group that accepts the building (StorageSettings.AllowedToAccept; ThingFilter.Allows
    // unwraps a minified thing to its inner building), at its cell nearest the building.
    private static Obs.TransitionStorage Storage(Map map, Building building)
    {
        var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder
            .Where(g => (g.parent is not Thing parent || parent.Faction == Faction.OfPlayer) && g.parent.HaulDestinationEnabled && g.Settings.AllowedToAccept(building) && g.CellsList.Count > 0)
            .ToList();
        if (groups.Count == 0) return new Obs.TransitionStorage.None("no_accepting_storage");
        var best = groups[0].Settings.Priority;
        var cell = groups.Where(g => g.Settings.Priority == best).SelectMany(g => g.CellsList)
            .OrderBy(c => c.DistanceToSquared(building.Position)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        return new Obs.TransitionStorage.Accepting(At(cell));
    }

    // The capable builder nearest the building in a straight line, its walk to it and its loaded carry from it to the store.
    private static Obs.TransitionWorker? TransitionWorker(Map map, NativePathLegs legs, Pawn[] builders, Building building, Obs.TransitionStorage storage,
        WorkGiverDef? uninstall, WorkGiverDef? deconstruct)
    {
        var pawn = builders.OrderBy(p => p.Position.DistanceToSquared(building.Position)).ThenBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault();
        if (pawn == null || legs.Cost(pawn, pawn.Position, building, PathEndMode.Touch) is not { } approach) return null;
        Obs.TransitionLeg store = storage is not Obs.TransitionStorage.Accepting { Cell: var cell } ? new Obs.TransitionLeg.Unread("no_accepting_storage")
            : legs.Cost(pawn, building.Position, new IntVec3(cell.X, 0, cell.Z), PathEndMode.OnCell) is { } carry ? new Obs.TransitionLeg.Walked(carry)
            : new Obs.TransitionLeg.Unread("no_path");
        return new(pawn.GetUniqueLoadID(), approach)
        {
            Ability = Construction(pawn),
            TicksPerCell = pawn.TicksPerMoveCardinal,
            UninstallActive = uninstall != null && Active(pawn, uninstall),
            DeconstructActive = deconstruct != null && Active(pawn, deconstruct),
            StoreLeg = store,
        };
    }

    // A colony building needs servicing when it is damaged and repairable, broken down, short of its target fuel, or a door
    // the native swap could replace with a wall.
    private static bool Needs(Building b) => Repairs(b) || b.IsBrokenDown() || b.TryGetComp<CompRefuelable>() is { IsFull: false } || WallStuff(b) != null;

    // RepairUtility.PawnCanRepairEver's def checks and a hit point deficit.
    private static bool Repairs(Building b) => b.def.useHitPoints && b.def.building.repairable && b.HitPoints < b.MaxHitPoints;

    // NativeDesignate's door-to-wall rule: a 1x1 player door whose stuff the wall allows.
    private static ThingDef? WallStuff(Building b) => b.def.IsDoor && b.Faction == Faction.OfPlayer && b.def.size == IntVec2.One
        && b.Stuff != null && GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(b.Stuff) ? b.Stuff : null;

    // A free colonist the native would give servicing work to: not downed, drafted, broken or in an owned activity, carrying nothing.
    private static bool Available(Pawn pawn) => !pawn.Downed && !pawn.Drafted && !pawn.InMentalState && NativeFoodActivity.Available(pawn)
        && !NativePawnActivities.Owned(pawn) && pawn.CurJob?.playerForced != true && pawn.jobs.jobQueue.Count == 0 && pawn.carryTracker.CarriedThing == null;

    private static IEnumerable<Pawn> Capable(Pawn[] roster, WorkTypeDef workType) =>
        roster.Where(p => !p.WorkTypeIsDisabled(workType) && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));

    private static WorkGiverDef? Giver(Func<WorkGiverDef, bool> matches) =>
        DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(d => d.giverClass != null && matches(d)).OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();

    // The refuel giver whose worker takes this thing (WorkGiver_Refuel.CanRefuelThing: a turret's is the rearm subclass).
    private static WorkGiverDef? RefuelGiver(Building b) =>
        Giver(def => typeof(WorkGiver_Refuel).IsAssignableFrom(def.giverClass) && def.Worker is WorkGiver_Refuel w && w.CanRefuelThing(b));

    private static Obs.ServicingWorker Row(string target, Obs.ServicingKind kind, Pawn pawn, WorkGiverDef giver, double approach, Obs.ServicingAbility ability, Obs.ServicingSupply? supply) =>
        new(target, kind, pawn.GetUniqueLoadID(), approach)
        {
            WorkActive = Active(pawn, giver), Ability = ability, Supply = supply,
        };

    private static bool Active(Pawn pawn, WorkGiverDef giver) => pawn.workSettings?.WorkIsActive(giver.workType) == true;

    private static Obs.ServicingAbility Construction(Pawn pawn) => StatDefOf.ConstructionSpeed.Worker.IsDisabledFor(pawn)
        ? new Obs.ServicingAbility.Unread("construction speed:" + pawn.GetUniqueLoadID())
        : new Obs.ServicingAbility.Construction(pawn.GetStatValue(StatDefOf.ConstructionSpeed));

    private static Obs.ServicingAbility Fixing(Pawn pawn) => StatDefOf.FixBrokenDownBuildingSuccessChance.Worker.IsDisabledFor(pawn)
        ? new Obs.ServicingAbility.Unread("fix success chance:" + pawn.GetUniqueLoadID())
        : new Obs.ServicingAbility.Fixing(pawn.GetStatValue(StatDefOf.FixBrokenDownBuildingSuccessChance));

    // The spawned matching stacks nearest the target first; a pawn takes the first it may (not forbidden to it, reservable).
    private static Thing[] Stacks(Map map, Building target, Predicate<Thing> matches) =>
        map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && matches(t))
            .OrderBy(t => t.Position.DistanceToSquared(target.Position)).ThenBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();

    private static Thing? Nearest(Thing[] stacks, Pawn pawn) => stacks.FirstOrDefault(t => !t.IsForbidden(pawn) && pawn.CanReserve(t));

    // The pawn's legs for one supply stack: to it, loaded to the target, and back for another load; null without a stack or a path.
    private static Obs.ServicingSupply? Supply(NativePathLegs legs, Pawn pawn, Building target, Thing? stack)
    {
        if (stack == null) return null;
        if (legs.Cost(pawn, pawn.Position, stack, PathEndMode.ClosestTouch) is not { } fetch
            || legs.Cost(pawn, stack.Position, target, PathEndMode.Touch) is not { } carry
            || legs.Cost(pawn, target.Position, stack, PathEndMode.ClosestTouch) is not { } back) return null;
        return new(stack.def.defName, pawn.carryTracker.MaxStackSpaceEver(stack.def), fetch, carry, back);
    }

    private static IEnumerable<Obs.ServicingCost> Costs(List<ThingDefCountClass> costs) =>
        costs.Select(c => new Obs.ServicingCost(c.thingDef.defName, c.count)).OrderBy(c => c.Resource, StringComparer.Ordinal);

    // GenLeaving's Deconstruct leavings: the target's CostListAdjusted at resourcesFractionWhenDeconstructed; JobDriver_Deconstruct's
    // work is WorkToBuild clamped to 20..3000. A stuffed def without its stuff has no cost list to read.
    private static Obs.ServicingRemoval Removal(Building b)
    {
        if (b.def.MadeFromStuff && b.Stuff == null) return new Obs.ServicingRemoval.Unread("cost list:" + b.def.defName);
        return new Obs.ServicingRemoval.Read(EquatableArray.From(Costs(b.CostListAdjusted())), b.def.resourcesFractionWhenDeconstructed,
            Math.Min(3000.0, Math.Max(20.0, b.GetStatValue(StatDefOf.WorkToBuild))));
    }

    private static Obs.ServicingReplacement Replacement(Building b, ThingDef? stuff)
    {
        if (stuff == null) return new Obs.ServicingReplacement.None(b.def.IsDoor ? "door_without_wall_stuff" : "not_a_door");
        return new Obs.ServicingReplacement.Wall(ThingDefOf.Wall.defName, EquatableArray.From(Costs(ThingDefOf.Wall.CostListAdjusted(stuff))),
            ThingDefOf.Wall.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff), stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor));
    }
}
