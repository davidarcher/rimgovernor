using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native;

internal static class NativeActivityTasks
{
    internal static RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead Read(RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request)
    {
        if (!GameThread.ValidateIdentity(request.Identity, out var map, out var context, out var error)) throw NativeFaults.Refused(error);
        request.Batch.Validate();
        try { return Read(map, context, request); }
        catch (Exception readError)
        {
            ModLog.Error("observe", "Activity task read failed: " + readError);
            throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                Detail = PlacementPreviewOperation.Diagnostic("Activity tasks could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
        }
    }

    // A bad identity is refused and a failed read unavailable (#3775); every binding names its kind's fact (ActivityTaskBinding).
    private static RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead Read(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context, RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request)
    {
        var participants = new List<RimGovernor.Brain.GameFacts.Observations.ActivityTaskObservation>();
        var bindings = new List<RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding>();
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
                    var source = task.Source;
                    var carried = pawn.carryTracker.CarriedThing;
                    var dropped = NativePawnActivities.Dropped(pawn);
                    var candidates = carried is not null ? new[] { carried }
                        : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                            .Where(t => t.Spawned && t.def.defName == task.Resource
                                && (t.Position.DistanceToSquared(new(source.X, 0, source.Z)) <= 4
                                    || order.Steps.Any(s => s.Target == t.GetUniqueLoadID()) || dropped.Length != 0 && t.GetUniqueLoadID() == dropped))
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
                            // A full load (#3046) keeps its collected stacks, less the one it now starts from.
                            bound = bound with { Container = site.GetUniqueLoadID(), Count = count,
                                Collect = EquatableArray.From(step.Collect.Where(id => id != bound.Target)) };
                            if (count <= 0 || !NativePawnActivities.SupplyAllowed(pawn, item, bound)) continue;
                        }
                        else
                        {
                            var destination = ((CellActivityStep)step).Destination;
                            if (!(step.SupportDemand.Length != 0 ? NativeSupportActivities.DestinationAllowed(pawn, item, new(destination.X, 0, destination.Z), count)
                                : JobDriver_GovernorDeliver.DestinationAvailable(pawn, new(destination.X, 0, destination.Z), item, count))
                                || !pawn.CanReserveAndReach(new IntVec3(destination.X, 0, destination.Z), PathEndMode.OnCell, Danger.None)) continue;
                        }
                        bindings.Add(step.Kind == PawnActivityKind.Supply && site is IConstructible current
                            ? new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Supply(task.Id, bound, current.ThingCountNeeded(item.def)) : new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, bound));
                    }
                }
                else if (step.Kind is PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation)
                {
                    // Future output IDs are not Things. Rebind only the selected resource at
                    // its selected use location; observed inputs retain their exact identity.
                    var targets = (step.Kind == PawnActivityKind.Eat
                            ? map.listerThings.AllThings.Where(t => t.Spawned
                                && t.def.defName == task.Resource && t.Position.DistanceToSquared(new(task.Location.X, 0, task.Location.Z)) <= 4
                                && (!task.Inputs.Any(i => i.Observed) || task.Inputs.Any(i => i.Observed && i.Lot == t.GetUniqueLoadID())))
                            : RefIndex.Thing(map, step.Target) is { } bound ? new[] { bound } : Enumerable.Empty<Thing>())
                        .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
                    if (targets.Length > 16) truncated = true;
                    foreach (var target in targets.Take(16))
                    {
                        if (--budget < 0) { truncated = true; break; }
                        // Reserve with the claim the start-time job makes (#3570): a multi-seat building, bed or stack
                        // another pawn holds under the same maxPawns stays bindable while its capacity lasts.
                        var (maxPawns, stackCount) = NativeNeedsForecast.ServiceReservation(step.Kind, target, step.Count);
                        if (target.IsForbidden(pawn) || target.IsBurning()
                            || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None, maxPawns, stackCount)) continue;
                        if (step.Kind == PawnActivityKind.Eat && (pawn.needs.food == null || !target.IngestibleNow
                            || target.stackCount < step.Count || !pawn.WillEat(target) || !FoodSupplyFacts.PolicyAllows(pawn, target))) continue;
                        if (step.Kind == PawnActivityKind.Sleep && (pawn.needs.rest == null || !RestUtility.CanUseBedNow(target, pawn, true))) continue;
                        if (step.Kind == PawnActivityKind.Recreation && (NativeRecreation.GiverFor(target) is not { } joyGiver
                            || !NativeRecreation.Usable(pawn, target, joyGiver))) continue;
                        bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step with { Target = target.GetUniqueLoadID() }));
                    }
                }
                else if (step.Practice != null)
                {
                    var target = RefIndex.Thing(map, step.Target);
                    if (target != null && NativePracticeActivities.Legal(pawn, target, step)) bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step));
                }
                else if (step.Kind == PawnActivityKind.Work && step.Job == "BuildRoof" && step is CellActivityStep { Destination: var destination })
                {
                    var cell = new IntVec3(destination.X, 0, destination.Z);
                    if (!cell.InBounds(map)) continue;
                    if (map.roofGrid.Roofed(cell)) { satisfied.Add(task.Id); continue; }
                    if (map.areaManager.BuildRoof[cell] && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                        && DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is WorkGiver_BuildRoof giver
                        && giver.HasJobOnCell(pawn, cell, false) && pawn.CanReach(cell, PathEndMode.Touch, Danger.None))
                        bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step));
                }
                else if (step.Kind == PawnActivityKind.Work && step.Job == "Sow")
                {
                    // A fully sown grower is done; otherwise the nearest cell this pawn can sow now.
                    if (NativeSowingActivities.FullySown(map, step.Target)) { satisfied.Add(task.Id); continue; }
                    if (NativeSowingActivities.Giver?.defName == step.WorkGiver && NativeSowingActivities.Grower(map, step.Target) is { } grower
                        && NativeSowingActivities.LegalCells(pawn, grower) is [var nearest, ..])
                        bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step.At(new(nearest.x, nearest.z))));
                }
                else if (step.Kind == PawnActivityKind.Work && step.IsSet)
                {
                    // A set step (#3613, cells #3615): every member gone is done; else the members this pawn can still work, nearest first.
                    if (!NativeSetActivities.Standing(pawn, step)) { satisfied.Add(task.Id); continue; }
                    if (DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is not WorkGiver_Scanner setGiver
                        || !NativeSetActivities.Supported(setGiver.def, step.Job) || setGiver.MissingRequiredCapacity(pawn) != null
                        || pawn.WorkTagIsDisabled(setGiver.def.workTags) || setGiver.def.workType != null && pawn.WorkTypeIsDisabled(setGiver.def.workType)) continue;
                    var legal = NativeSetActivities.Legal(pawn, step, setGiver);
                    var legalCells = NativeSetActivities.LegalCells(pawn, step, setGiver);
                    if (legal.Count + legalCells.Count == 0) continue;
                    bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step with { Targets = EquatableArray.From(legal.Select(m => m.GetUniqueLoadID())),
                        Cells = EquatableArray.From(legalCells.Select(c => new Cell(c.x, c.z))), Count = legal.Count + legalCells.Count }));
                }
                else if (step.Kind == PawnActivityKind.Work)
                {
                    var target = task.SiteDefinition.Length == 0
                        ? RefIndex.Thing(map, step.Target)
                        : map.thingGrid.ThingsListAt(new(task.Location.X, 0, task.Location.Z))
                            .FirstOrDefault(t => t is Frame && t.def.entityDefToBuild?.defName == task.SiteDefinition);
                    if (step.Job == "TendPatient" && target is Pawn patient && !patient.health.HasHediffsNeedingTend()) { satisfied.Add(task.Id); continue; }
                    if (target == null || target.IsForbidden(pawn) || !pawn.CanReserveAndReach(target, PathEndMode.Touch, Danger.None)) continue;
                    if (DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver)?.Worker is not WorkGiver_Scanner giver
                        || giver.MissingRequiredCapacity(pawn) != null || pawn.WorkTagIsDisabled(giver.def.workTags)
                        || giver.def.workType != null && pawn.WorkTypeIsDisabled(giver.def.workType)) continue;
                    if (step.Job == "DoBill")
                    {
                        if (target is not Pawn surgeryPatient || !NativeSupportSurgery.Resolve(pawn, surgeryPatient, step, out var surgery)) continue;
                        JobMaker.ReturnToPool(surgery);
                    }
                    else if (giver is WorkGiver_GrowerHarvest ? !giver.HasJobOnCell(pawn, target.Position, false) : !giver.HasJobOnThing(pawn, target, false)) continue;
                    bindings.Add(new RimGovernor.Brain.GameFacts.Observations.ActivityTaskBinding.Bound(task.Id, step with { Target = target.GetUniqueLoadID() }));
                }
            }
        }
        return new() { Context = context, Truncated = truncated,
            // Every continuation reads its participants' needs: the boundary between tasks hands no work to a pawn at its rest or food threshold.
            Needs = NativeNeedsForecast.Read(map, map.mapPawns.FreeColonistsSpawned.Where(p => request.Batch.Orders.Any(o => o.Pawn == p.GetUniqueLoadID())).ToList()),
            Pawns = EquatableArray.From(participants), Bindings = EquatableArray.From(bindings), SatisfiedTasks = EquatableArray.From(satisfied) };
    }
}
