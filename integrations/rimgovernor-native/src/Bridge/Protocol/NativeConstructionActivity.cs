#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeConstructionActivity
{
    // A site the rows estimate. Placed: a blueprint or frame (Thing set). Planned: unplaced cells, where a route
    // from an origin ends at the piece nearest it (Target).
    private sealed record Site(string Id, BuildableDef Definition, IntVec3 Location, Func<IntVec3, IntVec3> Target,
        Thing? Thing, (ThingDef Def, int Count)[] Owed, float Work, Func<Pawn, bool> Eligible, Func<Pawn, bool> Finishes, ThingDef? Stuff = null, double WorkQuantum = 0, bool PaysXp = true);

    // Every site, probe and free spawned colonist asked (#3341): nothing is capped. Each path is searched once per read
    // (NativePathLegs) and shared by every row that walks it, and the rows are built batched (NativePathLegs.Batched,
    // #3556): the legs every row could ask are collected first and searched together on the game's path job workers.
    // A bad identity is refused, and a read that fails is unavailable (#3775): never an empty read.
    internal static Obs.ConstructionActivityRead Read(Obs.ConstructionActivityRequest request, NativePathLegs? legs = null)
    {
        if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out var error)) throw NativeFaults.Refused(error);
        try { return Read(map, context, request, legs ?? new(map)); }
        catch (Exception readError)
        {
            ModLog.Error("observe", "Construction activity read failed: " + readError);
            throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                Detail = PlacementPreviewOperation.Diagnostic("Construction activity could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
        }
    }

    private static Obs.ConstructionActivityRead Read(Map map, Common.ObservationContext context, Obs.ConstructionActivityRequest request, NativePathLegs legs)
    {
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
            var work = thing is Frame frame ? NativeConstructionWork.Remaining(frame.WorkLeft) : definition.GetStatValueAbstract(StatDefOf.WorkToBuild, thing.Stuff);
            placed.Add(new Site(thing.GetUniqueLoadID(), definition, thing.Position, _ => thing.Position, thing, owed, work,
                p => !thing.IsForbidden(p), p => floorKnown && ConstructionSkillGuard.Allows(thing, p), thing.Stuff, work,
                constructible.TotalMaterialCost().Any(c => c.count > 0)));
        }
        var planned = request.Planned.Select(s => Prepare(map, s, findings)).ToArray();
        // The standing removal designations (#3557, #3612, #3614): the Deconstruct the room reconcile stood over an interior
        // ruin or a stale piece (or the player's own), the Uninstall it stood over a piece it packs and the Haul it stood over
        // a chunk it moves out, with the ordinary stacks it moves out under a haul-out record, no designation (#3625). Under
        // voluntary control nothing else works them; the brain binds each building as its kind's work and each stack as a
        // delivery to storage outside its planned room's footprint (the whole map's best storage when no planned site covers
        // it). The Cut and RemoveFloor waves below are read as set ops (#3613, #3615).
        var removals = map.designationManager.AllDesignations
            .Where(d => d.def == DesignationDefOf.Deconstruct || d.def == DesignationDefOf.Uninstall || d.def == DesignationDefOf.Haul)
            .Select(d => (Kind: d.def == DesignationDefOf.Deconstruct ? Obs.PlannedClearingKind.Deconstruct
                : d.def == DesignationDefOf.Uninstall ? Obs.PlannedClearingKind.Uninstall : Obs.PlannedClearingKind.Haul, Thing: d.target.Thing))
            .Where(d => d.Thing != null && d.Thing.Spawned && (d.Kind == Obs.PlannedClearingKind.Deconstruct ? d.Thing is Verse.Building deconstructible && deconstructible.DeconstructibleBy(Faction.OfPlayer).Accepted
                : d.Kind == Obs.PlannedClearingKind.Uninstall ? d.Thing is Verse.Building packable && Uninstallable(packable)
                : Haulable(d.Thing)))
            .Select(d => (d.Kind, Thing: d.Thing!))
            .Concat(NativeDesignationGuards.HaulOuts(map).Where(Haulable).Select(t => (Kind: Obs.PlannedClearingKind.Haul, Thing: t))).Distinct().OrderBy(d => d.Thing.thingIDNumber).ThenBy(d => d.Kind).ToArray();
        // The standing CutPlant designations, one Cut op per contiguous wave of plants (#3613), and the standing RemoveFloor
        // designations, one RemoveFloor op per contiguous wave of cells (#3615).
        var cutWaves = CutWaves(map);
        var floorWaves = FloorWaves(map);
        Lap("placement");
        var (options, answers, routes, runRemovals, found) = legs.Batched(() =>
        {
            // The collecting run's rows and findings are discarded with it; the real run's are the read's.
            var runFindings = new HashSet<string>(StringComparer.Ordinal);
            var runOptions = new List<Obs.ConstructionActivityOption>();
            var setRoutes = new List<Obs.CoupledRoute>();
            foreach (var site in placed) Rows(map, legs, roster, site, finishGiver, runFindings, runOptions, setRoutes);
            var runAnswers = planned.Select(p => p.Site is { } site ? Answer(map, legs, roster, site, finishGiver, runFindings) : p.Unknown).ToArray();
            var runRoutes = Routes(map, legs, roster, request.Routes, runFindings);
            runRoutes.AddRange(setRoutes);
            var free = roster.Where(Free).ToArray();
            var deconstructGiver = Giver<WorkGiver_Deconstruct>();
            var uninstallGiver = Giver<WorkGiver_Uninstall>();
            var haulGiver = Giver<WorkGiver_HaulGeneral>();
            var removalOps = new List<Obs.PlannedSiteClearing>();
            foreach (var (kind, thing) in removals)
            {
                var id = thing.GetUniqueLoadID();
                if (kind == Obs.PlannedClearingKind.Haul)
                {
                    // The stack leaves the planned room it stands in: storage inside any planned footprint covering it is no destination.
                    var footprint = new HashSet<IntVec3>(planned.Where(p => p.Site != null && p.Site.Footprint.Contains(thing.Position)).SelectMany(p => p.Site!.Footprint));
                    if (Destination(map, thing, footprint) is not { } destination) { runFindings.Add("removal_haul_destination_unknown"); continue; }
                    removalOps.Add(new(id, kind) { Options = EquatableArray.From(HaulRows(legs, free, thing, id, thing.def.defName, haulGiver, destination)), Targets = [], Cells = [] });
                    continue;
                }
                var building = (Verse.Building)thing;
                if (kind == Obs.PlannedClearingKind.Uninstall)
                {
                    if (NativeDesignationGuards.RoofWait(building) != null) { runFindings.Add("removal_roof_support"); continue; }
                    removalOps.Add(new(id, kind) { Options = EquatableArray.From(RemovalRows(legs, free, building, id, building.def.defName, uninstallGiver, building.def.building.uninstallWork)), Targets = [], Cells = [] });
                    continue;
                }
                if (RoofCells(building) is not { } roofCells) { runFindings.Add("removal_roof_support"); continue; }
                removalOps.Add(new(id, kind)
                {
                    Options = EquatableArray.From(RemovalRows(legs, free, building, id, building.def.defName, deconstructGiver, DeconstructWork(building), roofCells)),
                    Targets = [],
                    Cells = EquatableArray.From(roofCells.Select(c => new Cell(c.x, c.z))),
                });
            }
            // A standing Cut wave (#3613): one set op per contiguous group of CutPlant-designated plants, named by its lowest id.
            var cutGiver = Giver<WorkGiver_PlantsCut>();
            foreach (var wave in cutWaves) removalOps.Add(CutOp(legs, free, wave, wave[0].GetUniqueLoadID(), wave[0].def.defName, cutGiver));
            // A standing FloorOut wave (#3615): one cell set op per contiguous group of RemoveFloor-designated cells, named by its first cell.
            var floorGiver = Giver<WorkGiver_ConstructRemoveFloor>();
            foreach (var wave in floorWaves) removalOps.Add(RemoveFloorOp(map, legs, free, wave, floorGiver));
            return (runOptions, runAnswers, runRoutes, removalOps, runFindings);
        });
        findings.UnionWith(found);
        var paths = legs.SearchTicks - searched;
        Lap("rows", paths);
        ProtoBoundary.HopPart("paths", paths);
        var thermal = request.Thermal is { } asked ? NativeCoupledThermal.ReadPlanned(map, asked) : null;
        Lap("thermal");
        var predictions = NativeRoomPrediction.Predict(map, request.RoomPredictions);
        Lap("predictions");
        return new() { Context = context,
            Options = EquatableArray.From(options), Planned = EquatableArray.From(answers), Routes = EquatableArray.From(routes),
            Removals = EquatableArray.From(runRemovals),
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
                rows.Add(new(pawn.GetUniqueLoadID(), new(origin.x, origin.z), probe.To, cost is { } ticks ? new Obs.RouteTicks.Reachable(ticks) : Obs.RouteTicks.None)
                    { FromStart = probe.FromStart });
            }
        }
        return rows;
    }

    // The leg-free half of a hypothetical site (Prepare): its resolved definition and cells, native blueprint legality
    // over every piece, the footprint things with their clearing kinds, what it owes and its work.
    private sealed record Prepared(string Id, BuildableDef Definition, IntVec3[] Cells, bool Illegal, bool Fogged,
        (Thing Thing, Clear Kind)[] Things, HashSet<IntVec3> Footprint, (ThingDef Def, int Count)[] Owed, float Work, ThingDef? Stuff);

    // A hypothetical site's leg-free half: native blueprint legality for every piece and its footprint. Anything native
    // cannot resolve leaves the site Unresolved (no rows, no clearing; Site null with the unresolved answer), never a default.
    private static (Obs.PlannedSiteAnswer Unknown, Prepared? Site) Prepare(Map map, Obs.PlannedConstructionSite request, HashSet<string> findings)
    {
        var unknown = new Obs.PlannedSiteAnswer(request.Id) { Verdict = Obs.PlannedSiteVerdict.Unresolved, Options = [], Clearing = [] };
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
        bool illegal = !research, fogged = false;
        foreach (var cell in cells)
        {
            var row = PlacementPreviewOperation.EvaluateRotation(map, definition, definition.blueprintDef, cell, rotation, stuff, godMode: false);
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
        return (unknown, new Prepared(request.Id, definition, cells, illegal, fogged,
            things.Select(b => (b.Thing, Kind: Kind(b.Thing))).ToArray(), footprint, owed, work, stuff));
    }

    // A prepared site's answer: its clearing ops, then the placed-site rows against its cells, and the verdict.
    private static Obs.PlannedSiteAnswer Answer(Map map, NativePathLegs legs, Pawn[] roster, Prepared site, WorkGiverDef? finishGiver, HashSet<string> findings)
    {
        var clearing = Clearing(map, legs, roster, site.Id, site.Definition, site.Things, site.Footprint, findings, out var unsure);
        var siteVerdict = site.Illegal ? Obs.PlannedSiteVerdict.Illegal
            : site.Fogged || unsure || site.Things.Any(t => t.Kind == Clear.Unestimable) ? Obs.PlannedSiteVerdict.Unestimable
            : Obs.PlannedSiteVerdict.Feasible;
        var cells = site.Cells;
        IntVec3 Nearest(IntVec3 origin) => cells.OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).First();
        var options = new List<Obs.ConstructionActivityOption>();
        Rows(map, legs, roster, new Site(site.Id, site.Definition, cells[0], Nearest, null, site.Owed, site.Work, _ => true, _ => true, site.Stuff, site.Work / cells.Length, site.Owed.Length > 0), finishGiver, findings, options);
        return new(site.Id)
        {
            Options = EquatableArray.From(options),
            Verdict = siteVerdict, Clearing = EquatableArray.From(clearing)
        };
    }

    // What clearing one footprint thing takes, in GenConstruct.HandleBlockingThingJob's kinds.
    private enum Clear { None, Cut, Haul, Mine, Deconstruct, Unestimable, Illegal }

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
        // A building the player may deconstruct (a ruin wall, door or furniture, the colony's own) comes down as a Deconstruct job (#3557).
        if (thing is Verse.Building building)
            return building.DeconstructibleBy(Faction.OfPlayer).Accepted && thing is not Mineable ? Clear.Deconstruct : thing.def.mineable ? Clear.Unestimable : Clear.Illegal;
        return thing.Faction == Faction.OfPlayer ? Clear.Unestimable : Clear.Illegal;
    }

    // Clearing ops: one Cut for the plants, one Mine for the natural rock, one Deconstruct per deconstructible building,
    // one Haul per haulable stack, with per-pawn rows. A stack with no accepting storage outside the footprint, and a
    // roof-holder whose removal the enclosure guard would hold, have no op (unknown).
    private static List<Obs.PlannedSiteClearing> Clearing(Map map, NativePathLegs legs, Pawn[] roster, string site, BuildableDef definition,
        (Thing Thing, Clear Kind)[] things, HashSet<IntVec3> footprint, HashSet<string> findings, out bool unknown)
    {
        var result = new List<Obs.PlannedSiteClearing>();
        unknown = false;
        var pawns = roster.Where(Free).ToArray();
        var plants = things.Where(t => t.Kind == Clear.Cut).Select(t => (Plant)t.Thing).OrderBy(t => t.thingIDNumber).ToArray();
        if (plants.Length > 0) result.Add(CutOp(legs, pawns, plants, site + "/cut", definition.defName, Giver<WorkGiver_PlantsCut>()));
        var rock = things.Where(t => t.Kind == Clear.Mine).Select(t => t.Thing).OrderBy(t => t.thingIDNumber).ToArray();
        if (rock.Length > 0)
        {
            // One Mine op over the rock, Targets the rocks lowest id first (what the brain binds its Work/Mine set step over, #3616).
            var mineId = site + "/mine";
            var rows = WorkRows(legs, pawns, rock, mineId, definition.defName, Giver<WorkGiver_Miner>(),
                pawn => !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Mining),
                pawn =>
                {
                    // JobDriver_Mine: a hit every round(100 / MiningSpeed) ticks; the hit that finds HP at or under the damage destroys it.
                    var speed = pawn.GetStatValue(StatDefOf.MiningSpeed);
                    return speed > 0 ? rock.Sum(r => Math.Ceiling(r.HitPoints / (double)NaturalRockDamagePerPickHit))
                        * Math.Round(JobDriver_Mine.BaseTicksBetweenPickHits / speed) : null;
                }, pawn => MiningLearning(pawn, rock));
            result.Add(new(mineId, Obs.PlannedClearingKind.Mine) { Options = EquatableArray.From(rows), Targets = EquatableArray.From(rock.Select(r => r.GetUniqueLoadID())), Cells = [] });
        }
        // One Deconstruct op per building, its rows naming it: the brain binds the op to it before placement (#3557). A
        // roof-holder's op carries the roofed cells its removal leaves unsupported (#3617); one whose support is unknown has no op.
        var deconstructGiver = Giver<WorkGiver_Deconstruct>();
        foreach (var building in things.Where(t => t.Kind == Clear.Deconstruct).Select(t => (Verse.Building)t.Thing).OrderBy(t => t.thingIDNumber))
        {
            if (RoofCells(building) is not { } roofCells) { unknown = true; findings.Add("planned_clearing_roof_support"); continue; }
            var id = site + "/deconstruct/" + building.GetUniqueLoadID();
            result.Add(new(id, Obs.PlannedClearingKind.Deconstruct)
            {
                Options = EquatableArray.From(RemovalRows(legs, pawns, building, id, definition.defName, deconstructGiver, DeconstructWork(building), roofCells)),
                Targets = [],
                Cells = EquatableArray.From(roofCells.Select(c => new Cell(c.x, c.z))),
            });
        }
        var haulGiver = Giver<WorkGiver_HaulGeneral>();
        foreach (var item in things.Where(t => t.Kind == Clear.Haul).Select(t => t.Thing).OrderBy(t => t.thingIDNumber))
        {
            if (Destination(map, item, footprint) is not { } destination) { unknown = true; findings.Add("planned_clearing_haul_destination_unknown"); continue; }
            var id = site + "/haul/" + item.GetUniqueLoadID();
            result.Add(new(id, Obs.PlannedClearingKind.Haul) { Options = EquatableArray.From(HaulRows(legs, pawns, item, id, definition.defName, haulGiver, destination)), Targets = [], Cells = [] });
        }
        return result;
    }

    private static Obs.ProductiveSkillWork? MiningLearning(Pawn pawn, Thing[] rocks)
    {
        var learning = NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Mining, StatDefOf.MiningSpeed,
            rocks.Sum(r => Math.Ceiling(r.HitPoints / (double)NaturalRockDamagePerPickHit)), 1, 0.07, true, false,
            "JobDriver_Mine: learning each callback; rate changes at pick timer reset");
        if (learning == null) return null;
        return learning with
        {
            Rates = EquatableArray.From(learning.Rates.Select(r => new Obs.SkillWorkRate(r.StoredLevel,
                r.WorkPerTick > 0
                    ? 1.0 / Math.Max(1, Math.Round(JobDriver_Mine.BaseTicksBetweenPickHits / r.WorkPerTick)) : 0))),
            Stages = EquatableArray.From(rocks.Select(r => new Obs.ProductiveWorkStage(
                Math.Ceiling(r.HitPoints / (double)NaturalRockDamagePerPickHit), 1,
                pawn.Faction == null || r.Faction != pawn.Faction ? 0.07 : 0, 1, true)))
        };
    }
    // A set op's rows: one per able pawn, routed to the member nearest it (Location that member's cell); finish is the summed
    // native work (null when the pawn's rate is zero: it cannot finish).
    private static List<Obs.ConstructionActivityOption> WorkRows(NativePathLegs legs, Pawn[] pawns, Thing[] targets, string id, string definition, string giver,
        Func<Pawn, bool> able, Func<Pawn, double?> finish, Func<Pawn, Obs.ProductiveSkillWork?>? learning = null)
    {
        var rows = new List<Obs.ConstructionActivityOption>();
        foreach (var pawn in pawns.Where(able))
        {
            var nearest = targets.OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).ThenBy(t => t.thingIDNumber).First();
            if (legs.Cost(pawn, pawn.Position, nearest, PathEndMode.Touch) is not { } route) continue;
            var cell = new Cell(nearest.Position.x, nearest.Position.z);
            rows.Add(new(id, definition, cell, pawn.GetUniqueLoadID(), "", "", cell, 0, 0, giver, route,
                new Obs.ConstructionDelivery.Unsourced(), Finish(finish(pawn), () => learning?.Invoke(pawn)), 0, new Obs.StackStorage.Unsourced()));
        }
        return rows;
    }

    // A Cut op over its plants (a planned site's, or a standing wave's, #3613), Targets the plants lowest id first: rows for a
    // pawn not incapable of PlantCutting (the cut giver's work type) and willing to cut every plant; its Finish sums
    // harvestWork / JobDriver_PlantWork.WorkDonePerTick (a rate of zero: CannotFinish).
    private static Obs.PlannedSiteClearing CutOp(NativePathLegs legs, Pawn[] pawns, Plant[] plants, string id, string definition, string giver)
    {
        var rows = WorkRows(legs, pawns, plants, id, definition, giver,
            pawn => !pawn.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting) && plants.All(p => PlantUtility.PawnWillingToCutPlant_Job(p, pawn)),
            pawn => plants.Select(p => (double)JobDriver_PlantWork.WorkDonePerTick(pawn, p)).All(r => r > 0)
                ? plants.Sum(p => (double)p.def.plant.harvestWork / JobDriver_PlantWork.WorkDonePerTick(pawn, p)) : null,
            pawn => NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Plants, StatDefOf.PlantWorkSpeed,
                plants.Sum(p => (double)p.def.plant.harvestWork), 1, 0.085, true, false,
                "JobDriver_PlantCut: yielding plants learn before work") is { } learning ? learning with
                { Stages = EquatableArray.From(plants.Select(p => new Obs.ProductiveWorkStage(p.def.plant.harvestWork,
                    UnityEngine.Mathf.Lerp(3.3f, 1f, p.Growth), p.def.plant.harvestedThingDef != null && p.CanYieldNow() ? 0.085 : 0))) } : null);
        return new(id, Obs.PlannedClearingKind.Cut) { Options = EquatableArray.From(rows), Targets = EquatableArray.From(plants.Select(p => p.GetUniqueLoadID())), Cells = [] };
    }

    // The standing Cut waves (#3613): the spawned plants under a CutPlant designation (the room reconcile's Cut, or the
    // player's), grouped by contiguity (eight-adjacent cells), each wave lowest thing id first. CutPlant takes no guard
    // record, so the cells group the wave; a HarvestPlant stood as chop-wood is not read (it is a harvest's designation).
    private static List<Plant[]> CutWaves(Map map)
    {
        var plants = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Select(d => d.target.Thing as Plant)
            .Where(p => p != null && p.Spawned).Select(p => p!).Distinct().OrderBy(p => p.thingIDNumber).ToList();
        var byCell = plants.GroupBy(p => p.Position).ToDictionary(g => g.Key, g => g.ToArray());
        var seen = new HashSet<Plant>();
        var waves = new List<Plant[]>();
        foreach (var seed in plants)
        {
            if (!seen.Add(seed)) continue;
            var wave = new List<Plant> { seed };
            var pending = new Queue<Plant>();
            pending.Enqueue(seed);
            while (pending.Count > 0)
            {
                var plant = pending.Dequeue();
                foreach (var offset in GenAdj.AdjacentCellsAndInside)
                    if (byCell.TryGetValue(plant.Position + offset, out var neighbours))
                        foreach (var neighbour in neighbours)
                            if (seen.Add(neighbour)) { wave.Add(neighbour); pending.Enqueue(neighbour); }
            }
            waves.Add(wave.OrderBy(p => p.thingIDNumber).ToArray());
        }
        return waves;
    }

    // JobDriver_RemoveFloor's work per cell (JobDriver_AffectFloor.BaseWorkAmount), at ConstructionSpeed x 1.7 a tick.
    private const double RemoveFloorWork = 200.0;

    // A RemoveFloor op over a wave of cells (#3615), Id "removefloor/{x}/{z}/x{n}" of its first cell (as GroundStepMethod names
    // the FloorOut method), Definition the first cell's terrain, Cells the wave: rows for a free pawn not incapable of
    // Construction and able to manipulate, routed to the cell nearest it (Location that cell); its Finish is the cells'
    // summed floor-removal work at JobDriver_RemoveBuilding's rate (a rate of zero: CannotFinish).
    private static Obs.PlannedSiteClearing RemoveFloorOp(Map map, NativePathLegs legs, Pawn[] pawns, IntVec3[] cells, string giver)
    {
        var id = "removefloor/" + cells[0].x + "/" + cells[0].z + "/x" + cells.Length;
        var definition = map.terrainGrid.TerrainAt(cells[0]).defName;
        var rows = new List<Obs.ConstructionActivityOption>();
        foreach (var pawn in pawns)
        {
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
            var nearest = cells.OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z).First();
            if (legs.Cost(pawn, pawn.Position, nearest, PathEndMode.Touch) is not { } route) continue;
            var rate = pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7;
            var cell = new Cell(nearest.x, nearest.z);
            rows.Add(new(id, definition, cell, pawn.GetUniqueLoadID(), "", "", cell, 0, 0, giver, route, new Obs.ConstructionDelivery.Unsourced(),
                Finish(rate > 0 ? Math.Ceiling(RemoveFloorWork * cells.Length / rate) : null, () => NativeProductiveSkill.ReadWork(pawn,
                    SkillDefOf.Construction, StatDefOf.ConstructionSpeed, RemoveFloorWork * cells.Length, 1.7, 0.1,
                    false, false, "JobDriver_AffectFloor: work then learning") is { } learning
                    ? learning with { WorkQuantum = RemoveFloorWork } : null), 0, new Obs.StackStorage.Unsourced()));
        }
        return new(id, Obs.PlannedClearingKind.RemoveFloor) { Options = EquatableArray.From(rows), Targets = [], Cells = EquatableArray.From(cells.Select(c => new Cell(c.x, c.z))) };
    }

    // A row's finish: the work's ticks with the skill learning it pays, or CannotFinish without ticks (the learning unread).
    private static Obs.ConstructionFinish Finish(double? ticks, Func<Obs.ProductiveSkillWork?> learning) =>
        ticks is { } work ? new Obs.ConstructionFinish.Finishes(work, learning()) : new Obs.ConstructionFinish.CannotFinish();

    // The standing FloorOut waves (#3615): the cells under a RemoveFloor designation (the room reconcile's FloorOut, or the
    // player's) whose top layer can still be removed, grouped by contiguity (eight-adjacent cells), each wave lowest x then z first.
    private static List<IntVec3[]> FloorWaves(Map map)
    {
        var cells = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.RemoveFloor).Select(d => d.target.Cell)
            .Where(c => c.InBounds(map) && map.terrainGrid.CanRemoveTopLayerAt(c)).Distinct().OrderBy(c => c.x).ThenBy(c => c.z).ToList();
        var pending = new HashSet<IntVec3>(cells);
        var waves = new List<IntVec3[]>();
        foreach (var seed in cells)
        {
            if (!pending.Remove(seed)) continue;
            var wave = new List<IntVec3> { seed };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                foreach (var offset in GenAdj.AdjacentCells)
                    if (pending.Remove(cell + offset)) { wave.Add(cell + offset); queue.Enqueue(cell + offset); }
            }
            waves.Add(wave.OrderBy(c => c.x).ThenBy(c => c.z).ToArray());
        }
        return waves;
    }

    private static string Giver<T>() => DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.Worker is T)?.defName ?? "";

    // JobDriver_Deconstruct's work: WorkToBuild clamped to 20..3000.
    private static double DeconstructWork(Verse.Building building) => Math.Min(3000.0, Math.Max(20.0, building.GetStatValue(StatDefOf.WorkToBuild)));

    // A building the colony's Uninstall job takes (WorkGiver_Uninstall, Designator_Uninstall): minifiable and the colony's own (#3612).
    internal static bool Uninstallable(Verse.Building building) =>
        building.def.category == ThingCategory.Building && building.def.Minifiable && building.Faction == Faction.OfPlayer;

    // The roofed cells a Deconstruct op removes the roof from first (#3617): none for a building the enclosure guard
    // would not wait on; a roof-holder's unsupported cells (RoofSupportSafety.Unsupported); null when those are unknown.
    private static List<IntVec3>? RoofCells(Verse.Building building) =>
        NativeDesignationGuards.RoofWait(building) == null ? new List<IntVec3>() : RoofSupportSafety.Unsupported(building);

    // A building's removal rows (#3557, #3612), one per free pawn not incapable of Construction and able to manipulate, with
    // a route to it: Site is the op (a planned clearing's id, or the building's own for a standing designation), Input the
    // building's id and Resource its def. Its Finish is the removal job's work (JobDriver_Deconstruct's clamped
    // WorkToBuild, JobDriver_Uninstall's uninstallWork) at JobDriver_RemoveBuilding's rate (ConstructionSpeed x 1.7 a
    // tick); a rate of zero: CannotFinish. A roof-holder's rows (#3617) add JobDriver_RemoveRoof's work (65 a cell at
    // the same rate) for its roofCells and route to, and locate at, the one nearest the pawn.
    private static List<Obs.ConstructionActivityOption> RemovalRows(NativePathLegs legs, Pawn[] pawns, Verse.Building building, string site, string definition, string giver, double work,
        List<IntVec3>? roofCells = null)
    {
        var rows = new List<Obs.ConstructionActivityOption>();
        foreach (var pawn in pawns)
        {
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
            var roofed = roofCells == null || roofCells.Count == 0;
            var at = roofed ? building.Position : roofCells!.OrderBy(c => c.DistanceToSquared(pawn.Position)).ThenBy(c => c.x).ThenBy(c => c.z).First();
            if (legs.Cost(pawn, pawn.Position, roofed ? building : (LocalTargetInfo)at, PathEndMode.Touch) is not { } route) continue;
            var cell = new Cell(at.x, at.z);
            var rate = pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7;
            var total = work + RemoveRoofWorkPerCell * (roofCells?.Count ?? 0);
            rows.Add(new(site, definition, cell, pawn.GetUniqueLoadID(), building.def.defName, building.GetUniqueLoadID(), cell, 0, 0, giver,
                route, new Obs.ConstructionDelivery.Unsourced(), Finish(rate > 0 ? Math.Ceiling(total / rate) : null, () => NativeProductiveSkill.ReadWork(pawn,
                    SkillDefOf.Construction, StatDefOf.ConstructionSpeed, total, 1.7, 0, false, false,
                    "JobDriver_RemoveBuilding: construction speed, no learning") is { } learning ? learning with
                    { Stages = EquatableArray.From(Enumerable.Repeat(new Obs.ProductiveWorkStage(RemoveRoofWorkPerCell, 1, 0d), roofCells?.Count ?? 0)
                        .Append(new(work, 1, 0d))) } : null), 0, new Obs.StackStorage.Unsourced()));
        }
        return rows;
    }
    // JobDriver_AffectRoof's BaseWorkAmount, worked down at ConstructionSpeed x 1.7 a tick.
    private const double RemoveRoofWorkPerCell = 65.0;

    // A stack's haul rows (a planned clearing's, or a standing Haul designation's, #3614), one per free pawn able to haul
    // it with a carry capacity and routes: Site is the op, Input the stack's id, Resource its def, Origin its cell,
    // Location the destination, Count the stack and Capacity the carry; SiteApproachTicks and the Carried delivery's
    // ApproachTicks the pickup route, its DeliveryTicks the loaded carries plus the returns between trips, a Finish of 0
    // (the carry is the work), AvailableInput the stack and Storage where storage would take it.
    private static List<Obs.ConstructionActivityOption> HaulRows(NativePathLegs legs, Pawn[] pawns, Thing item, string site, string definition, string giver, IntVec3 destination)
    {
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
            rows.Add(new(site, definition, new(destination.x, destination.z), pawn.GetUniqueLoadID(), item.def.defName, item.GetUniqueLoadID(),
                new(item.Position.x, item.Position.z), item.stackCount, capacity, giver, first,
                new Obs.ConstructionDelivery.Carried(first, (double)loaded * trips + returning * (trips - 1)), new Obs.ConstructionFinish.Finishes(0, null),
                item.stackCount, StackStorageOf(item.Map, item)));
        }
        return rows;
    }

    // A spawned item a standing Haul designation names that hauling can move (#3614).
    private static bool Haulable(Thing thing) => thing.def.category == ThingCategory.Item && thing.def.EverHaulable;

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

    // A colonist free to take new work: the same gate the site rows apply. A vanilla job's carry is put down when an order
    // preempts that job (NativePawnActivities.CarryHeld, #3537), so it does not hold the colonist.
    private static bool Free(Pawn pawn) => !(pawn.Drafted || pawn.Downed || pawn.InMentalState || !NativeFoodActivity.Available(pawn)
        || NativePawnActivities.Owned(pawn) || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0 || NativePawnActivities.CarryHeld(pawn));

    // A site's material sources (#3673, #3046): every spawned stack of its material the player may use, lowest id first,
    // each with the share it can supply (the demand, or the whole stack under it) and its storage distance
    // (StackStorageOf). The reader picks the whole alternatives, or the set nearest the site that covers the demand, and
    // the stacks a remote delivery collects. Too little stock leaves none (source_stock_short).
    private static (Thing? Source, int Share, Obs.StackStorage Storage)[] Sources(Map map, Site site, HashSet<string> findings)
    {
        if (site.Owed.Length == 0) return [(null, 0, new Obs.StackStorage.Unsourced())];
        var (def, need) = site.Owed[0];
        var stacks = map.listerThings.ThingsOfDef(def).Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer))
            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        if (!stacks.Any(t => t.stackCount >= need) && stacks.Sum(t => (long)t.stackCount) < need) { findings.Add("source_stock_short"); return []; }
        return stacks.Select(t => ((Thing?)t, Math.Min(t.stackCount, need), StackStorageOf(map, t))).ToArray();
    }

    // Straight-line distance from the stack to the nearest cell of any storage that accepts it: zero when it already
    // sits in storage, Unaccepted when none accepts it.
    internal static Obs.StackStorage StackStorageOf(Map map, Thing thing)
    {
        if (thing.GetSlotGroup() != null) return new Obs.StackStorage.Accepted(0);
        var best = double.PositiveInfinity;
        foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder)
        {
            if (!group.Settings.AllowedToAccept(thing)) continue;
            foreach (var cell in group.CellsList) best = Math.Min(best, (cell - thing.Position).LengthHorizontal);
        }
        return double.IsPositiveInfinity(best) ? new Obs.StackStorage.Unaccepted() : new Obs.StackStorage.Accepted(best);
    }

    // Per pawn x material source (Sources): site approach, first approach, delivery trips for the source's share and
    // finish work; Count stays the site's owed count and AvailableInput the stack's. The legs are searched once each:
    // colonist to site and to stack per (colonist cell, target), stack to site and back per (stack, site), then combined
    // into the rows. A placed site also adds, per pawn and stack under its demand, the route from the site back to the
    // stack (to setRoutes): the leg one pawn walks between delivering one stack of a set and fetching the next.
    private static void Rows(Map map, NativePathLegs legs, Pawn[] roster, Site site, WorkGiverDef? finishGiver, HashSet<string> findings,
        List<Obs.ConstructionActivityOption> options, List<Obs.CoupledRoute>? setRoutes = null)
    {
        // Mixed-material sites need a complete multi-input observation; never pretend one material completes them.
        if (site.Owed.Length > 1) { findings.Add("mixed_material_demand_unsupported"); return; }
        var sources = Sources(map, site, findings);
        LocalTargetInfo To(IntVec3 origin) => site.Thing != null ? new LocalTargetInfo(site.Thing) : new LocalTargetInfo(site.Target(origin));
        foreach (var pawn in roster)
        {
            if (!Free(pawn) || !site.Eligible(pawn)) continue;
            if (legs.Cost(pawn, pawn.Position, To(pawn.Position), PathEndMode.Touch) is not { } siteRoute) continue;
            var finish = finishGiver != null && ConstructionSkillGuard.Capability(pawn) && site.Finishes(pawn)
                && (pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0) >= site.Definition.constructionSkillPrerequisite;
            var factor = 1.7 * (site.Stuff?.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor) ?? 1f);
            // Only a finisher reads ConstructionSpeed: a hauling-only pawn (Construction disabled) has the stat disabled.
            var speed = finish ? pawn.GetStatValue(StatDefOf.ConstructionSpeed) * factor : 0;
            var skillWork = finish ? NativeProductiveSkill.ReadWork(pawn, SkillDefOf.Construction, StatDefOf.ConstructionSpeed,
                site.Work, factor, site.PaysXp ? 0.25 : 0, true, false, "JobDriver_ConstructFinishFrame: learning before construction work") : null;
            if (skillWork != null) skillWork = skillWork with { WorkQuantum = site.WorkQuantum };
            Obs.ConstructionFinish finishes = finish && speed > 0 ? new Obs.ConstructionFinish.Finishes((double)site.Work / speed, skillWork) : new Obs.ConstructionFinish.CannotFinish();
            foreach (var (source, share, storage) in sources)
            {
                var count = site.Owed.Length == 0 ? 0 : site.Owed[0].Count;
                var capacity = source == null ? 0 : pawn.carryTracker.MaxStackSpaceEver(source.def);
                // How the pawn carries the stack: no stack, a carry it cannot make, a leg with no path, or the priced carry.
                Obs.ConstructionDelivery delivery;
                if (source == null) delivery = new Obs.ConstructionDelivery.Unsourced();
                else if (capacity <= 0 || source.IsForbidden(pawn)
                    || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                    || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                    || !pawn.CanReserveAndReach(source, PathEndMode.Touch, Danger.None)) delivery = new Obs.ConstructionDelivery.CannotCarry();
                else if (legs.Cost(pawn, pawn.Position, source, PathEndMode.Touch) is { } first
                    && legs.Cost(pawn, source.Position, To(source.Position), PathEndMode.Touch) is { } loaded
                    && legs.Cost(pawn, site.Target(source.Position), source, PathEndMode.Touch) is { } returning)
                {
                    if (share < count)
                        setRoutes?.Add(new(pawn.GetUniqueLoadID(), new(site.Location.x, site.Location.z), new(source.Position.x, source.Position.z), new Obs.RouteTicks.Reachable(returning)) { FromStart = false });
                    var trips = (share + capacity - 1) / capacity;
                    delivery = new Obs.ConstructionDelivery.Carried(first, (double)loaded * trips + returning * (trips - 1));
                }
                else delivery = new Obs.ConstructionDelivery.Unreachable();
                options.Add(new(site.Id, site.Definition.defName, new(site.Location.x, site.Location.z), pawn.GetUniqueLoadID(),
                    source?.def.defName ?? "", source?.GetUniqueLoadID() ?? "", new(source?.Position.x ?? site.Location.x, source?.Position.z ?? site.Location.z),
                    count, capacity, finishGiver?.defName ?? "", siteRoute, delivery, finishes, source?.stackCount ?? 0, storage));
            }
        }
    }
}
