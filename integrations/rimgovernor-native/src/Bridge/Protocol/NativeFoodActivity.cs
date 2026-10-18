#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Native legality and route observations. No orders, demand or economic selection.</summary>
internal static class NativeFoodActivity
{
    /// <summary>One native-internal food read: the asked plants, or the one pawn's executing delivery in <paramref name="Existing"/>.</summary>
    internal sealed record Request(RimGovernor.Contracts.Wire.Common.Identity Identity, EquatableArray<string> Plants, PawnActivityOrder? Existing = null)
    {
        internal bool AllowCooking { get; init; }
    }

    internal static bool Available(Pawn pawn) => pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait || pawn.CurJob.def == JobDefOf.Wait_Wander || pawn.CurJob.def == JobDefOf.GotoWander;
    /// <summary>
    /// Every free colonist's harvest and haul rows for the asked plants (or, executing, the one pawn's), one per
    /// accepting storage group: its cheapest flood-nearest exact cell (#3341). A source's route to a cell is searched once for every colonist (NativePathLegs).
    /// Only the cooking expansion of an executing delivery (NativeFoodCooking) keeps its route budget.
    /// </summary>
    internal static Obs.FoodActivityRead Read(Request request, NativePathLegs? legs = null)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out _, out _)) return new();
        legs ??= new(map);
        var result = new Obs.FoodActivityRead();
        var options = new List<Obs.FoodActivityOption>();
        var storage = new Dictionary<Thing, List<List<IntVec3>>>(); // Per source: its accepting groups' cells, flood-nearest first.
        var existing = request.Existing;
        var budget = 128; // NativeFoodCooking's route budget; only an executing delivery expands cooking.
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        if (existing is not null)
        {
            pawns = pawns.Where(p => p.GetUniqueLoadID() == existing.Pawn).ToArray();
            if (pawns.Length != 1) return result; // Missing pawn is unknown, not a completed delivery.
            var execution = NativePawnActivities.Read(pawns[0]);
            if (execution?.State is not ("completed" or "failed") && !(existing.Preparing && execution?.State == "holding")) return result;
        }
        var plants = request.Plants.Select(id => RefIndex.Thing(map, id))
            .OfType<Plant>().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_GrowerHarvest);
        foreach (var pawn in pawns)
        {
            if (pawn.Downed || pawn.InMentalState || pawn.Drafted || !pawn.IsColonistPlayerControlled
                || pawn.carryTracker.CarriedThing != null || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                || pawn.WorkTagIsDisabled(WorkTags.ManualDumb) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
            if (existing is null && (NativePawnActivities.Committed(pawn) || !Available(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0)) continue;
            IEnumerable<Thing> sources = plants;
            if (existing?.OutputDelivery is { } output)
                sources = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                    .Where(t => t.Spawned && t.def.defName == output.Resource && t.Position.DistanceToSquared(output.Recipe is null
                        ? new(output.Origin.X, 0, output.Origin.Z) : new(output.Destination.X, 0, output.Destination.Z)) <= 4)
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
            foreach (var source in sources)
            {
                var plant = source as Plant;
                var product = plant?.def.plant.harvestedThingDef ?? source.def;
                if (product?.ingestible is null || !product.IsNutritionGivingIngestible || source.IsForbidden(pawn)
                    || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)) continue;
                var harvestAllowed = plant is not null && !(giver?.Worker is not WorkGiver_Scanner scanner || !plant.HarvestableNow
                    || pawn.WorkTagIsDisabled(giver.workTags) || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType))
                    || scanner.MissingRequiredCapacity(pawn) != null || !scanner.HasJobOnCell(pawn, plant.Position, false));
                var sourceCount = plant is null ? source.stackCount : plant.YieldNow();
                var count = Math.Min(sourceCount, pawn.carryTracker.MaxStackSpaceEver(product));
                if (existing?.OutputDelivery is { } obligation) count = Math.Min(count, obligation.Count);
                if (count <= 0) continue;
                var work = plant is null ? 0 : plant.def.plant.harvestWork / Math.Max(0.01f, pawn.GetStatValue(StatDefOf.PlantWorkSpeed));
                if (legs.Cost(pawn, pawn.Position, source, PathEndMode.Touch) is not { } approach) continue;
                if (existing?.OutputDelivery?.Recipe is not null)
                {
                    NativeFoodCooking.Add(pawn, source, count, existing, options, ref budget);
                    continue; // Supply was selected; rebind that exact recipe only.
                }
                // Each accepting storage group's cheapest exact, empty cell for this pawn: of the group's cells in flood
                // order from the source, the first Nearest the pawn can use are searched. Policy ranks the groups' rows.
                if (!storage.TryGetValue(source, out var groups))
                    storage[source] = groups = StorageByGroup(map, source.Position, map.haulDestinationManager.AllGroupsListInPriorityOrder
                        .Where(g => plant is null ? g.Settings.AllowedToAccept(source) : g.Settings.filter.Allows(product)).ToList());
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
                            ApproachTicks = Fact.Known((double)approach), DeliveryTicks = Fact.Known((double)route), HarvestAllowed = Fact.Known(harvestAllowed), SourceCount = Fact.Known((double)sourceCount) });
                }
                if (plant is null && existing is not null && (request.AllowCooking || existing.Steps[0].Kind == PawnActivityKind.Work))
                    NativeFoodCooking.Add(pawn, source, count, existing, options, ref budget);
            }
        }
        return result with { Options = EquatableArray.From(options) };
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
