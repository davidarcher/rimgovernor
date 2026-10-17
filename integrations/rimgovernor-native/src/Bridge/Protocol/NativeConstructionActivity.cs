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
    private const int SiteCap = 16, PawnCap = 16, SourceCap = 16, PositionCap = 128, ReportCap = 32, RowBudget = 128, RouteBudget = SiteCap * PawnCap;

    [Tool("rimgovernor/construction_activity", Description = "Read bounded observed and planned site demand, exact material sources, eligible suppliers/builders, native route/work estimates, planned-site legality and routes between planned sites. No orders.")]
    public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "ConstructionActivityRequest JSON.")] object? request = null)
    {
        if (!ProtoBoundary.TryParse<Obs.ConstructionActivityRequest>(ctx, "rimgovernor/construction_activity", request!, out var parsed, out _))
            return ProtoBoundary.Encode(new Obs.ConstructionActivityRead());
        return ProtoBoundary.Encode(await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed), ct).ConfigureAwait(false));
    }

    // One read's row budget, truncation and findings. Placed and planned sites each get their own,
    // so a full placed census cannot erase every planned estimate. SiteTruncated marks a cut since the
    // caller last cleared it: the planned site being answered (#3312).
    private sealed class Budget
    {
        internal int Rows = RowBudget;
        internal bool Truncated, SiteTruncated;
        internal readonly HashSet<string> Findings = new(StringComparer.Ordinal);
        internal void Cut() => Truncated = SiteTruncated = true;
        internal bool Take() { if (--Rows >= 0) return true; Cut(); return false; }
    }

    // A site the rows estimate. Placed: a blueprint or frame (Thing set). Planned: unplaced cells, where a route
    // from an origin ends at the piece nearest it (Target).
    private sealed record Site(string Id, BuildableDef Definition, IntVec3 Location, Func<IntVec3, IntVec3> Target,
        Thing? Thing, (ThingDef Def, int Count)[] Owed, float Work, Func<Pawn, bool> Eligible, Func<Pawn, bool> Finishes);

    internal static Obs.ConstructionActivityRead Read(Obs.ConstructionActivityRequest request)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        var options = new List<Obs.ConstructionActivityOption>();
        // The named colonists (#3312), else the map's: a named pawn that is no free spawned colonist has no row.
        var named = new HashSet<string>(request.Pawns, StringComparer.Ordinal);
        var roster = map.mapPawns.FreeColonistsSpawned.Where(p => named.Count == 0 || named.Contains(p.GetUniqueLoadID()))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var pawnsCut = Math.Max(named.Count, roster.Length) > PawnCap;
        var placed = new Budget { Truncated = request.Buildings.Count > SiteCap || pawnsCut };
        if (roster.Length < named.Count) placed.Findings.Add("construction_pawn_absent");
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
        var planned = new Budget { Truncated = request.Planned.Count > SiteCap || pawnsCut };
        var clearing = new Budget();
        // Each answer says whether a cap cut it, so the read's other answers stay usable (#3312).
        var answers = request.Planned.Take(SiteCap).Select(s =>
        {
            planned.SiteTruncated = clearing.SiteTruncated = false;
            var answer = Planned(map, roster, s, finishGiver, planned, clearing);
            return answer with { Truncated = pawnsCut || planned.SiteTruncated || clearing.SiteTruncated };
        }).ToArray();
        var routed = new Budget { Rows = RouteBudget, Truncated = request.Routes.Count > SiteCap || pawnsCut };
        var routes = Routes(map, roster, request.Routes, routed);
        return new() { Context = context, Known = true, Truncated = placed.Truncated || planned.Truncated || clearing.Truncated || routed.Truncated,
            Options = EquatableArray.From(options), Planned = EquatableArray.From(answers), Routes = EquatableArray.From(routes),
            Thermal = request.Thermal is { } thermal ? NativeCoupledThermal.ReadPlanned(map, thermal) : null,
            Findings = EquatableArray.From(placed.Findings.Concat(planned.Findings).Concat(clearing.Findings).Concat(routed.Findings).Distinct().OrderBy(s => s, StringComparer.Ordinal)) };
    }

    // Routes between planned sites (#3313): per probe and free colonist, the native path from the probe's cell (or the
    // pawn's own, FromStart) to its target cell nearest that origin. No path leaves the row's ticks unknown.
    private static List<Obs.CoupledRoute> Routes(Map map, Pawn[] roster, EquatableArray<Obs.PlannedRouteProbe> probes, Budget budget)
    {
        var rows = new List<Obs.CoupledRoute>();
        var pawns = roster.Take(PawnCap).Where(Free).ToArray();
        foreach (var probe in probes.Take(SiteCap))
        {
            var to = new IntVec3(probe.To.X, 0, probe.To.Z);
            var reach = probe.Reach.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            if (!to.InBounds(map) || (!probe.FromStart && !new IntVec3(probe.From.X, 0, probe.From.Z).InBounds(map)) || reach.Any(c => !c.InBounds(map)))
            {
                budget.Findings.Add("planned_route_out_of_bounds");
                continue;
            }
            foreach (var pawn in pawns)
            {
                if (!budget.Take()) return rows;
                var origin = probe.FromStart ? pawn.Position : new IntVec3(probe.From.X, 0, probe.From.Z);
                var target = reach.Length == 0 ? to : reach.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
                using var route = map.pathFinder.FindPathNow(origin, target, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                rows.Add(new(pawn.GetUniqueLoadID(), new(origin.x, origin.z), probe.To, route.Found ? Fact.Known((double)route.TotalCost) : default)
                    { FromStart = probe.FromStart });
            }
        }
        return rows;
    }

    // A hypothetical site: native blueprint legality for every piece, then the placed-site rows against its cells.
    // Anything native cannot resolve leaves the site unknown (no verdict, no rows), never a default.
    private static Obs.PlannedSiteAnswer Planned(Map map, Pawn[] roster, Obs.PlannedConstructionSite request, WorkGiverDef? finishGiver, Budget budget, Budget clearingBudget)
    {
        var unknown = new Obs.PlannedSiteAnswer(request.Id, default);
        if (request.Positions.Count == 0) { budget.Findings.Add("planned_site_empty"); return unknown; }
        if (request.Positions.Count > PositionCap) { budget.Findings.Add("planned_site_positions_exceeded"); budget.Cut(); return unknown; }
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
        // The site's blueprint, never spawned (no thing id): GenConstruct.BlocksConstruction reads only its def.
        var plan = new Blueprint_Build { def = definition.blueprintDef };
        var kinds = new Dictionary<Thing, Clear>();
        Clear Kind(Thing thing) => kinds.TryGetValue(thing, out var kind) ? kind : kinds[thing] = Classify(plan, definition, stuff, thing);
        var things = new List<PlacementBlocker>();
        var footprint = new HashSet<IntVec3>();
        var seen = new HashSet<int>();
        bool accepted = research, illegal = !research, fogged = false;
        foreach (var cell in cells)
        {
            var row = PlacementPreviewOperation.EvaluateRotation(map, definition, definition.blueprintDef, cell, rotation, stuff, godMode: false);
            accepted &= row.accepted;
            footprint.UnionWith(row.Rect.Cells);
            things.AddRange(row.blockingThings.Where(b => seen.Add(b.Thing.thingIDNumber)));
            Obs.PlannedSiteRejection? rejection = null;
            if (!research) rejection = new(new(cell.x, cell.z), "Required research for this definition is not finished.") { Illegal = true };
            else if (!row.accepted)
            {
                // Native legality with every removable footprint thing skipped: what still refuses is not clearing's to fix.
                var cleared = PlacementPreviewOperation.EvaluateRotation(map, definition, definition.blueprintDef, cell, rotation, stuff, godMode: false,
                    skip: t => Kind(t) != Clear.Illegal);
                var hidden = !cleared.accepted && cell.Fogged(map);
                fogged |= hidden;
                illegal |= !cleared.accepted && !hidden;
                rejection = cleared.accepted || hidden ? new(new(cell.x, cell.z), row.reason) : new(new(cell.x, cell.z), cleared.reason) { Illegal = true };
            }
            if (rejection != null && Report(rejections.Count, budget)) rejections.Add(rejection);
        }
        if (fogged) budget.Findings.Add("planned_site_fogged");
        var clearing = Clearing(map, roster, request.Id, definition, things.Select(b => (b.Thing, Kind: Kind(b.Thing))).ToArray(), footprint, clearingBudget,
            out var ops, out var unsure);
        var blockers = new List<Obs.PlannedSiteBlocker>();
        foreach (var blocker in things)
        {
            var thing = blocker.Thing;
            illegal |= Kind(thing) == Clear.Illegal;
            if (!Report(blockers.Count, budget)) continue;
            var verdict = Kind(thing) switch
            {
                Clear.None => Obs.PlannedBlockerVerdict.NoClearing,
                Clear.Unestimable => Obs.PlannedBlockerVerdict.Unestimable,
                Clear.Illegal => Obs.PlannedBlockerVerdict.Illegal,
                _ => ops.ContainsKey(thing) ? Obs.PlannedBlockerVerdict.Cleared : Obs.PlannedBlockerVerdict.Unspecified,
            };
            blockers.Add(new(new(thing.Position.x, thing.Position.z), thing.GetUniqueLoadID(), thing.def.defName, blocker.category)
                { Verdict = verdict, Clearing = ops.TryGetValue(thing, out var op) ? op : "" });
        }
        var siteVerdict = illegal ? Obs.PlannedSiteVerdict.Illegal
            : fogged || things.Any(b => Kind(b.Thing) == Clear.Unestimable) ? Obs.PlannedSiteVerdict.Unestimable
            : unsure ? Obs.PlannedSiteVerdict.Unspecified : Obs.PlannedSiteVerdict.Feasible;
        var owed = costs.Where(c => c.count > 0).Select(c => (c.Definition, Count: c.count * cells.Length)).ToArray();
        // No finishing minimum is stored before placement: gate on the native prerequisite alone and say so.
        if (definition is ThingDef built && built.HasComp(typeof(CompQuality))) budget.Findings.Add("planned_quality_minimum_unset");
        var work = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) * cells.Length;
        IntVec3 Nearest(IntVec3 origin) => cells.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        var options = new List<Obs.ConstructionActivityOption>();
        Rows(map, roster, new Site(request.Id, definition, cells[0], Nearest, null, owed, work, _ => true, _ => true), finishGiver, budget, options);
        return new(request.Id, Fact.Known(accepted))
        {
            Rejections = EquatableArray.From(rejections), Blockers = EquatableArray.From(blockers), Options = EquatableArray.From(options),
            Verdict = siteVerdict, Clearing = EquatableArray.From(clearing)
        };
    }

    // What clearing one footprint thing takes, in GenConstruct.HandleBlockingThingJob's kinds.
    private enum Clear { None, Cut, Haul, Mine, Unestimable, Illegal }

    // JobDriver_Mine.BaseDamagePerPickHit_NaturalRock (private): a pick hit's damage to natural rock.
    private const int NaturalRockDamagePerPickHit = 80;

    private static Clear Classify(Blueprint plan, BuildableDef definition, ThingDef? stuff, Thing thing)
    {
        // Pawns step aside; placing the blueprint wipes what SpawningWipes names; the rest stays unless it blocks.
        if (thing is Pawn || GenSpawn.SpawningWipes(plan.def, thing.def)) return Clear.None;
        if (GenConstruct.CanPlaceBlueprintOver(definition, thing.def, stuff, thing.Stuff) && !GenConstruct.BlocksConstruction(plan, thing)) return Clear.None;
        if (thing.def.category == ThingCategory.Plant) return thing is Plant ? Clear.Cut : Clear.Unestimable;
        if (thing.def.category == ThingCategory.Item) return thing.def.EverHaulable ? Clear.Haul : Clear.Unestimable;
        if (thing is Mineable && thing.def.building?.isNaturalRock == true) return Clear.Mine;
        if (thing is Verse.Building building) return building.DeconstructibleBy(Faction.OfPlayer).Accepted || thing.def.mineable ? Clear.Unestimable : Clear.Illegal;
        return thing.Faction == Faction.OfPlayer ? Clear.Unestimable : Clear.Illegal;
    }

    // Clearing ops: one Cut for the plants, one Mine for the natural rock, one Haul per haulable stack with per-pawn
    // rows. A stack with no accepting storage outside the footprint has no op (unknown). ops maps each cleared thing
    // to its op id.
    private static List<Obs.PlannedSiteClearing> Clearing(Map map, Pawn[] roster, string site, BuildableDef definition,
        (Thing Thing, Clear Kind)[] things, HashSet<IntVec3> footprint, Budget budget, out Dictionary<Thing, string> ops, out bool unknown)
    {
        var result = new List<Obs.PlannedSiteClearing>();
        var cleared = ops = new Dictionary<Thing, string>();
        unknown = false;
        var pawns = roster.Take(PawnCap).Where(Free).ToArray();
        string Giver<T>() => DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is T)?.defName ?? "";
        var plants = things.Where(t => t.Kind == Clear.Cut).Select(t => (Plant)t.Thing).OrderBy(t => t.thingIDNumber).ToArray();
        if (plants.Length > 0)
            result.Add(Work(site + "/cut", Obs.PlannedClearingKind.Cut, plants, Giver<WorkGiver_PlantsCut>(),
                pawn => (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting) || !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                    && plants.All(p => PlantUtility.PawnWillingToCutPlant_Job(p, pawn)),
                pawn => plants.Select(p => (double)JobDriver_PlantWork.WorkDonePerTick(pawn, p)).All(r => r > 0)
                    ? Fact.Known(plants.Sum(p => (double)p.def.plant.harvestWork / JobDriver_PlantWork.WorkDonePerTick(pawn, p))) : default));
        var rock = things.Where(t => t.Kind == Clear.Mine).Select(t => t.Thing).OrderBy(t => t.thingIDNumber).ToArray();
        if (rock.Length > 0)
            result.Add(Work(site + "/mine", Obs.PlannedClearingKind.Mine, rock, Giver<WorkGiver_Miner>(),
                pawn => !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Mining),
                pawn =>
                {
                    // JobDriver_Mine: a hit every round(100 / MiningSpeed) ticks; the hit that finds HP at or under the damage destroys it.
                    var speed = pawn.GetStatValue(StatDefOf.MiningSpeed);
                    return speed > 0 ? Fact.Known(rock.Sum(r => Math.Ceiling(r.HitPoints / (double)NaturalRockDamagePerPickHit))
                        * Math.Round(JobDriver_Mine.BaseTicksBetweenPickHits / speed)) : default;
                }));
        var haulGiver = Giver<WorkGiver_HaulGeneral>();
        foreach (var item in things.Where(t => t.Kind == Clear.Haul).Select(t => t.Thing).OrderBy(t => t.thingIDNumber))
        {
            if (Destination(map, item, footprint) is not { } destination) { unknown = true; budget.Findings.Add("planned_clearing_haul_destination_unknown"); continue; }
            var id = site + "/haul/" + item.GetUniqueLoadID();
            ops[item] = id;
            var rows = new List<Obs.ConstructionActivityOption>();
            foreach (var pawn in pawns)
            {
                var capacity = pawn.carryTracker.MaxStackSpaceEver(item.def);
                if (capacity <= 0 || item.IsForbidden(pawn) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                    || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !pawn.CanReserveAndReach(item, PathEndMode.Touch, Danger.None)) continue;
                if (!budget.Take()) break;
                var parms = TraverseParms.For(pawn, Danger.None);
                using var first = map.pathFinder.FindPathNow(pawn.Position, item, parms, peMode: PathEndMode.Touch);
                using var loaded = map.pathFinder.FindPathNow(item.Position, destination, parms, peMode: PathEndMode.OnCell);
                using var returning = map.pathFinder.FindPathNow(destination, item, parms, peMode: PathEndMode.Touch);
                if (!first.Found || !loaded.Found || !returning.Found) continue;
                var trips = (item.stackCount + capacity - 1) / capacity;
                rows.Add(new(id, definition.defName, new(destination.x, destination.z), pawn.GetUniqueLoadID(), item.def.defName, item.GetUniqueLoadID(),
                    new(item.Position.x, item.Position.z), item.stackCount, capacity, haulGiver, Fact.Known((double)first.TotalCost),
                    Fact.Known((double)loaded.TotalCost * trips + returning.TotalCost * (trips - 1)), Fact.Known(0.0))
                    { SiteApproachTicks = Fact.Known((double)first.TotalCost), AvailableInput = Fact.Known((double)item.stackCount) });
            }
            result.Add(new(id, Obs.PlannedClearingKind.Haul, [new(item.Position.x, item.Position.z)]) { Options = EquatableArray.From(rows) });
        }
        return result;

        // A work op over its things: the route reaches the one nearest the pawn; finish is the summed native work.
        Obs.PlannedSiteClearing Work(string id, Obs.PlannedClearingKind kind, Thing[] targets, string giver, Func<Pawn, bool> able, Func<Pawn, Fact<double>> finish)
        {
            foreach (var target in targets) cleared[target] = id;
            var rows = new List<Obs.ConstructionActivityOption>();
            foreach (var pawn in pawns.Where(able))
            {
                if (!budget.Take()) break;
                var nearest = targets.OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.thingIDNumber).First();
                using var route = map.pathFinder.FindPathNow(pawn.Position, nearest, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.Touch);
                if (!route.Found) continue;
                var cell = new Cell(nearest.Position.x, nearest.Position.z);
                rows.Add(new(id, definition.defName, cell, pawn.GetUniqueLoadID(), "", "", cell, 0, 0, giver, default, default, finish(pawn))
                    { SiteApproachTicks = Fact.Known((double)route.TotalCost) });
            }
            return new(id, kind, EquatableArray.From(targets.Select(t => new Cell(t.Position.x, t.Position.z)).Distinct())) { Options = EquatableArray.From(rows) };
        }
    }

    // The nearest cell of the best-priority player storage that accepts the stack, outside the footprint
    // (StoreUtility.TryFindBestBetterStoreCellFor's order, scanned exactly instead of sampled).
    private static IntVec3? Destination(Map map, Thing item, HashSet<IntVec3> footprint)
    {
        IntVec3? best = null;
        var bestDistance = float.MaxValue;
        var found = StoragePriority.Unstored;
        foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
        {
            var priority = group.Settings.Priority;
            if (priority <= StoragePriority.Unstored || (best != null && priority < found)) break;
            if ((group.parent is Thing parent && parent.Faction != Faction.OfPlayer) || !group.parent.HaulDestinationEnabled || !group.Settings.AllowedToAccept(item)) continue;
            foreach (var cell in group.CellsList)
            {
                var distance = (item.Position - cell).LengthHorizontalSquared;
                if (distance >= bestDistance || footprint.Contains(cell) || !StoreUtility.IsGoodStoreCell(cell, map, item, null, Faction.OfPlayer)) continue;
                best = cell; bestDistance = distance; found = priority;
            }
        }
        return best;
    }

    // A colonist free to take new work: the same gate the site rows apply.
    private static bool Free(Pawn pawn) => !(pawn.Drafted || pawn.Downed || pawn.InMentalState || !NativeFoodActivity.Available(pawn)
        || NativePawnActivities.Owned(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 || pawn.carryTracker.CarriedThing != null);

    private static bool Report(int count, Budget budget)
    {
        if (count < ReportCap) return true;
        budget.Cut();
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
        if (sources.Length > SourceCap) budget.Cut();
        if (sources.Length == 0) budget.Findings.Add("whole_source_stack_unavailable");
        LocalTargetInfo To(IntVec3 origin) => site.Thing != null ? new LocalTargetInfo(site.Thing) : new LocalTargetInfo(site.Target(origin));
        foreach (var pawn in roster.Take(PawnCap))
        {
            if (!Free(pawn) || !site.Eligible(pawn)) continue;
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
