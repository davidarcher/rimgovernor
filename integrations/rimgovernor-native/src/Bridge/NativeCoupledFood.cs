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
    internal static (Obs.CoupledFoodConversion[] Conversions, Obs.CoupledFoodUse[] Uses, Obs.CoupledFoodSource[] Sources, bool Truncated) Read(Map map, Obs.FoodActivityRead food)
    {
        var conversions = new List<Obs.CoupledFoodConversion>(); var uses = new List<Obs.CoupledFoodUse>();
        var sourceRows = new List<Obs.CoupledFoodSource>();
        var budget = 128; var truncated = map.mapPawns.FreeColonistsSpawned.Count > 16;
        var benches = map.listerThings.AllThings.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayer).OrderBy(b => b.GetUniqueLoadID(),StringComparer.Ordinal).Take(9).ToArray();
        truncated |= benches.Length > 8;
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(16).ToArray();
                var plants = food.Options.Select(o => o.Target).Distinct().Select(id => map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id)).OfType<Plant>().Take(9).Cast<Thing>().ToArray();
        truncated |= plants.Length > 8;
        var loose = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && t.def.IsNutritionGivingIngestible)
            .OrderBy(t => t.GetUniqueLoadID(),StringComparer.Ordinal).Take(9).ToArray();
        truncated |= loose.Length > 8;
        var sources = plants.Take(8).Concat(loose.Take(8));
        foreach (var source in sources)
        {
                        var plant = source as Plant; var raw = plant?.def.plant.harvestedThingDef ?? source.def;
            var availableInput = plant == null ? source.stackCount : food.Options.Where(o => o.Target == source.GetUniqueLoadID())
                .Select(o => o.SourceCount.IsKnown ? (int)o.SourceCount.Value : 0).DefaultIfEmpty(0).Min();
            if (plant == null) sourceRows.Add(new(source.GetUniqueLoadID(),raw.defName,new(source.Position.x,source.Position.z),source.stackCount,
                Fact.Known((double)(source.TryGetComp<CompRottable>()?.TicksUntilRotAtCurrentTemp ?? RimGovernor.Brain.Domain.GameTime.TicksPerDay))));
            foreach (var target in food.Options.Where(o => o.Target == source.GetUniqueLoadID()).Select(o => o.Destination).Distinct()) AddUses(raw, target);
            foreach (var bench in benches.Take(8))
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
                    if (--budget < 0) { truncated = true; return Result(); }
                    if (pawn.Downed || pawn.Drafted || pawn.InMentalState || !NativeProductionBills.Usable(bench)
                        || bench.IsForbidden(pawn) || source.IsForbidden(pawn)

                        || !pawn.CanReserveAndReach(bench, PathEndMode.InteractionCell, Danger.None)) continue;
                    var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(g => g.workType == DefDatabase<WorkTypeDef>.GetNamed("Cooking")
                        && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(bench));
                    if (giver == null || !bill.IsFixedOrAllowedIngredient(raw)) continue;
                    var canCook = !pawn.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking"))
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
                    var stage = NativeSupportActivities.Staging(map, bench, raw);
                    var skillWork = canCook ? NativeProductiveSkill.Read(pawn, recipe, bench) : null;
                    foreach (var product in recipe.products)
                    {
                        var destinations = map.haulDestinationManager.AllGroupsListInPriorityOrder.Where(g => g.Settings.filter.Allows(product.thingDef))
                            .SelectMany(g => g.CellsList).Distinct().OrderBy(c => c.x).ThenBy(c => c.z).Take(3).ToArray();
                        if (destinations.Length > 2) truncated = true;
                        foreach (var destination in destinations.Take(2))
                        {
                            if (!JobDriver_GovernorDeliver.DestinationAvailable(pawn, destination)) continue;
                            using var approach = map.pathFinder.FindPathNow(pawn.Position, source, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                            using var supply = map.pathFinder.FindPathNow(source.Position, stage, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                            using var delivery = map.pathFinder.FindPathNow(bench.InteractionCell, destination, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                            if (!approach.Found || !supply.Found || !delivery.Found) continue;
                            var productCapacity = pawn.carryTracker.MaxStackSpaceEver(product.thingDef);
                            var trips = productCapacity > 0 ? Math.Ceiling((double)product.count / productCapacity) : 0;
                            Fact<double> deliveryTicks = trips == 1 ? Fact.Known((double)delivery.TotalCost) : default;
                            if (trips > 1)
                            {
                                using var back = map.pathFinder.FindPathNow(destination,bench.InteractionCell,TraverseParms.For(pawn,Danger.None),peMode: PathEndMode.OnCell);
                                if (back.Found) deliveryTicks = Fact.Known(trips * delivery.TotalCost + (trips - 1) * back.TotalCost);
                            }
                            conversions.Add(new(source.GetUniqueLoadID(), pawn.GetUniqueLoadID(), bench.GetUniqueLoadID(), recipe.defName,
                                raw.defName, (int)required, product.thingDef.defName, product.count,
                                new(source.Position.x, source.Position.z), new(stage.x, stage.z), new(destination.x, destination.z),
                                Fact.Known((double)approach.TotalCost), Fact.Known((double)supply.TotalCost),
                                speed > 0 ? Fact.Known(recipe.WorkAmountTotal(null) / (double)speed) : default, deliveryTicks, capacity, availableInput) { WorkGiver = giver.defName, Bill = bill.GetUniqueLoadID(), RequiresHarvest = plant != null, CanCook = canCook, CanSupply = canHaul, CanDeliver = canHaul && productCapacity > 0,
                                    SkillWork = skillWork });
                            AddUses(product.thingDef, new(destination.x, destination.z));
                        }
                    }
                }
            }
        }
        return Result();
        (Obs.CoupledFoodConversion[], Obs.CoupledFoodUse[], Obs.CoupledFoodSource[], bool) Result() => (conversions.ToArray(), uses.Distinct().ToArray(), sourceRows.ToArray(), truncated);
        void AddUses(ThingDef resource, Cell location)
        {
            foreach (var pawn in roster)
            {
                if (pawn.needs?.food == null) continue;
                var nutrition = resource.GetStatValueAbstract(StatDefOf.Nutrition);
                if (ModsConfig.BiotechActive && resource.IsRawHumanFood()) nutrition *= pawn.GetStatValue(StatDefOf.RawNutritionFactor);
                var rot = resource.GetCompProperties<CompProperties_Rottable>();
                // Conditional fresh-output shelf life at this observed destination temperature; future weather is unassessed.
                Fact<double> fresh = rot == null ? Fact.Known((double)RimGovernor.Brain.Domain.GameTime.TicksPerDay) : default;
                var temperature = new IntVec3(location.X, 0, location.Z).GetTemperature(map);
                if (rot != null) fresh = Fact.Known<double>(GenTemperature.RotRateAtTemperature(temperature) > 0
                    ? rot.daysToRotStart * RimGovernor.Brain.Domain.GameTime.TicksPerDay / GenTemperature.RotRateAtTemperature(temperature) : RimGovernor.Brain.Domain.GameTime.TicksPerDay);
                uses.Add(new(pawn.GetUniqueLoadID(), resource.defName, location,
                    Fact.Known(pawn.WillEat(resource) && pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn)?.filter.Allows(resource) != false),
                    Fact.Known((double)nutrition / pawn.needs.food.MaxLevel),
                    Fact.Known(resource.ingestible.baseIngestTicks / (double)(resource.ingestible.useEatingSpeedStat ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1)),
                    resource.ingestible.maxNumToIngestAtOnce, fresh));
            }
        }
    }
}
