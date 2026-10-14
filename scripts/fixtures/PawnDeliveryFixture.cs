#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;

namespace HomeBridge.BridgeTools
{
    public sealed partial class PawnControlFixture
    {
        private static IntVec3 deliveryCell, revisedDeliveryCell;
        private static ThingDef? harvestProduct;
        private static int harvestedCount;
        private static HashSet<string> precedingFood = new();
        private static Thing? deliverySource;
        private static IntVec3 deliverySourceCell;

        [Tool("test/pawn_delivery_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare a ripe edible crop and two empty exact delivery destinations.")]
        public async Task<object> PrepareDelivery(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            worker = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !p.WorkTagIsDisabled(WorkTags.ManualDumb));
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
            var cells = GenRadial.RadialCellsAround(worker.Position, 8, false).Where(c => c.InBounds(map)
                && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(worker.Position) > 3
                && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Take(3).ToArray();
            if (cells.Length != 3) throw new InvalidOperationException("No quiet delivery cells.");
            foreach (var plant in cells[0].GetThingList(map).OfType<Plant>().ToArray()) plant.Destroy();
            var definition = DefDatabase<ThingDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)
                .First(d => d.plant?.Sowable == true && d.plant.harvestedThingDef?.IsNutritionGivingIngestible == true);
            crop = GenSpawn.Spawn(definition, cells[0], map);
            map.areaManager.Home[cells[0]] = true;
            ((Plant)crop).Growth = 1;
            harvestProduct = definition.plant.harvestedThingDef;
            foreach (var stack in map.listerThings.AllThings.Where(t => t.def == harvestProduct && t.Position.DistanceTo(cells[0]) < 4).ToArray()) stack.Destroy();
            precedingFood = new HashSet<string>(map.listerThings.AllThings.Where(t => t.def == harvestProduct).Select(t => t.GetUniqueLoadID()), StringComparer.Ordinal);
            var destinations = GenRadial.RadialCellsAround(worker.Position, 10, false).Where(c => c.InBounds(map)
                && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(cells[0]) > 6
                && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Take(2).ToArray();
            if (destinations.Length != 2) throw new InvalidOperationException("No separate delivery destinations.");
            deliveryCell = destinations[0]; revisedDeliveryCell = destinations[1]; deliverySource = null;
            worker.skills.GetSkill(SkillDefOf.Plants).Level = 20;
            worker.needs.food.CurLevelPercentage = 1; worker.needs.rest.CurLevelPercentage = 1;
            deadline = Find.TickManager.TicksGame + 6000;
            harvestedCount = 0;
            return new { success = true };
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_delivery_harvest", Description="UNSAFE FOR MODEL EXECUTION. Install an exact harvest in the prepared delivery fixture.")]
        public async Task<object> HarvestDelivery(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d => d.Worker is WorkGiver_GrowerHarvest).defName;
            NativePawnActivities.Apply(new PawnActivities(Context().NativeGeneration!.Value,
                new[] { new PawnActivityOrder(worker!.GetUniqueLoadID(), "food-delivery", "food-method", 0, 1, false,
                    new[] { new PawnActivityStep(PawnActivityKind.Work, crop!.GetUniqueLoadID(), giver, "Harvest", 0, deadline) }) }), Context());
            return new { success = true };
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_delivery_select", Description="UNSAFE FOR MODEL EXECUTION. Bind observed harvested food to an exact delivery; optionally block the destination after admission.")]
        public async Task<object> SelectDelivery(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="First attempt blocks its destination; the second explicitly chooses another cell.")] bool revised = false) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (NativePawnActivities.Read(worker!)?.State != (revised ? "failed" : "completed"))
                throw new InvalidOperationException("Missing prior execution evidence.");
            var product = revised ? deliverySource : worker!.Map.listerThings.AllThings.SingleOrDefault(t =>
                t.def == harvestProduct && !precedingFood.Contains(t.GetUniqueLoadID()));
            if (product == null || !product.Spawned) throw new InvalidOperationException("No unambiguous observed harvest output.");
            if (!product.IngestibleNow || product.stackCount < 2) throw new InvalidOperationException("No edible split-stack harvest.");
            if (!revised) { harvestedCount = product.stackCount; deliverySource = product; deliverySourceCell = product.Position; }
            var cell = revised ? revisedDeliveryCell : deliveryCell;
            var order = new PawnActivityOrder(worker!.GetUniqueLoadID(), "food-delivery", "food-method", revised ? 2 : 1, revised ? 3 : 2, false,
                new[] { new PawnActivityStep(PawnActivityKind.Deliver, product.GetUniqueLoadID(), "", "", 1, deadline)
                    { Destination = new Cell(cell.x, cell.z) } });
            var batch = new PawnActivities(Context().NativeGeneration!.Value, new[] { order });
            NativePawnActivities.Apply(batch, Context());
            NativePawnActivities.Apply(batch, Context());
            if (!revised) GenSpawn.Spawn(ThingDefOf.Steel, cell, worker.Map);
            return new { success = true, source = product.GetUniqueLoadID(), quantity = product.stackCount };
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_delivery_read", Description="UNSAFE FOR MODEL EXECUTION. Read actual edible food and exact delivery locations, independent of job success.")]
        public async Task<object> ReadDelivery(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            int Count(IntVec3 cell) => cell.GetThingList(worker!.Map).Where(t => t.def == harvestProduct && t.IngestibleNow).Sum(t => t.stackCount);
            return new { success = true, execution = NativePawnActivities.Read(worker!), source = deliverySource == null ? 0 : Count(deliverySourceCell),
                destination = Count(deliveryCell), revisedDestination = Count(revisedDeliveryCell), harvestedCount,
                forced = worker!.CurJob?.playerForced == true };
        }, ct).ConfigureAwait(false);
    }
}
