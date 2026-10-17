#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeFoodCooking
{
    internal static void Add(Pawn pawn, Thing source, int available, double approach, PawnActivityOrder existing,
        List<Obs.FoodActivityOption> options, ref int budget, ref bool truncated)
    {
        var map = pawn.Map;
        var pinned = existing.OutputDelivery!.Recipe;
        var benches = map.listerThings.AllThings.OfType<Building_WorkTable>()
            .Where(b => pinned is null || b.GetUniqueLoadID() == pinned.Bench).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).Take(5).ToArray();
        if (benches.Length > 4) truncated = true;
        foreach (var bench in benches.Take(4))
        {
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(g => g.workType == ExtraWorkTypeDefOf.Cooking
                && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(bench));
            if (giver is null) continue;
            var bills = bench.BillStack.Bills.OfType<Bill_Production>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).Take(9).ToArray();
            if (bills.Length > 8) truncated = true;
            foreach (var bill in bills.Take(8))
            {
                var recipe = bill.recipe;
                if (pinned is not null && (bill.GetUniqueLoadID() != pinned.Bill || recipe.defName != pinned.Recipe)) continue;
                if (!NativeActivityRecipe.ResolveRecipe(pawn, bench, giver.defName, bill.GetUniqueLoadID(), recipe.defName, out _, out _)
                    || !bill.IsFixedOrAllowedIngredient(source) || recipe.ingredients.Any(i => !i.filter.Allows(source.def))) continue;
                var required = recipe.ingredients.Sum(i => (long)i.CountRequiredOfFor(source.def, recipe, bill));
                if (required <= 0 || required > available || required > int.MaxValue) continue;
                var product = recipe.products[0];
                if (product.count > pawn.carryTracker.MaxStackSpaceEver(product.thingDef)) continue;
                var speed = recipe.workSpeedStat is null ? 1 : pawn.GetStatValue(recipe.workSpeedStat);
                var work = recipe.WorkAmountTotal(null);
                if (speed <= 0 || work <= 0) continue;
                var staging = pinned is null
                    ? GenRadial.RadialCellsAround(bench.Position, 3, true).Where(c => c.InBounds(map) && c != source.Position
                        && c.DistanceTo(bench.Position) <= bill.ingredientSearchRadius && map.haulDestinationManager.SlotGroupAt(c) == null && JobDriver_GovernorDeliver.DestinationAvailable(pawn, c))
                        .OrderBy(c => c.x).ThenBy(c => c.z).Take(3).ToArray()
                    : new[] { source.Position };
                if (staging.Length > 2) truncated = true;
                var destinations = map.haulDestinationManager.AllGroupsListInPriorityOrder.Where(g => g.Settings.filter.Allows(product.thingDef))
                    .SelectMany(g => g.CellsList).Distinct().OrderBy(c => c.x).ThenBy(c => c.z).Take(5).ToArray();
                if (destinations.Length > 4) truncated = true;
                foreach (var supply in staging.Take(2))
                foreach (var destination in destinations.Take(4))
                {
                    if (budget < 3) { truncated = true; return; }
                    if (!JobDriver_GovernorDeliver.DestinationAvailable(pawn, destination) || supply == destination) continue;
                    using var toSupply = map.pathFinder.FindPathNow(source.Position, supply, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                    // FindPathNow expects the resolved destination; it does not resolve InteractionCell.
                    using var toBench = map.pathFinder.FindPathNow(supply, bench.InteractionCell, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                    using var toStore = map.pathFinder.FindPathNow(bench.InteractionCell, destination, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                    budget -= 3;
                    if (!toSupply.Found || !toBench.Found || !toStore.Found) continue;
                    if (pinned is not null)
                    {
                        var exact = new PawnActivityStep(PawnActivityKind.Recipe, bench.GetUniqueLoadID(), giver.defName, "", 0, existing.OutputDelivery.Deadline)
                            { Recipe = new(bill.GetUniqueLoadID(), recipe.defName, [new(source.GetUniqueLoadID(), (int)required)]) };
                        if (!NativeActivityRecipe.Resolve(pawn, bench, exact, out _, out _, out _)) continue;
                    }
                    var continuation = new ActivityRecipeContinuation(bench.GetUniqueLoadID(), bill.GetUniqueLoadID(), recipe.defName, giver.defName,
                        new(bench.InteractionCell.x, bench.InteractionCell.z), product.thingDef.defName, product.count, new(destination.x, destination.z));
                    options.Add(new(pawn.GetUniqueLoadID(), source.GetUniqueLoadID(), source.def.defName, new(source.Position.x, source.Position.z),
                        new(supply.x, supply.z), "", available, approach + toSupply.TotalCost + toBench.TotalCost, 0)
                    { Cooking = new(continuation, (int)required) { Learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe) } });
                }
            }
        }
    }
}
