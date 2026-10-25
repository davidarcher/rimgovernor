#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools
{
    // control/pawn-activity-delivery (#3398): the staged hauler, ripe crop and two stockpile cells bound by load id and
    // cell; an exact harvest and exact deliveries installed straight into the native execution table, with a blocked
    // destination and a replaced stack injected after admission.
    public sealed partial class PawnControlFixture
    {
        private static IntVec3 deliveryCell, revisedDeliveryCell;
        private static ThingDef? harvestProduct;
        private static int harvestedCount;
        private static HashSet<string> precedingFood = new();
        private static Thing? deliverySource;
        private static IntVec3 deliverySourceCell;

        [Tool("test/pawn_delivery_prepare", Description="UNSAFE FOR MODEL EXECUTION. Bind the staged hauler, ripe crop and the two delivery cells; stand every colonist still.")]
        public async Task<object> PrepareDelivery(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="The hauler's load id.")] string hauler = "",
            [ToolParameter(Description="The ripe crop's load id.")] string crop = "",
            [ToolParameter(Description="The first delivery cell, \"x,z\".")] string destination = "",
            [ToolParameter(Description="The revised delivery cell, \"x,z\".")] string revised = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            worker = FixtureKit.Spawned<Pawn>(map, hauler);
            var plant = FixtureKit.Spawned<Plant>(map, crop);
            PawnControlFixture.crop = plant;
            harvestProduct = plant.def.plant.harvestedThingDef ?? throw new ArgumentException($"{plant.def.defName} harvests nothing.");
            deliveryCell = FixtureKit.InBounds(map, FixtureKit.Cells(destination).Single());
            revisedDeliveryCell = FixtureKit.InBounds(map, FixtureKit.Cells(revised).Single());
            Still(map);
            precedingFood = new HashSet<string>(map.listerThings.AllThings.Where(t => t.def == harvestProduct).Select(t => t.GetUniqueLoadID()), StringComparer.Ordinal);
            deliverySource = null;
            deadline = Find.TickManager.TicksGame + 6000;
            harvestedCount = 0;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_delivery_harvest", Description="UNSAFE FOR MODEL EXECUTION. Install an exact harvest in the prepared delivery fixture.")]
        public async Task<object> HarvestDelivery(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d => d.Worker is WorkGiver_GrowerHarvest).defName;
            var reading = NativeFoodActivity.Read(new(Context().Identity!, EquatableArray.From(new[] { crop!.GetUniqueLoadID() })));
            if (!reading.Options.Any(o => o.Pawn == worker!.GetUniqueLoadID() && o.Target == crop.GetUniqueLoadID()))
                throw new InvalidOperationException("No native eligible harvest-to-storage observation.");
            NativePawnActivities.Apply(new PawnActivities(((RimGovernor.Contracts.Wire.Common.ObservedGeneration.AtCase)Context().NativeGeneration).Value,
                new[] { new PawnActivityOrder(worker!.GetUniqueLoadID(), "food-delivery", "food-method", 0, 1, false,
                    new[] { new PawnActivityStep(PawnActivityKind.Work, crop!.GetUniqueLoadID(), giver, "Harvest", 0, deadline) }) }), Context());
            // The idle colonists run vanilla with their work priorities off (#3669).
            IdleColonists.Apply();
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
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
            if (revised)
            {
                // Replacing the physical stack invalidates the first delivery's identity without changing stock.
                var count = product.stackCount;
                product.Destroy();
                product = GenSpawn.Spawn(harvestProduct!, deliverySourceCell, worker!.Map);
                product.stackCount = count;
                deliverySource = product;
            }
            var cell = revised ? revisedDeliveryCell : deliveryCell;
            var order = new PawnActivityOrder(worker!.GetUniqueLoadID(), "food-delivery", "food-method", revised ? 2 : 1, revised ? 3 : 2, false,
                new[] { new PawnActivityStep(PawnActivityKind.Deliver, product.GetUniqueLoadID(), "", "", 1, deadline).At(new Cell(cell.x, cell.z)) });
            var batch = new PawnActivities(((RimGovernor.Contracts.Wire.Common.ObservedGeneration.AtCase)Context().NativeGeneration).Value, new[] { order });
            NativePawnActivities.Apply(batch, Context());
            NativePawnActivities.Apply(batch, Context());
            if (!revised) GenSpawn.Spawn(ThingDefOf.Steel, cell, worker.Map);
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_delivery_read", Description="UNSAFE FOR MODEL EXECUTION. Read actual edible food and exact delivery locations, independent of job success.")]
        public async Task<object> ReadDelivery(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            int Count(IntVec3 cell) => cell.GetThingList(worker!.Map).Where(t => t.def == harvestProduct && t.IngestibleNow).Sum(t => t.stackCount);
            return FixtureKit.Reply(new Fx.DeliveryRead { Execution = Execution(NativePawnActivities.Read(worker!)),
                Source = deliverySource == null ? 0 : Count(deliverySourceCell), Destination = Count(deliveryCell), RevisedDestination = Count(revisedDeliveryCell),
                HarvestedCount = harvestedCount, Forced = worker!.CurJob?.playerForced == true });
        }, ct).ConfigureAwait(false);
    }
}
