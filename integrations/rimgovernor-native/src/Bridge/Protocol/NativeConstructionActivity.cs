#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

public sealed class NativeConstructionActivity
{
    [Tool("rimgovernor/construction_activity", Description = "Read bounded observed site demand, exact material sources, eligible suppliers/builders and native route/work estimates. No orders.")]
    public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "ConstructionActivityRequest JSON.")] object? request = null)
    {
        if (!ProtoBoundary.TryParse<Obs.ConstructionActivityRequest>(ctx, "rimgovernor/construction_activity", request!, out var parsed, out _))
            return ProtoBoundary.Encode(new Obs.ConstructionActivityRead());
        return ProtoBoundary.Encode(await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed), ct).ConfigureAwait(false));
    }
    internal static Obs.ConstructionActivityRead Read(Obs.ConstructionActivityRequest request)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        var options = new List<Obs.ConstructionActivityOption>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var budget = 128; var truncated = request.Buildings.Count > 16 || roster.Length > 16;
        var finishGiver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_ConstructFinishFrames);
        foreach (var building in request.Buildings.Take(16))
        {
            var site = map.thingGrid.ThingsListAt(new(building.Cell.X, 0, building.Cell.Z))
                .FirstOrDefault(t => t is IConstructible && t.Faction == Faction.OfPlayer && t.def.entityDefToBuild?.defName == building.Definition);
            if (site is not IConstructible constructible || site.def.entityDefToBuild is not ThingDef definition) continue;
            var owed = constructible.TotalMaterialCost().Select(c => (c.thingDef, Count: constructible.ThingCountNeeded(c.thingDef)))
                .Where(c => c.Count > 0).ToArray();
            // Mixed-material sites need a complete multi-input observation; never pretend one material completes them.
            if (owed.Length > 1) { findings.Add("mixed_material_demand_unsupported"); continue; }
            var sources = owed.Length == 0 ? new Thing?[] { null }
                : map.listerThings.ThingsOfDef(owed[0].thingDef).Where(t => t.Spawned && t.stackCount >= owed[0].Count)
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).Cast<Thing?>().ToArray();
            if (sources.Length > 16) truncated = true;
            if (sources.Length == 0) findings.Add("whole_source_stack_unavailable");
            foreach (var pawn in roster.Take(16))
            {
                if (pawn.Drafted || pawn.Downed || pawn.InMentalState || !NativeFoodActivity.Available(pawn)
                    || NativePawnActivities.Owned(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0
                    || pawn.carryTracker.CarriedThing != null || site.IsForbidden(pawn)) continue;
                if (--budget < 0) { truncated = true; break; }
                using var siteRoute = map.pathFinder.FindPathNow(pawn.Position, site, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                if (!siteRoute.Found) continue;
                var floorKnown = !ConstructionSkillGuard.Quality(site) || ConstructionSkillGuard.Setting(site)?.Minimum >= 0;
                var finish = finishGiver != null && floorKnown && ConstructionSkillGuard.Capability(pawn) == true
                    && ConstructionSkillGuard.Allows(site, pawn)
                    && (pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= definition.constructionSkillPrerequisite;
                var speed = pawn.GetStatValue(StatDefOf.ConstructionSpeed);
                var work = site is Frame frame ? frame.WorkLeft : definition.GetStatValueAbstract(StatDefOf.WorkToBuild, site.Stuff);
                Fact<double> finishTicks = finish && speed > 0 ? Fact.Known((double)work / speed) : default;
                foreach (var source in sources.Take(16))
                {
                    if (--budget < 0) { truncated = true; break; }
                    var count = owed.Length == 0 ? 0 : owed[0].Count;
                    var capacity = source == null ? 0 : pawn.carryTracker.MaxStackSpaceEver(source.def);
                    Fact<double> approach = default, delivery = default;
                    if (source != null && capacity > 0 && !source.IsForbidden(pawn)
                        && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                        && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                        && pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None))
                    {
                        using var first = map.pathFinder.FindPathNow(pawn.Position, source, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                        using var loaded = map.pathFinder.FindPathNow(source.Position, site, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                        using var returning = map.pathFinder.FindPathNow(site.Position, source, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                        if (first.Found && loaded.Found && returning.Found)
                        {
                            approach = Fact.Known((double)first.TotalCost);
                            var trips = (count + capacity - 1) / capacity;
                            delivery = Fact.Known((double)loaded.TotalCost * trips + returning.TotalCost * (trips - 1));
                        }
                    }
                    options.Add(new(site.GetUniqueLoadID(), definition.defName, new(site.Position.x, site.Position.z), pawn.GetUniqueLoadID(),
                        source?.def.defName ?? "", source?.GetUniqueLoadID() ?? "", new(source?.Position.x ?? site.Position.x, source?.Position.z ?? site.Position.z),
                        count, capacity, finishGiver?.defName ?? "", approach, delivery, finishTicks) { SiteApproachTicks = Fact.Known((double)siteRoute.TotalCost), AvailableInput = Fact.Known((double)(source?.stackCount ?? 0)) });
                }
            }
        }
        return new() { Context = context, Known = true, Truncated = truncated, Options = EquatableArray.From(options), Findings = EquatableArray.From(findings.OrderBy(s => s, StringComparer.Ordinal)) };
    }
}
