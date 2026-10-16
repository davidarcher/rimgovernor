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
    private const int SiteCap = 16, PawnCap = 16, SourceCap = 16, PositionCap = 128, ReportCap = 32, RowBudget = 128;

    [Tool("rimgovernor/construction_activity", Description = "Read bounded observed and planned site demand, exact material sources, eligible suppliers/builders, native route/work estimates and planned-site legality. No orders.")]
    public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "ConstructionActivityRequest JSON.")] object? request = null)
    {
        if (!ProtoBoundary.TryParse<Obs.ConstructionActivityRequest>(ctx, "rimgovernor/construction_activity", request!, out var parsed, out _))
            return ProtoBoundary.Encode(new Obs.ConstructionActivityRead());
        return ProtoBoundary.Encode(await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed), ct).ConfigureAwait(false));
    }

    // One read's row budget, truncation and findings. Placed and planned sites each get their own,
    // so a full placed census cannot erase every planned estimate.
    private sealed class Budget
    {
        internal int Rows = RowBudget;
        internal bool Truncated;
        internal readonly HashSet<string> Findings = new(StringComparer.Ordinal);
        internal bool Take() { if (--Rows >= 0) return true; Truncated = true; return false; }
    }

    // A site the rows estimate. Placed: a blueprint or frame (Thing set). Planned: unplaced cells, where a route
    // from an origin ends at the piece nearest it (Target).
    private sealed record Site(string Id, BuildableDef Definition, IntVec3 Location, Func<IntVec3, IntVec3> Target,
        Thing? Thing, (ThingDef Def, int Count)[] Owed, float Work, Func<Pawn, bool> Eligible, Func<Pawn, bool> Finishes);

    internal static Obs.ConstructionActivityRead Read(Obs.ConstructionActivityRequest request)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        var options = new List<Obs.ConstructionActivityOption>();
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var placed = new Budget { Truncated = request.Buildings.Count > SiteCap || roster.Length > PawnCap };
        var finishGiver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_ConstructFinishFrames);
        foreach (var building in request.Buildings.Take(SiteCap))
        {
            var thing = map.thingGrid.ThingsListAt(new(building.Cell.X, 0, building.Cell.Z))
                .FirstOrDefault(t => t is IConstructible && t.Faction == Faction.OfPlayer && t.def.entityDefToBuild?.defName == building.Definition);
            if (thing is not IConstructible constructible || thing.def.entityDefToBuild is not ThingDef definition) continue;
            var owed = constructible.TotalMaterialCost().Select(c => (c.thingDef, Count: constructible.ThingCountNeeded(c.thingDef)))
                .Where(c => c.Count > 0).ToArray();
            var floorKnown = !ConstructionSkillGuard.Quality(thing) || ConstructionSkillGuard.Setting(thing)?.Minimum >= 0;
            var work = thing is Frame frame ? frame.WorkLeft : definition.GetStatValueAbstract(StatDefOf.WorkToBuild, thing.Stuff);
            Rows(map, roster, new Site(thing.GetUniqueLoadID(), definition, thing.Position, _ => thing.Position, thing, owed, work,
                p => !thing.IsForbidden(p), p => floorKnown && ConstructionSkillGuard.Allows(thing, p)), finishGiver, placed, options);
        }
        var planned = new Budget { Truncated = request.Planned.Count > SiteCap || roster.Length > PawnCap };
        var answers = request.Planned.Take(SiteCap).Select(s => Planned(map, roster, s, finishGiver, planned)).ToArray();
        return new() { Context = context, Known = true, Truncated = placed.Truncated || planned.Truncated, Options = EquatableArray.From(options),
            Planned = EquatableArray.From(answers),
            Findings = EquatableArray.From(placed.Findings.Concat(planned.Findings).Distinct().OrderBy(s => s, StringComparer.Ordinal)) };
    }

    // A hypothetical site: native blueprint legality for every piece, then the placed-site rows against its cells.
    // Anything native cannot resolve leaves the site unknown (no verdict, no rows), never a default.
    private static Obs.PlannedSiteAnswer Planned(Map map, Pawn[] roster, Obs.PlannedConstructionSite request, WorkGiverDef? finishGiver, Budget budget)
    {
        var unknown = new Obs.PlannedSiteAnswer(request.Id, default);
        if (request.Positions.Count == 0) { budget.Findings.Add("planned_site_empty"); return unknown; }
        if (request.Positions.Count > PositionCap) { budget.Findings.Add("planned_site_positions_exceeded"); budget.Truncated = true; return unknown; }
        if (!PlacementPreviewOperation.TryResolveBuildable(request.Definition, out var definition, out _)
            || !definition.BuildableByPlayer || definition.blueprintDef == null) { budget.Findings.Add("planned_site_definition_unresolved"); return unknown; }
        ThingDef? stuff = null;
        if (definition.MadeFromStuff)
        {
            stuff = string.IsNullOrEmpty(request.Stuff) ? GenStuff.DefaultStuffFor(definition) : PlacementPreviewOperation.ResolveThingDef(request.Stuff);
            if (stuff?.stuffProps == null || !stuff.IsStuff || !stuff.stuffProps.CanMake(definition)) { budget.Findings.Add("planned_site_stuff_unresolved"); return unknown; }
        }
        else if (!string.IsNullOrEmpty(request.Stuff)) { budget.Findings.Add("planned_site_stuff_unresolved"); return unknown; }
        var cells = request.Positions.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
        if (cells.Any(c => !c.InBounds(map))) { budget.Findings.Add("planned_site_out_of_bounds"); return unknown; }
        var costs = PlacementPreviewOperation.ReadCosts(definition, stuff);
        if (costs == null) { budget.Findings.Add("planned_site_costs_unreadable"); return unknown; }
        var rotation = new Rot4((int)request.Rotation);
        var research = definition.IsResearchFinished;
        var rejections = new List<Obs.PlannedSiteRejection>();
        var blockers = new List<Obs.PlannedSiteBlocker>();
        var seen = new HashSet<int>();
        var accepted = research;
        foreach (var cell in cells)
        {
            var row = PlacementPreviewOperation.EvaluateRotation(map, definition, definition.blueprintDef, cell, rotation, stuff, godMode: false);
            accepted &= row.accepted;
            var reason = !research ? "Required research for this definition is not finished." : row.accepted ? null : row.reason;
            if (reason != null && Report(rejections.Count, budget)) rejections.Add(new(new(cell.x, cell.z), reason));
            foreach (var blocker in row.blockingThings.Where(b => seen.Add(b.Thing.thingIDNumber)))
                if (Report(blockers.Count, budget))
                    blockers.Add(new(new(blocker.Thing.Position.x, blocker.Thing.Position.z), blocker.Thing.GetUniqueLoadID(), blocker.Thing.def.defName, blocker.category));
        }
        var owed = costs.Where(c => c.count > 0).Select(c => (c.Definition, Count: c.count * cells.Length)).ToArray();
        // No finishing minimum is stored before placement: gate on the native prerequisite alone and say so.
        if (definition is ThingDef built && built.HasComp(typeof(CompQuality))) budget.Findings.Add("planned_quality_minimum_unset");
        var work = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) * cells.Length;
        IntVec3 Nearest(IntVec3 origin) => cells.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        var options = new List<Obs.ConstructionActivityOption>();
        Rows(map, roster, new Site(request.Id, definition, cells[0], Nearest, null, owed, work, _ => true, _ => true), finishGiver, budget, options);
        return new(request.Id, Fact.Known(accepted))
            { Rejections = EquatableArray.From(rejections), Blockers = EquatableArray.From(blockers), Options = EquatableArray.From(options) };
    }

    private static bool Report(int count, Budget budget)
    {
        if (count < ReportCap) return true;
        budget.Truncated = true;
        return false;
    }

    // Per pawn x whole-stack material source: site approach, first approach, delivery trips and finish work.
    private static void Rows(Map map, Pawn[] roster, Site site, WorkGiverDef? finishGiver, Budget budget, List<Obs.ConstructionActivityOption> options)
    {
        // Mixed-material sites need a complete multi-input observation; never pretend one material completes them.
        if (site.Owed.Length > 1) { budget.Findings.Add("mixed_material_demand_unsupported"); return; }
        var sources = site.Owed.Length == 0 ? new Thing?[] { null }
            : map.listerThings.ThingsOfDef(site.Owed[0].Def).Where(t => t.Spawned && t.stackCount >= site.Owed[0].Count)
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(SourceCap + 1).Cast<Thing?>().ToArray();
        if (sources.Length > SourceCap) budget.Truncated = true;
        if (sources.Length == 0) budget.Findings.Add("whole_source_stack_unavailable");
        LocalTargetInfo To(IntVec3 origin) => site.Thing != null ? new LocalTargetInfo(site.Thing) : new LocalTargetInfo(site.Target(origin));
        foreach (var pawn in roster.Take(PawnCap))
        {
            if (pawn.Drafted || pawn.Downed || pawn.InMentalState || !NativeFoodActivity.Available(pawn)
                || NativePawnActivities.Owned(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0
                || pawn.carryTracker.CarriedThing != null || !site.Eligible(pawn)) continue;
            if (!budget.Take()) break;
            var parms = TraverseParms.For(pawn, Danger.None);
            using var siteRoute = map.pathFinder.FindPathNow(pawn.Position, To(pawn.Position), parms, peMode: PathEndMode.Touch);
            if (!siteRoute.Found) continue;
            var finish = finishGiver != null && ConstructionSkillGuard.Capability(pawn) == true && site.Finishes(pawn)
                && (pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= site.Definition.constructionSkillPrerequisite;
            var speed = pawn.GetStatValue(StatDefOf.ConstructionSpeed);
            Fact<double> finishTicks = finish && speed > 0 ? Fact.Known((double)site.Work / speed) : default;
            foreach (var source in sources.Take(SourceCap))
            {
                if (!budget.Take()) break;
                var count = site.Owed.Length == 0 ? 0 : site.Owed[0].Count;
                var capacity = source == null ? 0 : pawn.carryTracker.MaxStackSpaceEver(source.def);
                Fact<double> approach = default, delivery = default;
                if (source != null && capacity > 0 && !source.IsForbidden(pawn)
                    && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                    && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    && pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None))
                {
                    using var first = map.pathFinder.FindPathNow(pawn.Position, source, parms, peMode: PathEndMode.Touch);
                    using var loaded = map.pathFinder.FindPathNow(source.Position, To(source.Position), parms, peMode: PathEndMode.Touch);
                    using var returning = map.pathFinder.FindPathNow(site.Target(source.Position), source, parms, peMode: PathEndMode.Touch);
                    if (first.Found && loaded.Found && returning.Found)
                    {
                        approach = Fact.Known((double)first.TotalCost);
                        var trips = (count + capacity - 1) / capacity;
                        delivery = Fact.Known((double)loaded.TotalCost * trips + returning.TotalCost * (trips - 1));
                    }
                }
                options.Add(new(site.Id, site.Definition.defName, new(site.Location.x, site.Location.z), pawn.GetUniqueLoadID(),
                    source?.def.defName ?? "", source?.GetUniqueLoadID() ?? "", new(source?.Position.x ?? site.Location.x, source?.Position.z ?? site.Location.z),
                    count, capacity, finishGiver?.defName ?? "", approach, delivery, finishTicks) { SiteApproachTicks = Fact.Known((double)siteRoute.TotalCost), AvailableInput = Fact.Known((double)(source?.stackCount ?? 0)) });
            }
        }
    }
}
