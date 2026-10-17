#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    [Tool("test/coupled_forecast_read", Description="UNSAFE FOR MODEL EXECUTION. Validate one-tick recipe, physical stock, needs and thermal observations in the prepared cooking scene.")]
    public async Task<object> CoupledRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = worker!.Map;
            var context = Context();
            var read = NativeCoupledForecast.Read(map,context);
            var needs = NativeNeedsForecast.Read(map,map.mapPawns.FreeColonistsSpawned.ToList());
            if (read.Tick != needs.Tick || read.Tick != Find.TickManager.TicksGame)
                throw new InvalidOperationException("Coupled census mixed native ticks.");
            var conversions = read.FoodConversions.Where(c => !c.RequiresHarvest && c.Source == unselectedIngredient.GetUniqueLoadID()
                && c.Bench == cookingBench.GetUniqueLoadID()).ToArray();
            foreach (var bill in new[] { selectedBill, otherBill })
            {
                var option = conversions.FirstOrDefault(c => c.Bill == bill.GetUniqueLoadID());
                if (option == null || option.ProductCount != bill.recipe.products.Single().count || option.AvailableInput != unselectedIngredient.stackCount
                    || option.IngredientCount != bill.recipe.ingredients.Sum(i => i.CountRequiredOfFor(unselectedIngredient.def,bill.recipe,bill)))
                    throw new InvalidOperationException("Native batch dimensions or exact ingredient option missing.");
            }
            foreach (var partial in new[] { firstIngredient, secondIngredient })
            {
                var contribution = read.FoodConversions.FirstOrDefault(c => c.Source == partial.GetUniqueLoadID()
                    && c.Bill == selectedBill.GetUniqueLoadID());
                if (contribution == null || contribution.AvailableInput != partial.stackCount
                    || contribution.IngredientCount != firstCount + secondCount || contribution.AvailableInput >= contribution.IngredientCount)
                    throw new InvalidOperationException("Eligible partial source was omitted or misrepresented as a complete batch.");
            }
            var source = read.FoodSources.Single(s => s.Target == unselectedIngredient.GetUniqueLoadID());
            if (source.Count != unselectedIngredient.stackCount || !source.FreshTicks.IsKnown)
                throw new InvalidOperationException("Observed source quantity or expiry missing.");
            var thermal = read.Thermal ?? throw new InvalidOperationException("Missing thermal read.");
            if (!thermal.ThinRoofExchangePerTick.IsKnown || !thermal.WallExchangePerTick.IsKnown || thermal.Workers.Count == 0)
                throw new InvalidOperationException("Native roof/heat mechanics missing.");
            var heater = thermal.Sources.Single();
            if (!heater.Complete || !heater.HeatPerTick.IsKnown || heater.SupplyTicks.IsKnown)
                throw new InvalidOperationException("Unpowered heater presence incorrectly established thermal supply.");
            return new { success = true, tick = read.Tick, needsTick = needs.Tick, conversions = conversions.Length,
                sourceCount = source.Count, bulkProducts = otherBill.recipe.products.Single().count,
                roofRate = thermal.ThinRoofExchangePerTick.Value, wallRate = thermal.WallExchangePerTick.Value,
                heatPerTick = heater.HeatPerTick.Value, powerUnassessed = !heater.SupplyTicks.IsKnown,
                limitations = "Current condition observations; no future weather, fuel guarantee, wearing or calibrated thermal residual claim." };
        },ct).ConfigureAwait(false);
}
