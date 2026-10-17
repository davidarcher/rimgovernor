#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeActivityTasks
{
    internal static Obs.ActivityTaskRead Read(Obs.ActivityTaskRequest request)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        request.Batch.Validate();
        var participants = new List<Obs.ActivityTaskObservation>();
        var bindings = new List<Obs.ActivityTaskBinding>();
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        var budget = 128;
        var truncated = false;
        foreach (var order in request.Batch.Orders)
        {
            var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == order.Pawn);
            if (pawn == null) continue; // Missing participant is unknown, never completion.
            participants.Add(new(order.Pawn, NativePawnActivities.Read(pawn)));
            if (pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled) continue;
            foreach (var task in request.Batch.Tasks.Where(t => t.Pawn == order.Pawn))
            {
                if (--budget < 0) { truncated = true; break; }
                var step = task.Step;
                if (step.Kind == PawnActivityKind.Deliver && step.SupportDemand.Length != 0 && DefDatabase<ThingDef>.GetNamedSilentFail(task.Resource) is { } supportResource
                    && NativeSupportActivities.Remaining(map, step, supportResource) == 0)
                { satisfied.Add(task.Id); continue; }
                var site = task.SiteDefinition.Length == 0 ? null : map.thingGrid.ThingsListAt(new(task.Location.X, 0, task.Location.Z))
                    .FirstOrDefault(t => t is IConstructible && t.def.entityDefToBuild?.defName == task.SiteDefinition);
                if (task.SiteDefinition.Length != 0 && (map.thingGrid.ThingsListAt(new(task.Location.X, 0, task.Location.Z))
                    .Any(t => t is Verse.Building && t.def.defName == task.SiteDefinition && t.Faction == Faction.OfPlayer)
                    || step.Kind == PawnActivityKind.Supply && site is IConstructible observedSite
                        && DefDatabase<ThingDef>.GetNamedSilentFail(task.Resource) is { } material && observedSite.ThingCountNeeded(material) == 0))
                { satisfied.Add(task.Id); continue; }
                if (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply)
                {
                    var source = task.Source ?? task.Location;
                    var carried = pawn.carryTracker.CarriedThing;
                    var candidates = carried is not null ? new[] { carried }
                        : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                            .Where(t => t.Spawned && t.def.defName == task.Resource
                                && (t.Position.DistanceToSquared(new(source.X, 0, source.Z)) <= 4
                                    || order.Steps.Any(s => s.Target == t.GetUniqueLoadID())))
                            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
                    if (candidates.Length > 16) truncated = true;
                    foreach (var item in candidates.Take(16))
                    {
                        if (--budget < 0) { truncated = true; break; }
                        if (item.def.defName != task.Resource || task.ProductStuff.Length != 0 && item.Stuff?.defName != task.ProductStuff
                            || item.Destroyed || item.IsForbidden(pawn)
                            || carried == null && !pawn.CanReserveAndReach(item, PathEndMode.Touch, Danger.None)) continue;
                        var count = Math.Min(task.Step.Count, Math.Min(item.stackCount, pawn.carryTracker.MaxStackSpaceEver(item.def)));
                        if (step.SupportDemand.Length != 0) count = Math.Min(count, NativeSupportActivities.Remaining(map, step, item.def));
                        if (count <= 0 || carried != null && count != carried.stackCount) continue;
                        var bound = step with { Target = item.GetUniqueLoadID(), Count = count };
                        if (step.Kind == PawnActivityKind.Supply)
                        {
                            if (site is not IConstructible constructible) continue;
                            count = Math.Min(count, constructible.ThingCountNeeded(item.def));
                            bound = bound with { Container = site.GetUniqueLoadID(), Count = count };
                            if (count <= 0 || !NativePawnActivities.SupplyAllowed(pawn, item, bound)) continue;
                        }
                        else
                        {
                            var destination = step.Destination!.Value;
                            if (!(step.SupportDemand.Length != 0 ? NativeSupportActivities.DestinationAllowed(pawn, item, new(destination.X, 0, destination.Z), count)
                                : JobDriver_GovernorDeliver.DestinationAvailable(pawn, new(destination.X, 0, destination.Z)))
                                || !pawn.CanReserveAndReach(new IntVec3(destination.X, 0, destination.Z), PathEndMode.OnCell, Danger.None)) continue;
                        }
                        bindings.Add(new(task.Id, bound) { RemainingCount = step.Kind == PawnActivityKind.Supply && site is IConstructible current
                            ? Fact.Known(current.ThingCountNeeded(item.def)) : default });
                    }
                }
                else if (step.Kind is PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation)
                {
                    // Future output IDs are not Things. Rebind only the selected resource at
                    // its selected use location; observed inputs retain their exact identity.
                    var targets = map.listerThings.AllThings.Where(t => t.Spawned
                        && (step.Kind == PawnActivityKind.Eat
                            ? t.def.defName == task.Resource && t.Position.DistanceToSquared(new(task.Location.X, 0, task.Location.Z)) <= 4
                                && (!task.Inputs.Any(i => i.Observed) || task.Inputs.Any(i => i.Observed && i.Lot == t.GetUniqueLoadID()))
                            : t.GetUniqueLoadID() == step.Target))
                        .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
                    if (targets.Length > 16) truncated = true;
                    foreach (var target in targets.Take(16))
                    {
                        if (--budget < 0) { truncated = true; break; }
                        if (target.IsForbidden(pawn) || target.IsBurning()
                            || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None)) continue;
                        if (step.Kind == PawnActivityKind.Eat && (pawn.needs.food == null || !target.IngestibleNow
                            || target.stackCount < step.Count || !pawn.WillEat(target) || !FoodSupplyFacts.PolicyAllows(pawn, target))) continue;
                        if (step.Kind == PawnActivityKind.Sleep && (pawn.needs.rest == null || !RestUtility.CanUseBedNow(target, pawn, true))) continue;
                        if (step.Kind == PawnActivityKind.Recreation && (target.def.defName != "HorseshoesPin" || pawn.needs.joy == null
                            || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) continue;
                        bindings.Add(new(task.Id, step with { Target = target.GetUniqueLoadID() }));
                    }
                }
                else if (step.Practice != null)
                {
                    if (!NativePracticeActivities.Assigned(pawn, step.Practice)) { satisfied.Add(task.Id); continue; }
                    var target = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == step.Target);
                    if (target != null && NativePracticeActivities.Legal(pawn, target, step)) bindings.Add(new(task.Id, step));
                }
                else if (step.Kind == PawnActivityKind.Work && step.Job == "BuildRoof" && step.Destination is { } destination)
                {
                    var cell = new IntVec3(destination.X, 0, destination.Z);
                    if (!cell.InBounds(map)) continue;
                    if (map.roofGrid.Roofed(cell)) { satisfied.Add(task.Id); continue; }
                    if (map.areaManager.BuildRoof[cell] && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                        && DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is WorkGiver_BuildRoof giver
                        && giver.HasJobOnCell(pawn, cell, false) && pawn.CanReach(cell, PathEndMode.Touch, Danger.None))
                        bindings.Add(new(task.Id, step));
                }
                else if (step.Kind == PawnActivityKind.Work)
                {
                    var target = task.SiteDefinition.Length == 0
                        ? map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == step.Target)
                        : map.thingGrid.ThingsListAt(new(task.Location.X, 0, task.Location.Z))
                            .FirstOrDefault(t => t is Frame && t.def.entityDefToBuild?.defName == task.SiteDefinition);
                    if (step.Job == "TendPatient" && target is Pawn patient && !patient.health.HasHediffsNeedingTend()) { satisfied.Add(task.Id); continue; }
                    if (step.Job == "Clean" && target == null) { satisfied.Add(task.Id); continue; }
                    if (target == null || target.IsForbidden(pawn) || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None)) continue;
                    if (DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is not WorkGiver_Scanner giver
                        || giver.MissingRequiredCapacity(pawn) != null || pawn.WorkTagIsDisabled(giver.def.workTags)
                        || giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType)) continue;
                    if (step.Job == "DoBill")
                    {
                        if (target is not Pawn surgeryPatient || !NativeSupportSurgery.Resolve(pawn, surgeryPatient, step, out var surgery)) continue;
                        JobMaker.ReturnToPool(surgery!);
                    }
                    else if (giver is WorkGiver_GrowerHarvest ? !giver.HasJobOnCell(pawn, target.Position, false) : !giver.HasJobOnThing(pawn, target, false)) continue;
                    bindings.Add(new(task.Id, step with { Target = target.GetUniqueLoadID() }));
                }
                else if (step.Kind == PawnActivityKind.Recipe)
                {
                    var bench = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == step.Target);
                    if (bench == null || step.Recipe is not { } recipe || task.Source is not { } input) continue;
                    if (recipe.Stuff.Length != 0)
                    {
                        // A fresh selected task keeps its exact recipe/stuff. Rebind split/merged inputs only;
                        // failed consuming work itself is never replayed by the shared continuation.
                        var left = recipe.Ingredients.Sum(i => i.Count);
                        var reboundInputs = new List<PawnActivityIngredient>();
                        foreach (var item in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                            .Where(t => t.Spawned && t.def.defName == recipe.Resource && !t.IsForbidden(pawn)
                                && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None))
                            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                        {
                            if (left == 0) break;
                            if (reboundInputs.Count == 16) { truncated = true; break; }
                            var take = Math.Min(left, item.stackCount); reboundInputs.Add(new(item.GetUniqueLoadID(), take)); left -= take;
                        }
                        var rebound = step with { Recipe = recipe with { Ingredients = EquatableArray.From(reboundInputs) } };
                        if (left == 0 && NativeActivityRecipe.Resolve(pawn, bench, rebound, out _, out _, out _)) bindings.Add(new(task.Id, rebound));
                        continue;
                    }
                    var count = recipe.Ingredients.Sum(i => i.Count);
                    var ingredients = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                        .Where(t => t.Spawned && t.def.defName == task.Resource && !t.IsForbidden(pawn) && !t.IsBurning()
                            && pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some)
                            && t.Position.DistanceToSquared(new(input.X, 0, input.Z)) <= 4)
                        .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
                    if (ingredients.Length > 16) truncated = true;
                    var remainingIngredients = count;
                    var exactIngredients = new List<PawnActivityIngredient>();
                    foreach (var ingredient in ingredients.Take(16))
                    {
                        var take = Math.Min(remainingIngredients, ingredient.stackCount);
                        if (take <= 0) break;
                        exactIngredients.Add(new(ingredient.GetUniqueLoadID(), take)); remainingIngredients -= take;
                    }
                    var bound = step with { Recipe = recipe with { Ingredients = EquatableArray.From(exactIngredients) } };
                    if (remainingIngredients == 0 && NativeActivityRecipe.Resolve(pawn, bench, bound, out _, out _, out _))
                    {
                        var productive = task.ProductiveWorkTicks.IsKnown && bench is Building_WorkTable table
                            && DefDatabase<RecipeDef>.GetNamedSilentFail(recipe.Recipe) is { } definition
                            ? NativeProductiveSkill.Read(pawn, definition, table) : null;
                        var rate = productive?.Rates.SingleOrDefault(r => productive.Learning.StoredLevel == Fact.Known(r.StoredLevel))?.WorkPerTick ?? default;
                        bindings.Add(new(task.Id, bound) { ProductiveWorkTicks = productive?.WorkAmount.IsKnown == true && rate.IsKnown && rate.Value > 0
                            ? Fact.Known(Math.Ceiling(productive.WorkAmount.Value / rate.Value)) : default });
                    }
                }
            }
        }
        Obs.FoodActivityRead? food = null;
        var pendingFood = request.Batch.Tasks.FirstOrDefault(t => t.Step.Kind == PawnActivityKind.Deliver && t.Source != null && t.Step.Destination != null);
        if (pendingFood != null && request.Batch.Tasks.Any(t => t.Step.Job == "Harvest"))
        {
            var output = new ActivityOutputDelivery(pendingFood.Resource, pendingFood.Source!.Value,
                pendingFood.Step.Destination!.Value, pendingFood.Step.Count, pendingFood.Step.Deadline, pendingFood.AttemptsLeft);
            var reads = request.Batch.Orders.Select(o => NativeFoodActivity.Read(new(request.Identity, [], o with { OutputDelivery = output }) { AllowCooking = true })).ToArray();
            food = new() { Context = context, Known = reads.All(r => r.Known), Truncated = reads.Any(r => r.Truncated),
                Options = EquatableArray.From(reads.SelectMany(r => r.Options)) };
        }
        return new() { Context = context, Known = true, Truncated = truncated, Food = food,
            Practice = EquatableArray.From(request.Batch.Tasks.Where(t => t.Step.Practice != null).Select(t => NativePracticeActivities.Mandate(map, t))),
            // Every continuation reads its participants' needs: the boundary between tasks hands no work to a pawn at its rest or food threshold.
            Needs = NativeNeedsForecast.Read(map, map.mapPawns.FreeColonistsSpawned.Where(p => request.Batch.Orders.Any(o => o.Pawn == p.GetUniqueLoadID())).ToList()),
            Pawns = EquatableArray.From(participants), Bindings = EquatableArray.From(bindings), SatisfiedTasks = EquatableArray.From(satisfied) };
    }
}
