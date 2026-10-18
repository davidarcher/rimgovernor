#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeConstructionActivity
{
    // A site the rows estimate. Placed: a blueprint or frame (Thing set). Planned: unplaced cells, where a route
    // from an origin ends at the piece nearest it (Target).
    private sealed record Site(string Id, BuildableDef Definition, IntVec3 Location, Func<IntVec3, IntVec3> Target,
        Thing? Thing, (ThingDef Def, int Count)[] Owed, float Work, Func<Pawn, bool> Eligible, Func<Pawn, bool> Finishes);

    // Every site, probe and free spawned colonist asked (#3341): nothing is capped. Each path is searched once per read
    // (NativePathLegs) and shared by every row that walks it, and the rows are built batched (NativePathLegs.Batched,
    // #3556): the legs every row could ask are collected first and searched together on the game's path job workers.
    internal static Obs.ConstructionActivityRead Read(Obs.ConstructionActivityRequest request, NativePathLegs? legs = null)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out _)) return new();
        legs ??= new(map);
        // Where the hop's run time goes, per part of the read (hop_run parts): the placement legality, the rows (both
        // runs of the batched read, less the searches), the path searches, the thermal read and the room predictions.
        var mark = Stopwatch.GetTimestamp();
        var searched = legs.SearchTicks;
        void Lap(string part, long less = 0) { var now = Stopwatch.GetTimestamp(); ProtoBoundary.HopPart(part, now - mark - less); mark = now; }
        // Each colonist gets a row wherever it is free, eligible and routed.
        var roster = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var finishGiver = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is WorkGiver_ConstructFinishFrames);
        var placed = new List<Site>();
        foreach (var building in request.Buildings)
        {
            var thing = map.thingGrid.ThingsListAt(new(building.Cell.X, 0, building.Cell.Z))
                .FirstOrDefault(t => t is IConstructible && t.Faction == Faction.OfPlayer && t.def.entityDefToBuild?.defName == building.Definition);
            if (thing is not IConstructible constructible || thing.def.entityDefToBuild is not BuildableDef definition) continue;
            var owed = constructible.TotalMaterialCost().Select(c => (c.thingDef, Count: constructible.ThingCountNeeded(c.thingDef)))
                .Where(c => c.Count > 0).ToArray();
            var floorKnown = !ConstructionSkillGuard.Quality(thing) || ConstructionSkillGuard.Setting(thing)?.Minimum >= 0;
            var work = thing is Frame frame ? frame.WorkLeft : definition.GetStatValueAbstract(StatDefOf.WorkToBuild, thing.Stuff);
            placed.Add(new Site(thing.GetUniqueLoadID(), definition, thing.Position, _ => thing.Position, thing, owed, work,
                p => !thing.IsForbidden(p), p => floorKnown && ConstructionSkillGuard.Allows(thing, p)));
        }
        var planned = request.Planned.Select(s => Prepare(map, s, findings)).ToArray();
        Lap("placement");
        var (options, answers, routes, found) = legs.Batched(() =>
        {
            // The collecting run's rows and findings are discarded with it; the real run's are the read's.
            var runFindings = new HashSet<string>(StringComparer.Ordinal);
            var runOptions = new List<Obs.ConstructionActivityOption>();
            foreach (var site in placed) Rows(map, legs, roster, site, finishGiver, runFindings, runOptions);
            var runAnswers = planned.Select(p => p.Site is { } site ? Answer(map, legs, roster, site, finishGiver, runFindings) : p.Unknown).ToArray();
            var runRoutes = Routes(map, legs, roster, request.Routes, runFindings);
            return (runOptions, runAnswers, runRoutes, runFindings);
        });
        findings.UnionWith(found);
        var paths = legs.SearchTicks - searched;
        Lap("rows", paths);
        ProtoBoundary.HopPart("paths", paths);
        var thermal = request.Thermal is { } asked ? NativeCoupledThermal.ReadPlanned(map, asked) : null;
        Lap("thermal");
        var predictions = NativeRoomPrediction.Predict(map, request.RoomPredictions);
        Lap("predictions");
        return new() { Context = context, Known = true,
            Options = EquatableArray.From(options), Planned = EquatableArray.From(answers), Routes = EquatableArray.From(routes),
            Thermal = thermal, RoomPredictions = predictions,
            Findings = EquatableArray.From(findings.OrderBy(s => s, StringComparer.Ordinal)) };
    }

    // Routes between planned sites (#3313): per probe and free colonist, the native path from the probe's cell (or the
    // pawn's own, FromStart) to its target cell nearest that origin. A probe's cell is every colonist's origin, so its
    // path is searched once. No path leaves the row's ticks unknown.
    private static List<Obs.CoupledRoute> Routes(Map map, NativePathLegs legs, Pawn[] roster, EquatableArray<Obs.PlannedRouteProbe> probes, HashSet<string> findings)
    {
        var rows = new List<Obs.CoupledRoute>();
        var pawns = roster.Where(Free).ToArray();
        foreach (var probe in probes)
        {
            var to = new IntVec3(probe.To.X, 0, probe.To.Z);
            var reach = probe.Reach.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            if (!to.InBounds(map) || (!probe.FromStart && !new IntVec3(probe.From.X, 0, probe.From.Z).InBounds(map)) || reach.Any(c => !c.InBounds(map)))
            {
                findings.Add("planned_route_out_of_bounds");
                continue;
            }
            foreach (var pawn in pawns)
            {
                var origin = probe.FromStart ? pawn.Position : new IntVec3(probe.From.X, 0, probe.From.Z);
                var target = reach.Length == 0 ? to : reach.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
                var cost = legs.Cost(pawn, origin, target, PathEndMode.Touch);
                rows.Add(new(pawn.GetUniqueLoadID(), new(origin.x, origin.z), probe.To, cost is { } ticks ? Fact.Known((double)ticks) : default)
                    { FromStart = probe.FromStart });
            }
        }
        return rows;
    }

    // The leg-free half of a hypothetical site (Prepare): its resolved definition and cells, native blueprint legality
    // over every piece, the footprint things with their clearing kinds, what it owes and its work.
    private sealed record Prepared(string Id, BuildableDef Definition, IntVec3[] Cells, bool Accepted, bool Illegal, bool Fogged,
        (Thing Thing, Clear Kind)[] Things, HashSet<IntVec3> Footprint, (ThingDef Def, int Count)[] Owed, float Work);

    // A hypothetical site's leg-free half: native blueprint legality for every piece and its footprint. Anything native
    // cannot resolve leaves the site unknown (no verdict, no rows; Site null with the unknown answer), never a default.
    private static (Obs.PlannedSiteAnswer Unknown, Prepared? Site) Prepare(Map map, Obs.PlannedConstructionSite request, HashSet<string> findings)
    {
        var unknown = new Obs.PlannedSiteAnswer(request.Id, default);
        if (request.Positions.Count == 0) { findings.Add("planned_site_empty"); return (unknown, null); }
        if (!PlacementPreviewOperation.TryResolveBuildable(request.Definition, out var definition, out _)
            || !definition.BuildableByPlayer || definition.blueprintDef == null) { findings.Add("planned_site_definition_unresolved"); return (unknown, null); }
        ThingDef? stuff = null;
        if (definition.MadeFromStuff)
        {
            stuff = string.IsNullOrEmpty(request.Stuff) ? GenStuff.DefaultStuffFor(definition) : PlacementPreviewOperation.ResolveThingDef(request.Stuff);
            if (stuff?.stuffProps == null || !stuff.IsStuff || !stuff.stuffProps.CanMake(definition)) { findings.Add("planned_site_stuff_unresolved"); return (unknown, null); }
        }
        else if (!string.IsNullOrEmpty(request.Stuff)) { findings.Add("planned_site_stuff_unresolved"); return (unknown, null); }
        var cells = request.Positions.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
        if (cells.Any(c => !c.InBounds(map))) { findings.Add("planned_site_out_of_bounds"); return (unknown, null); }
        var costs = PlacementPreviewOperation.ReadCosts(definition, stuff);
        if (costs == null) { findings.Add("planned_site_costs_unreadable"); return (unknown, null); }
        var rotation = new Rot4((int)request.Rotation);
        var research = definition.IsResearchFinished;
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
            if (research && !row.accepted)
            {
                // Native legality with every removable footprint thing skipped: what still refuses is not clearing's to fix.
                var cleared = PlacementPreviewOperation.EvaluateRotation(map, definition, definition.blueprintDef, cell, rotation, stuff, godMode: false,
                    skip: t => Kind(t) != Clear.Illegal);
                var hidden = !cleared.accepted && cell.Fogged(map);
                fogged |= hidden;
                illegal |= !cleared.accepted && !hidden;
            }
        }
        if (fogged) findings.Add("planned_site_fogged");
        foreach (var blocker in things) illegal |= Kind(blocker.Thing) == Clear.Illegal;
        var owed = costs.Where(c => c.count > 0).Select(c => (c.Definition, Count: c.count * cells.Length)).ToArray();
        // No finishing minimum is stored before placement: gate on the native prerequisite alone and say so.
        if (definition is ThingDef built && built.HasComp(typeof(CompQuality))) findings.Add("planned_quality_minimum_unset");
        var work = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) * cells.Length;
        return (unknown, new Prepared(request.Id, definition, cells, accepted, illegal, fogged,
            things.Select(b => (b.Thing, Kind: Kind(b.Thing))).ToArray(), footprint, owed, work));
    }

    // A prepared site's answer: its clearing ops, then the placed-site rows against its cells, and the verdict.
    private static Obs.PlannedSiteAnswer Answer(Map map, NativePathLegs legs, Pawn[] roster, Prepared site, WorkGiverDef? finishGiver, HashSet<string> findings)
    {
        var clearing = Clearing(map, legs, roster, site.Id, site.Definition, site.Things, site.Footprint, findings, out var unsure);
        var siteVerdict = site.Illegal ? Obs.PlannedSiteVerdict.Illegal
            : site.Fogged || site.Things.Any(t => t.Kind == Clear.Unestimable) ? Obs.PlannedSiteVerdict.Unestimable
            : unsure ? Obs.PlannedSiteVerdict.Unspecified : Obs.PlannedSiteVerdict.Feasible;
        var cells = site.Cells;
        IntVec3 Nearest(IntVec3 origin) => cells.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        var options = new List<Obs.ConstructionActivityOption>();
        Rows(map, legs, roster, new Site(site.Id, site.Definition, cells[0], Nearest, null, site.Owed, site.Work, _ => true, _ => true), finishGiver, findings, options);
        return new(site.Id, Fact.Known(site.Accepted))
        {
            Options = EquatableArray.From(options),
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
    // rows. A stack with no accepting storage outside the footprint has no op (unknown).
    private static List<Obs.PlannedSiteClearing> Clearing(Map map, NativePathLegs legs, Pawn[] roster, string site, BuildableDef definition,
        (Thing Thing, Clear Kind)[] things, HashSet<IntVec3> footprint, HashSet<string> findings, out bool unknown)
    {
        var result = new List<Obs.PlannedSiteClearing>();
        unknown = false;
        var pawns = roster.Where(Free).ToArray();
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
            if (Destination(map, item, footprint) is not { } destination) { unknown = true; findings.Add("planned_clearing_haul_destination_unknown"); continue; }
            var id = site + "/haul/" + item.GetUniqueLoadID();
            var rows = new List<Obs.ConstructionActivityOption>();
            foreach (var pawn in pawns)
            {
                var capacity = pawn.carryTracker.MaxStackSpaceEver(item.def);
                if (capacity <= 0 || item.IsForbidden(pawn) || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                    || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !pawn.CanReserveAndReach(item, PathEndMode.Touch, Danger.None)) continue;
                if (legs.Cost(pawn, pawn.Position, item, PathEndMode.Touch) is not { } first
                    || legs.Cost(pawn, item.Position, destination, PathEndMode.OnCell) is not { } loaded
                    || legs.Cost(pawn, destination, item, PathEndMode.Touch) is not { } returning) continue;
                var trips = (item.stackCount + capacity - 1) / capacity;
                rows.Add(new(id, definition.defName, new(destination.x, destination.z), pawn.GetUniqueLoadID(), item.def.defName, item.GetUniqueLoadID(),
                    new(item.Position.x, item.Position.z), item.stackCount, capacity, haulGiver, Fact.Known((double)first),
                    Fact.Known((double)loaded * trips + returning * (trips - 1)), Fact.Known(0.0))
                    { SiteApproachTicks = Fact.Known((double)first), AvailableInput = Fact.Known((double)item.stackCount) });
            }
            result.Add(new(id, Obs.PlannedClearingKind.Haul) { Options = EquatableArray.From(rows) });
        }
        return result;

        // A work op over its things: the route reaches the one nearest the pawn; finish is the summed native work.
        Obs.PlannedSiteClearing Work(string id, Obs.PlannedClearingKind kind, Thing[] targets, string giver, Func<Pawn, bool> able, Func<Pawn, Fact<double>> finish)
        {
            var rows = new List<Obs.ConstructionActivityOption>();
            foreach (var pawn in pawns.Where(able))
            {
                var nearest = targets.OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.thingIDNumber).First();
                if (legs.Cost(pawn, pawn.Position, nearest, PathEndMode.Touch) is not { } route) continue;
                var cell = new Cell(nearest.Position.x, nearest.Position.z);
                rows.Add(new(id, definition.defName, cell, pawn.GetUniqueLoadID(), "", "", cell, 0, 0, giver, default, default, finish(pawn))
                    { SiteApproachTicks = Fact.Known((double)route) });
            }
            return new(id, kind) { Options = EquatableArray.From(rows) };
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
        || NativePawnActivities.Committed(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 || pawn.carryTracker.CarriedThing != null);

    // Per pawn x whole-stack material source: site approach, first approach, delivery trips and finish work. The legs
    // are searched once each: colonist to site and to stack per (colonist cell, target), stack to site and back per
    // (stack, site), then combined into the rows.
    private static void Rows(Map map, NativePathLegs legs, Pawn[] roster, Site site, WorkGiverDef? finishGiver, HashSet<string> findings, List<Obs.ConstructionActivityOption> options)
    {
        // Mixed-material sites need a complete multi-input observation; never pretend one material completes them.
        if (site.Owed.Length > 1) { findings.Add("mixed_material_demand_unsupported"); return; }
        var sources = site.Owed.Length == 0 ? new Thing?[] { null }
            : map.listerThings.ThingsOfDef(site.Owed[0].Def).Where(t => t.Spawned && t.stackCount >= site.Owed[0].Count)
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Cast<Thing?>().ToArray();
        if (sources.Length == 0) findings.Add("whole_source_stack_unavailable");
        LocalTargetInfo To(IntVec3 origin) => site.Thing != null ? new LocalTargetInfo(site.Thing) : new LocalTargetInfo(site.Target(origin));
        foreach (var pawn in roster)
        {
            if (!Free(pawn) || !site.Eligible(pawn)) continue;
            if (legs.Cost(pawn, pawn.Position, To(pawn.Position), PathEndMode.Touch) is not { } siteRoute) continue;
            var finish = finishGiver != null && ConstructionSkillGuard.Capability(pawn) == true && site.Finishes(pawn)
                && (pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= site.Definition.constructionSkillPrerequisite;
            var speed = pawn.GetStatValue(StatDefOf.ConstructionSpeed);
            Fact<double> finishTicks = finish && speed > 0 ? Fact.Known((double)site.Work / speed) : default;
            foreach (var source in sources)
            {
                var count = site.Owed.Length == 0 ? 0 : site.Owed[0].Count;
                var capacity = source == null ? 0 : pawn.carryTracker.MaxStackSpaceEver(source.def);
                Fact<double> approach = default, delivery = default;
                if (source != null && capacity > 0 && !source.IsForbidden(pawn)
                    && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                    && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    && pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None))
                {
                    if (legs.Cost(pawn, pawn.Position, source, PathEndMode.Touch) is { } first
                        && legs.Cost(pawn, source.Position, To(source.Position), PathEndMode.Touch) is { } loaded
                        && legs.Cost(pawn, site.Target(source.Position), source, PathEndMode.Touch) is { } returning)
                    {
                        approach = Fact.Known((double)first);
                        var trips = (count + capacity - 1) / capacity;
                        delivery = Fact.Known((double)loaded * trips + returning * (trips - 1));
                    }
                }
                options.Add(new(site.Id, site.Definition.defName, new(site.Location.x, site.Location.z), pawn.GetUniqueLoadID(),
                    source?.def.defName ?? "", source?.GetUniqueLoadID() ?? "", new(source?.Position.x ?? site.Location.x, source?.Position.z ?? site.Location.z),
                    count, capacity, finishGiver?.defName ?? "", approach, delivery, finishTicks) { SiteApproachTicks = Fact.Known((double)siteRoute), AvailableInput = Fact.Known((double)(source?.stackCount ?? 0)) });
            }
        }
    }
}
