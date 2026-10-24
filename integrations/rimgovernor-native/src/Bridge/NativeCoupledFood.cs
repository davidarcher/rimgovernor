#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeCoupledFood
{
    private static readonly Dictionary<ThingDef, Thing> FreshUnits = new();

    /// <summary>
    /// Every food source's cooking conversions at every player bench, for every free colonist, to every accepting
    /// storage cell (#3341). The source-to-bench, bench-to-cell and return legs are searched once each for every
    /// colonist (NativePathLegs).
    /// </summary>
    internal static (Obs.CoupledFoodConversion[] Conversions, Obs.CoupledFoodUse[] Uses, Obs.CoupledFoodSource[] Sources) Read(Map map, Obs.FoodActivityRead food,
        NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var conversions = new List<Obs.CoupledFoodConversion>(); var uses = new List<Obs.CoupledFoodUse>();
        var sourceRows = new List<Obs.CoupledFoodSource>();
        var benches = map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer).OrderBy(b => b.GetUniqueLoadID(),StringComparer.Ordinal).ToArray();
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var consumers = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer && p.needs?.food != null)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var hunt in food.Options)
            if (RefIndex.Thing(map, hunt.Target) is Pawn prey && prey.RaceProps.meatDef is { } meat) AddUses(meat, hunt.Destination, FreshUnit(meat));
        var plants = food.Options.Select(o => o.Target).Distinct().Select(id => RefIndex.Thing(map, id)).OfType<Plant>().Cast<Thing>().ToArray();
        var loose = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && t.def.IsNutritionGivingIngestible)
            .OrderBy(t => t.GetUniqueLoadID(),StringComparer.Ordinal).ToArray();
        var sources = plants.Concat(loose);
        // A governor meal is cooked from the one ingredient its batch is supplied (#3872): a use per product, location and ingredient.
        var cooked = new List<(ThingDef Product, Cell Location, ThingDef Ingredient)>();
        foreach (var source in sources)
        {
            var plant = source as Plant; var raw = plant?.def.plant.harvestedThingDef ?? source.def;
            var availableInput = plant == null ? source.stackCount : food.Options.Where(o => o.Target == source.GetUniqueLoadID())
                .Select(o => o.SourceCount.IsKnown ? (int)o.SourceCount.Value : 0).DefaultIfEmpty(0).Min();
            if (plant == null) sourceRows.Add(new(source.GetUniqueLoadID(),raw.defName,new(source.Position.x,source.Position.z),source.stackCount,
                Fact.Known(source.TryGetComp<CompRottable>() is { } rottable ? (double)rottable.TicksUntilRotAtCurrentTemp : (double)long.MaxValue)));
            // A loose source with ingredients is eaten with its own; any other is a fresh unit of its product.
            foreach (var target in food.Options.Where(o => o.Target == source.GetUniqueLoadID()).Select(o => o.Destination).Distinct())
                AddUses(raw, target, plant == null && raw.HasComp(typeof(CompIngredients)) ? source : FreshUnit(raw));
            foreach (var bench in benches)
            foreach (var bill in bench.BillStack.Bills.OfType<Bill_Production>().OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var recipe = bill.recipe;
                if (!recipe.AvailableNow || recipe.products.Count != 1 || recipe.products.Any(p => !p.thingDef.IsNutritionGivingIngestible)
                    || recipe.UsesUnfinishedThing || recipe.ingredients.Any(i => !i.filter.Allows(raw))) continue;
                var required = recipe.ingredients.Sum(i => (long)i.CountRequiredOfFor(raw, recipe, bill));
                // A row certifies an eligible source contribution, not a complete single-source batch.
                // Policy may combine independently observed sources against this exact total requirement.
                if (required <= 0 || availableInput <= 0 || required > int.MaxValue) continue;
                foreach (var pawn in roster)
                {
                    if (pawn.Downed || pawn.Drafted || pawn.InMentalState || !NativeProductionBills.Usable(bench)
                        || bench.IsForbidden(pawn) || source.IsForbidden(pawn)

                        || !pawn.CanReserveAndReach(bench, PathEndMode.InteractionCell, Danger.None)) continue;
                    var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(g => g.workType == ExtraWorkTypeDefOf.Cooking
                        && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(bench));
                    if (giver == null || !bill.IsFixedOrAllowedIngredient(raw)) continue;
                    var canCook = !pawn.WorkTypeIsDisabled(ExtraWorkTypeDefOf.Cooking)
                        && recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) == null
                        && NativeActivityRecipe.ResolveRecipe(pawn,bench,giver.defName,bill.GetUniqueLoadID(),recipe.defName,out _,out _);
                    var canHaul = !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                        && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);
                    if (!canCook && !canHaul) continue;
                    var capacity = pawn.carryTracker.MaxStackSpaceEver(raw);
                    canHaul &= capacity > 0;
                    var speed = recipe.workSpeedStat == null ? 1 : pawn.GetStatValue(recipe.workSpeedStat);
                    if (recipe.workTableSpeedStat != null) speed *= bench.GetStatValue(recipe.workTableSpeedStat);
                    canCook &= speed > 0;
                    if (!canCook && !canHaul) continue;
                    // The ingredient delivery is strict-exact-destination: it refuses a cell holding any item, staged rice included.
                    var stage = NativeSupportActivities.Staging(map, bench, c => JobDriver_GovernorDeliver.DestinationAvailable(pawn, c));
                    var skillWork = canCook ? NativeProductiveSkill.Read(pawn, recipe, bench) : null;
                    // CompFoodPoisonable.Notify_RecipeProduced: the room's chance first, then the cook's own.
                    var room = bench.InteractionCell.GetRoom(map);
                    var roomPoison = room?.GetStat(RoomStatDefOf.FoodPoisonChance) ?? RoomStatDefOf.FoodPoisonChance.roomlessScore;
                    Fact<double> poison = canCook ? Fact.Known(roomPoison + (1 - roomPoison) * (double)pawn.GetStatValue(StatDefOf.FoodPoisonChance)) : default;
                    foreach (var product in recipe.products)
                    {
                        var destinations = map.haulDestinationManager.AllGroupsListInPriorityOrder.Where(g => g.Settings.filter.Allows(product.thingDef))
                            .SelectMany(g => g.CellsList).Distinct().OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
                        foreach (var destination in destinations)
                        {
                            if (!JobDriver_GovernorDeliver.DestinationAvailable(pawn, destination)) continue;
                            if (legs.Cost(pawn, pawn.Position, source, PathEndMode.Touch) is not { } approach
                                || legs.Cost(pawn, source.Position, stage, PathEndMode.OnCell) is not { } supply
                                || legs.Cost(pawn, bench.InteractionCell, destination, PathEndMode.OnCell) is not { } delivery) continue;
                            var productCapacity = pawn.carryTracker.MaxStackSpaceEver(product.thingDef);
                            var trips = productCapacity > 0 ? Math.Ceiling((double)product.count / productCapacity) : 0;
                            Fact<double> deliveryTicks = trips == 1 ? Fact.Known((double)delivery) : default;
                            if (trips > 1 && legs.Cost(pawn, destination, bench.InteractionCell, PathEndMode.OnCell) is { } back)
                                deliveryTicks = Fact.Known(trips * delivery + (trips - 1) * back);
                            conversions.Add(new(source.GetUniqueLoadID(), pawn.GetUniqueLoadID(), bench.GetUniqueLoadID(), recipe.defName,
                                raw.defName, (int)required, product.thingDef.defName, product.count,
                                new(source.Position.x, source.Position.z), new(stage.x, stage.z), new(destination.x, destination.z),
                                Fact.Known((double)approach), Fact.Known((double)supply),
                                speed > 0 ? Fact.Known(recipe.WorkAmountTotal(null) / (double)speed) : default, deliveryTicks, capacity, availableInput) { WorkGiver = giver.defName, Bill = bill.GetUniqueLoadID(), RequiresHarvest = plant != null, CanCook = canCook, CanSupply = canHaul, CanDeliver = canHaul && productCapacity > 0,
                                    SkillWork = skillWork, FoodPoisonChance = poison });
                            cooked.Add((product.thingDef, new(destination.x, destination.z), raw));
                        }
                    }
                }
            }
        }
        foreach (var (product, location, ingredient) in cooked.Distinct()) AddUses(product, location, CookedUnit(product, ingredient), ingredient.defName);
        return (conversions.ToArray(), uses.Distinct().ToArray(), sourceRows.ToArray());
        void AddUses(ThingDef resource, Cell location, Thing unit, string ingredient = "")
        {
            foreach (var pawn in consumers)
            {
                if (pawn.needs?.food == null) continue;
                // FoodUtility.ThoughtsFromIngesting gives a pawn without a mood none, and reads the story of one with a mood.
                if (pawn.needs.mood != null && pawn.story == null)
                    throw new InvalidOperationException("food consumer with a mood and no story: " + pawn.GetUniqueLoadID());
                var thoughts = EquatableArray.From(FoodUtility.ThoughtsFromIngesting(pawn, unit, resource).Select(t => NativeNeedsForecast.MealMemory(pawn, t)).ToArray());
                var nutrition = resource.GetStatValueAbstract(StatDefOf.Nutrition);
                if (ModsConfig.BiotechActive && resource.IsRawHumanFood()) nutrition *= pawn.GetStatValue(StatDefOf.RawNutritionFactor);
                var rot = resource.GetCompProperties<CompProperties_Rottable>();
                // Conditional fresh-output shelf life at this observed destination temperature; future weather is unassessed.
                Fact<double> fresh = rot == null ? Fact.Known((double)long.MaxValue) : default;
                var temperature = new IntVec3(location.X, 0, location.Z).GetTemperature(map);
                if (rot != null) fresh = Fact.Known<double>(GenTemperature.RotRateAtTemperature(temperature) > 0
                    ? rot.daysToRotStart * RimGovernor.Brain.Domain.GameTime.TicksPerDay / GenTemperature.RotRateAtTemperature(temperature) : (double)long.MaxValue);
                uses.Add(new(pawn.GetUniqueLoadID(), resource.defName, location,
                    Fact.Known(pawn.WillEat(resource) && pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn)?.filter.Allows(resource) != false),
                    Fact.Known((double)nutrition / pawn.needs.food.MaxLevel),
                    Fact.Known(resource.ingestible.baseIngestTicks / (double)(resource.ingestible.useEatingSpeedStat ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1)),
                    resource.ingestible.maxNumToIngestAtOnce, fresh) { Thoughts = thoughts, Ingredient = ingredient });
            }
        }
    }

    /// <summary>An unspawned fresh unit of <paramref name="resource"/>, one per def for the session, so repeated reads draw no
    /// further thing ids.</summary>
    private static Thing FreshUnit(ThingDef resource)
    {
        if (!FreshUnits.TryGetValue(resource, out var unit)) FreshUnits[resource] = unit = ThingMaker.MakeThing(resource);
        return unit;
    }

    /// <summary>
    /// A fresh unit of a cooked product as GenRecipe.MakeRecipeProducts makes it: CompIngredients.RegisterIngredient for each
    /// ingredient thing's def. A governor recipe step hands JobDriver_DoBill its batch's ingredient stacks (job.targetQueueB from
    /// NativeActivityRecipe.Resolve: one def, never a substitute search), so WorkGiver_DoBill's ingredient search never runs and
    /// the meal holds exactly <paramref name="ingredient"/> (#3872). FoodUtility.ThoughtsFromIngesting then reads that list: the
    /// cannibal and insect ingredient thoughts, the precepts on human meat, fungus and plants, and the meat and vegetarian food
    /// kinds. The unit is shared per product, so the caller reads its thoughts before the next call registers another ingredient.
    /// </summary>
    private static Thing CookedUnit(ThingDef product, ThingDef ingredient)
    {
        var unit = FreshUnit(product);
        if (unit.TryGetComp<CompIngredients>() is not { } ingredients) return unit;
        ingredients.ingredients.Clear();
        ingredients.RegisterIngredient(ingredient);
        return unit;
    }
}
