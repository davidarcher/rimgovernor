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

internal static class NativeRecipeActivity
{
    /// <summary>
    /// Every free colonist's tailoring alternatives at every bench, stuff, stack and accepting storage cell (#3341). A
    /// stack's carry and return legs and a bench's output legs are searched once for every colonist (NativePathLegs).
    /// </summary>
    internal static Obs.RecipeActivityRead Read(Obs.RecipeActivityRequest request, NativePathLegs? legs = null)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        legs ??= new(map);
        var options = new List<Obs.RecipeActivityOption>(); var findings = new HashSet<string>();
        var retained = new List<Obs.RecipeRetainedOutput>();
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var bench in map.listerThings.AllThings.OfType<Building_WorkTable>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
        foreach (var recipe in bench.def.AllRecipes.Where(r => r.products.Any(p => p.thingDef.IsApparel)).OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (!NativeActivityRecipe.TailoringRecipe(bench, recipe)) { findings.Add("unsupported_recipe:" + recipe.defName); continue; }
            var product = recipe.products[0].thingDef;
            var standing = bench.BillStack.Bills.OfType<Bill_Production>().Where(b => b.recipe == recipe && b.ShouldDoNow())
                .OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
            var givers = DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(g => g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(bench))
                .OrderBy(g => g.defName, StringComparer.Ordinal).ToArray();
            var unfinished = map.listerThings.AllThings.OfType<UnfinishedThing>().Where(u => u.Recipe == recipe && u.BoundWorkTable == bench).ToArray();
            foreach (var worker in pawns.Where(p => p.CurJob?.bill?.recipe == recipe && p.CurJob.targetA.Thing == bench))
            {
                if (worker.jobs.curDriver is not JobDriver_DoBill running || running.billStartTick <= 0 || running.workLeft < 0
                    || worker.CurJob.targetB.Thing is not UnfinishedThing current) { findings.Add("unknown_existing_recipe:" + recipe.defName); continue; }
                var rate = recipe.workSpeedStat == null ? 1 : worker.GetStatValue(recipe.workSpeedStat);
                if (recipe.workTableSpeedStat != null) rate *= bench.GetStatValue(recipe.workTableSpeedStat);
                retained.Add(new(current.GetUniqueLoadID(), product.defName, current.Stuff.defName, 1,
                    rate > 0 ? Fact.Known((double)running.workLeft / rate) : default));
            }
            foreach (var stuff in DefDatabase<ThingDef>.AllDefsListForReading.Where(s => s.IsStuff
                && product.stuffCategories.Any(c => s.stuffProps.categories.Contains(c)) && recipe.ingredients.All(i => i.filter.Allows(s))).OrderBy(s => s.defName, StringComparer.Ordinal))
            foreach (var pawn in pawns)
            {
                if (pawn.Downed || pawn.Drafted || pawn.InMentalState || pawn.carryTracker.CarriedThing != null
                    || !NativeProductionBills.Usable(bench) || bench.IsForbidden(pawn)
                    || recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null
                    || !pawn.CanReserveAndReach(bench, PathEndMode.InteractionCell, Danger.None)) continue;
                var giver = givers.FirstOrDefault(g => !pawn.WorkTypeIsDisabled(g.workType) && !pawn.WorkTagIsDisabled(g.workTags)
                    && ((WorkGiver_DoBill)g.Worker).MissingRequiredCapacity(pawn) == null);
                if (giver == null) continue;
                var uft = unfinished.Where(u => u.Stuff == stuff && u.Creator == pawn && !u.IsForbidden(pawn)
                    && pawn.CanReserveAndReach(u, PathEndMode.ClosestTouch, Danger.None)).OrderBy(u => u.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault();
                var billOptions = uft != null ? new Bill_Production?[] { uft.BoundBill }
                    : standing.Length == 0 ? new Bill_Production?[] { null }
                    : standing.Where(b => b.PawnAllowedToStartAnew(pawn) && b.GetStoreMode() == BillStoreModeDefOf.DropOnFloor
                        && b.IsFixedOrAllowedIngredient(stuff)).Cast<Bill_Production?>().ToArray();
                if (standing.Length != 0 && billOptions.Length == 0) findings.Add("blocked_existing_recipe:" + recipe.defName);
                foreach (var selectedBill in billOptions)
                {

                var inputs = new List<Obs.RecipeIngredientObservation>();
                var required = recipe.ingredients.Sum(i => (long)i.CountRequiredOfFor(stuff, recipe, null));
                if (required <= 0 || required > int.MaxValue) { findings.Add("unsupported_ingredient_count:" + recipe.defName); continue; }
                double travel = 0;
                var stocks = uft != null ? new Thing[] { uft } : map.listerThings.ThingsOfDef(stuff)
                    .Where(t => t.Spawned && !t.IsForbidden(pawn) && !t.IsBurning()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
                foreach (var stack in stocks)
                {
                    if (!pawn.CanReserveAndReach(stack, PathEndMode.ClosestTouch, Danger.None)
                        || selectedBill != null && uft == null && stack.Position.DistanceTo(bench.Position) > selectedBill.ingredientSearchRadius) continue;
                    var capacity = pawn.carryTracker.MaxStackSpaceEver(stack.def);
                    if (capacity <= 0) continue;
                    if (legs.Cost(pawn, pawn.Position, stack.Position, PathEndMode.ClosestTouch) is not { } approach
                        || legs.Cost(pawn, stack.Position, bench.InteractionCell, PathEndMode.OnCell) is not { } carry
                        || legs.Cost(pawn, bench.InteractionCell, stack.Position, PathEndMode.ClosestTouch) is not { } back) continue;
                    if (uft == null) inputs.Add(new(stack.GetUniqueLoadID(), stuff.defName, stack.stackCount, new(stack.Position.x, stack.Position.z), stack.stackCount)
                    { Capacity = capacity, ApproachTicks = Fact.Known((double)approach), CarryTicks = Fact.Known((double)carry), ReturnTicks = Fact.Known((double)back) });
                    else travel = approach + carry;
                }
                if (uft == null && inputs.Sum(i => (long)i.Count) < required) continue;
                var destinations = map.haulDestinationManager.AllGroupsListInPriorityOrder.Where(g => g.Settings.filter.Allows(product))
                    .SelectMany(g => g.CellsList).Distinct().OrderBy(c => c.x).ThenBy(c => c.z);                var speed = recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(recipe.workSpeedStat);
                if (recipe.workTableSpeedStat != null) speed *= bench.GetStatValue(recipe.workTableSpeedStat);
                if (speed <= 0) { findings.Add("unknown_recipe_speed:" + pawn.GetUniqueLoadID()); continue; }
                var work = uft?.workLeft ?? recipe.WorkAmountForStuff(stuff);
                if (work < 0) { findings.Add("unknown_recipe_work:" + recipe.defName); continue; }
                foreach (var destination in destinations)
                {
                    if (!JobDriver_GovernorDeliver.DestinationAvailable(pawn, destination)) continue;
                    if (legs.Cost(pawn, bench.InteractionCell, destination, PathEndMode.OnCell) is not { } output) continue;
                    options.Add(new(options.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), pawn.GetUniqueLoadID(), bench.GetUniqueLoadID(), giver.defName,
                        recipe.defName, product.defName, stuff.defName, selectedBill?.GetUniqueLoadID() ?? "", uft?.GetUniqueLoadID() ?? "",
                        new(bench.InteractionCell.x, bench.InteractionCell.z), EquatableArray.From(inputs), Fact.Known(travel), Fact.Known((double)work / speed),
                        Fact.Known((double)output), new(destination.x, destination.z)) { RequiredCount = (int)required, Learning = RimGovernor.Brain.Bridge.DefinitionCatalog.ObserveRecipeLearning(recipe) });
                }
                }
            }
        }
        return new() { Context = context, Known = true,
            Options = EquatableArray.From(options), Retained = EquatableArray.From(retained.Distinct()), Findings = EquatableArray.From(findings.Order(StringComparer.Ordinal)) };
    }
}
