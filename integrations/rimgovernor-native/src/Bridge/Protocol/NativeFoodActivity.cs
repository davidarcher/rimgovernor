using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Native;

/// <summary>Native legality and route observations. No orders, demand or economic selection.</summary>
internal static class NativeFoodActivity
{
    /// <summary>One native-internal food read: the asked plants.</summary>
    internal sealed record Request(RimGovernor.Brain.GameFacts.Common.Identity Identity, EquatableArray<string> Plants);

    internal static bool Available(Pawn pawn) => !NativeBabyCare.Occupied(pawn)
        && (pawn.CurJob == null || NativePawnActivities.Standing(pawn) || pawn.CurJob.def == JobDefOf.Wait || pawn.CurJob.def == JobDefOf.Wait_Wander || pawn.CurJob.def == JobDefOf.GotoWander);
    /// <summary>
    /// Every free colonist's harvest and haul rows for the asked plants, one per accepting storage group: its cheapest
    /// flood-nearest exact cell (#3341). A source's route to a cell is searched once for every colonist (NativePathLegs).
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.FoodActivityRead Read(Request request, NativePathLegs? legs = null)
    {
        if (!GameThread.ValidateIdentity(request.Identity, out var map, out _, out _)) throw new InvalidOperationException("food_activity_identity_invalid");
        legs ??= new(map);
        // Every pawn's approach legs start at its own cell, so a read searches them anew once the pawn has moved: the
        // read runs batched (NativePathLegs.Batched), its legs searched together on the path job workers (#3556).
        return legs.Batched(() => ReadOnce(request, map, legs));
    }

    private static RimGovernor.Brain.GameFacts.Observations.FoodActivityRead ReadOnce(Request request, Map map, NativePathLegs legs)
    {
        var options = new List<RimGovernor.Brain.GameFacts.Observations.FoodActivityOption>();
        var storage = new Dictionary<Thing, List<List<IntVec3>>>(); // Per source: its accepting groups' cells, flood-nearest first.
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var plants = request.Plants.Select(id => RefIndex.Thing(map, id))
            .OfType<Plant>().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_GrowerHarvest);
        foreach (var pawn in pawns)
        {
            if (pawn.Downed || pawn.InMentalState || pawn.Drafted || !pawn.IsColonistPlayerControlled
                || pawn.carryTracker.CarriedThing != null || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                || pawn.WorkTagIsDisabled(WorkTags.ManualDumb) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
            if (NativePawnActivities.Owned(pawn) || !Available(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0) continue;
            foreach (var plant in plants)
            {
                Thing source = plant; var product = plant.def.plant.harvestedThingDef;
                if (product?.ingestible is null || !product.IsNutritionGivingIngestible || source.IsForbidden(pawn)
                    || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)) continue;
                var harvestAllowed = !(giver?.Worker is not WorkGiver_Scanner scanner || !plant.HarvestableNow
                    || pawn.WorkTagIsDisabled(giver.workTags) || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType))
                    || scanner.MissingRequiredCapacity(pawn) != null || !scanner.HasJobOnCell(pawn, plant.Position, false));
                var sourceCount = plant.YieldNow();
                var count = Math.Min(sourceCount, pawn.carryTracker.MaxStackSpaceEver(product));
                if (count <= 0) continue;
                var work = plant.def.plant.harvestWork / Math.Max(0.01f, JobDriver_PlantWork.WorkDonePerTick(pawn, plant));
                if (legs.Cost(pawn, pawn.Position, source, PathEndMode.Touch) is not { } approach) continue;
                // Each accepting storage group's cheapest exact, empty cell for this pawn: of the group's cells in flood
                // order from the source, the first Nearest the pawn can use are searched. Policy ranks the groups' rows.
                if (!storage.TryGetValue(source, out var groups))
                    storage[source] = groups = StorageByGroup(map, source.Position, map.haulDestinationManager.AllGroupsListInPriorityOrder
                        .Where(g => g.Settings.filter.Allows(product)).ToList());
                foreach (var ordered in groups)
                {
                    (IntVec3 Cell, float Route)? best = null;
                    var searched = 0;
                    foreach (var candidate in ordered)
                    {
                        if (candidate == source.Position || !JobDriver_GovernorDeliver.DestinationAvailable(pawn, candidate)
                            || !pawn.CanReserveAndReach(candidate, PathEndMode.OnCell, Danger.None)) continue;
                        if (legs.Cost(pawn, source.Position, candidate, PathEndMode.OnCell) is { } cost && (best is null || cost < best.Value.Route))
                            best = (candidate, cost);
                        if (++searched == Nearest) break;
                    }
                    if (best is not { } chosen) continue;
                    var (cell, route) = chosen;
                    options.Add(new(pawn.GetUniqueLoadID(), source.GetUniqueLoadID(), product.defName,
                        new(source.Position.x, source.Position.z), new(cell.x, cell.z), giver?.defName ?? "", count, work) {
                            ServicePosition = legs.ServicePosition(pawn, pawn.Position, source, PathEndMode.Touch),
                            SkillWork = NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Plants, StatDefOf.PlantWorkSpeed,
                                plant.def.plant.harvestWork, UnityEngine.Mathf.Lerp(3.3f, 1f, plant.Growth), .085, true, false, "JobDriver_PlantHarvest: learning before work"),
                            ApproachTicks = approach, DeliveryTicks = route, HarvestAllowed = harvestAllowed, SourceCount = sourceCount });
                }
            }
        }
        return new() { Options = EquatableArray.From(options) };
    }

    /// <summary>The flood-nearest cells searched per storage group: flood steps are cardinal, paths also diagonal.</summary>
    private const int Nearest = 3;

    /// <summary>
    /// Each group's cells nearest first, by one flood fill from the source over walkable cells (cardinal steps), which
    /// stops once every group cell is reached. A cell the fill never reaches has no path from the source.
    /// </summary>
    private static List<List<IntVec3>> StorageByGroup(Map map, IntVec3 root, List<SlotGroup> accepting)
    {
        var groups = accepting.Select(_ => new List<IntVec3>()).ToList();
        var owner = new Dictionary<IntVec3, int>();
        for (var i = 0; i < accepting.Count; i++)
            foreach (var cell in accepting[i].CellsList) if (!owner.ContainsKey(cell)) owner[cell] = i;
        var left = owner.Count;
        if (left == 0) return groups;
        var walkable = map.pathing.Normal.pathGrid;
        map.floodFiller.FloodFill(root, c => c == root || walkable.Walkable(c), (c, _) =>
        {
            if (owner.TryGetValue(c, out var group)) { groups[group].Add(c); left--; }
            return left == 0;
        });
        return groups;
    }
}
