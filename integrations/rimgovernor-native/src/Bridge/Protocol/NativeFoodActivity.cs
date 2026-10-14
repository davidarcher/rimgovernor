#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Bounded native legality and route observations. No orders, demand or economic selection.</summary>
public sealed class NativeFoodActivity
{
    internal static bool Available(Pawn pawn) => pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait || pawn.CurJob.def == JobDefOf.Wait_Wander;
    [Tool("rimgovernor/food_activity", Description = "Read bounded eligible harvest/delivery routes and current loose food. Read-only FoodActivityRequest; known=false is unavailable, truncated=true is an incomplete candidate search.")]
    public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "FoodActivityRequest JSON.")] object? request = null)
    {
        if (!ProtoBoundary.TryParse<Obs.FoodActivityRequest>(ctx, "rimgovernor/food_activity", request!, out var parsed, out _))
            return ProtoBoundary.Encode(new Obs.FoodActivityRead());
        var result = await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed), ct).ConfigureAwait(false);
        return ProtoBoundary.Encode(result);
    }

    internal static Obs.FoodActivityRead Read(Obs.FoodActivityRequest request)
    {
        if (request.Plants.Count > 16 || !ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        var result = new Obs.FoodActivityRead { Context = context };
        var options = new List<Obs.FoodActivityOption>();
        var existing = request.Existing;
        var truncated = false;
        var budget = 128; // Bounds native route probes per read, independently of colony size.
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        if (existing is not null)
        {
            pawns = pawns.Where(p => p.GetUniqueLoadID() == existing.Pawn).ToArray();
            if (pawns.Length != 1) return result; // Missing pawn is unknown, not a completed delivery.
            result = result with { Execution = NativePawnActivities.Read(pawns[0]), Carrying = pawns[0].carryTracker.CarriedThing != null };
            if (result.Execution?.State is not ("completed" or "failed") && !(existing.Preparing && result.Execution?.State == "holding")) return result with { Known = true };
        }
        if (pawns.Length > 16) { pawns = pawns.Take(16).ToArray(); truncated = true; }
        var plants = request.Plants.Select(id => map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id))
            .OfType<Plant>().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_GrowerHarvest);
        foreach (var pawn in pawns)
        {
            if (pawn.Downed || pawn.InMentalState || pawn.Drafted || !pawn.IsColonistPlayerControlled
                || pawn.carryTracker.CarriedThing != null || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                || pawn.WorkTagIsDisabled(WorkTags.ManualDumb) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
            if (existing is null && (NativePawnActivities.Owned(pawn) || !Available(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0)) continue;
            IEnumerable<Thing> sources = plants;
            if (existing?.OutputDelivery is { } output)
                sources = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                    .Where(t => t.Spawned && t.def.defName == output.Resource && t.Position.DistanceToSquared(output.Cooking is null
                        ? new(output.Origin.X, 0, output.Origin.Z) : new(output.Destination.X, 0, output.Destination.Z)) <= 4)
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
            if (sources.Count() > 16) { sources = sources.Take(16); truncated = true; }
            foreach (var source in sources)
            {
                if (budget <= 0) { truncated = true; break; }
                var plant = source as Plant;
                var product = plant?.def.plant.harvestedThingDef ?? source.def;
                if (product?.ingestible is null || !product.IsNutritionGivingIngestible || source.IsForbidden(pawn)
                    || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)) continue;
                var harvestAllowed = plant is not null && !(giver?.Worker is not WorkGiver_Scanner scanner || !plant.HarvestableNow
                    || pawn.WorkTagIsDisabled(giver.workTags) || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType))
                    || scanner.MissingRequiredCapacity(pawn) != null || !scanner.HasJobOnCell(pawn, plant.Position, false));
                var count = Math.Min(plant is null ? source.stackCount : plant.YieldNow(), pawn.carryTracker.MaxStackSpaceEver(product));
                if (existing?.OutputDelivery is { } obligation) count = Math.Min(count, obligation.Count);
                if (count <= 0) continue;
                var work = plant is null ? 0 : plant.def.plant.harvestWork / Math.Max(0.01f, pawn.GetStatValue(StatDefOf.PlantWorkSpeed));
                using var approach = map.pathFinder.FindPathNow(pawn.Position, source, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                budget--;
                if (!approach.Found) continue;
                if (existing?.OutputDelivery?.Cooking is not null)
                {
                    NativeFoodCooking.Add(pawn, source, count, approach.TotalCost, existing, options, ref budget, ref truncated);
                    continue; // Supply was selected; rebind that exact recipe only.
                }
                // Enumerate exact, empty storage cells. Policy ranks options; native never chooses storage.
                var cells = map.haulDestinationManager.AllGroupsListInPriorityOrder.Where(g => plant is null
                        ? g.Settings.AllowedToAccept(source) : g.Settings.filter.Allows(product))
                    .SelectMany(g => g.CellsList).Distinct().OrderBy(c => c.x).ThenBy(c => c.z).Take(9).ToArray();
                if (cells.Length > 8) truncated = true;
                foreach (var cell in cells.Take(8))
                {
                    if (budget <= 0) { truncated = true; break; }
                    if (cell == source.Position || !JobDriver_GovernorDeliver.DestinationAvailable(pawn, cell)
                        || !pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.None)) continue;
                    using var route = map.pathFinder.FindPathNow(source.Position, cell, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                    budget--;
                    if (!route.Found) continue;
                    options.Add(new(pawn.GetUniqueLoadID(), source.GetUniqueLoadID(), product.defName,
                        new(source.Position.x, source.Position.z), new(cell.x, cell.z), giver?.defName ?? "", count,
                        approach.TotalCost + route.TotalCost, work) { NutritionPerUnit = Fact.Known((double)product.GetStatValueAbstract(StatDefOf.Nutrition)),
                            ApproachTicks = Fact.Known((double)approach.TotalCost), DeliveryTicks = Fact.Known((double)route.TotalCost), HarvestAllowed = Fact.Known(harvestAllowed) });
                }
                // Preserve raw alternatives before recipe expansion can spend the remaining route budget.
                if (plant is null && existing is not null && (request.AllowCooking || existing.Steps[0].Kind == PawnActivityKind.Work))
                    NativeFoodCooking.Add(pawn, source, count, approach.TotalCost, existing, options, ref budget, ref truncated);
            }
            if (budget <= 0) { truncated = true; break; }
        }
        return result with { Known = true, Truncated = truncated, Options = EquatableArray.From(options) };
    }
}
