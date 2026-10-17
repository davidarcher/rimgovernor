#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Policy;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Common = RimGovernor.Contracts.Wire.Common;
using Cell = RimGovernor.Brain.Domain.Cell;
using Domain = RimGovernor.Brain.Domain;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only (#3313): one builder and one planned 3x3 wood room on an open patch. The
// planned-site read must return native rows for the room's wall run, door and floor and native routes between them
// (and to the roof); the coupled forecast over those rows, routes and the native roof work must then schedule the
// whole room for that one builder within a day, site after site. Nothing is placed or ordered.
public sealed partial class PawnControlFixture
{
    private static IntVec3 plannedRoomOrigin;

    [Tool("test/planned_room_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Clear one open 14x12 patch near the one builder for a planned 3x3 room.")]
    public async Task<object> PlannedRoomPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var builder = PlannedRoomBuilder(map);
            const int width = 14, height = 12;
            plannedRoomOrigin = GenRadial.RadialCellsAround(builder.Position, 40, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.Roofed(map)
                    && !cell.GetThingList(map).Any(t => t.def.category == Verse.ThingCategory.Pawn || t.def.category == Verse.ThingCategory.Building))
                && builder.CanReach(c, PathEndMode.Touch, Danger.None));
            if (plannedRoomOrigin == default) throw new InvalidOperationException("No open reachable 14x12 patch.");
            foreach (var cell in new CellRect(plannedRoomOrigin.x, plannedRoomOrigin.z, width, height).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is Verse.ThingCategory.Item or Verse.ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            return new { success = true, builder = builder.GetUniqueLoadID(), origin = new { x = plannedRoomOrigin.x, z = plannedRoomOrigin.z } };
        }, ct).ConfigureAwait(false);

    private static Pawn PlannedRoomBuilder(Map map)
    {
        var free = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).ToArray();
        if (free.Length != 1 || free[0].WorkTypeIsDisabled(WorkTypeDefOf.Construction))
            throw new InvalidOperationException("Exactly one free colonist able to construct is required; found " + free.Length + ".");
        return free[0];
    }

    private static IntVec3 RoomAt(int x, int z) => new(plannedRoomOrigin.x + x, 0, plannedRoomOrigin.z + z);

    // One main-thread call: the builder stands at (1,1), two wood stacks are spawned and the room is read and forecast
    // before any job can move a log.
    [Tool("test/planned_room_read", Description = "UNSAFE FOR MODEL EXECUTION. On the prepared patch place the builder and two wood stacks, read the planned 3x3 room's sites and the routes between them, and forecast the room for the one builder.")]
    public async Task<object> PlannedRoomRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (plannedRoomOrigin == default) throw new InvalidOperationException("Prepare planned_room first.");
            var map = Find.CurrentMap;
            var builder = PlannedRoomBuilder(map);
            var pawn = builder.GetUniqueLoadID();
            builder.jobs.StopAll();
            builder.Position = RoomAt(1, 1);
            builder.Notify_Teleported(true, true);
            foreach (var at in new[] { RoomAt(11, 2), RoomAt(11, 8) })
            {
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = wood.def.stackLimit;
                GenSpawn.Spawn(wood, at, map);
            }
            var context = Context();
            var identity = context.Identity!;

            // The 3x3 room's sites as PlannedSitesOf names them: ring (4..8)^2 less the door, the door south, the
            // interior's plank floor and its roof.
            Cell C(IntVec3 cell) => new(cell.x, cell.z);
            var door = RoomAt(6, 4);
            var interior = new CellRect(plannedRoomOrigin.x + 5, plannedRoomOrigin.z + 5, 3, 3).Cells.Select(C).OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
            var walls = new CellRect(plannedRoomOrigin.x + 4, plannedRoomOrigin.z + 4, 5, 5).EdgeCells.Where(c => c != door).Select(C)
                .OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
            var floor = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            Amount[] Priced(List<ThingDefCountClass> costs, int pieces) =>
                costs.Select(c => new Amount(new Resource(c.thingDef.defName), (long)c.count * pieces)).ToArray();
            PlannedBuildSite Site(string kind, OpKind op, string def, string stuff, Cell[] cells, Amount[] material) =>
                new("planned/room1/" + kind, 1, op, def, stuff, Domain.Rotation.North, Domain.EquatableArray.From(cells))
                    { Material = Domain.EquatableArray.From(material) };
            var sites = new List<PlannedBuildSite>
            {
                Site("wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", walls, Priced(ThingDefOf.Wall.CostListAdjusted(ThingDefOf.WoodLog), walls.Length)),
                Site("door/WoodLog", OpKind.DoorIn, "Door", "WoodLog", [C(door)], Priced(ThingDefOf.Door.CostListAdjusted(ThingDefOf.WoodLog), 1)),
                Site("floor/" + floor.defName, OpKind.FloorIn, floor.defName, "", interior, Priced(floor.CostList ?? new List<ThingDefCountClass>(), interior.Length)),
                Site("roof", OpKind.RoofIn, "", "", interior, []),
            };

            // The production read path (Rounder.PlannedSitesAsync): one read of the sites, then one of the routes between them.
            var joined = PolicyFunctions.JoinPlannedSites(sites,
                NativeConstructionActivity.Read(new(identity, default) { Planned = PolicyFunctions.PlannedSiteRequest(sites) }));
            foreach (var site in joined.Sites.Where(s => s.Op != OpKind.RoofIn))
            {
                if (site.Verdict != Obs.PlannedSiteVerdict.Feasible || !site.Options.Any(o => o.Pawn == pawn && o.FinishTicks.IsKnown && o.SiteApproachTicks.IsKnown
                    && (site.Material.Count == 0 || o.ApproachTicks.IsKnown && o.DeliveryTicks.IsKnown)))
                    throw new InvalidOperationException("Site " + site.Id + " has no complete row for the builder: verdict " + site.Verdict + ", rows "
                        + string.Join("; ", site.Options) + "; findings " + string.Join(",", joined.Findings));
            }
            var asked = PolicyFunctions.PlannedRouteProbes(joined.Sites);
            var facts = PolicyFunctions.JoinPlannedRoutes(joined, asked,
                NativeConstructionActivity.Read(new(identity, default) { Routes = asked }));
            if (asked.Count == 0 || facts.Findings.Any(f => f.StartsWith("planned_route", StringComparison.Ordinal)))
                throw new InvalidOperationException("Route probes " + asked.Count + "; findings " + string.Join(",", facts.Findings));
            var unrouted = asked.Where(p => !facts.Routes.Any(r => r.Pawn == pawn && r.To == p.To && r.FromStart == p.FromStart
                && (p.FromStart || r.From == p.From) && r.Ticks.IsKnown && double.IsFinite(r.Ticks.Value) && r.Ticks.Value >= 0)).ToArray();
            if (unrouted.Length > 0)
                throw new InvalidOperationException("No known native route for the builder on " + unrouted.Length + " probes: " + string.Join("; ", unrouted.Take(8)));

            // The coupled forecast of that one builder over the native rows, routes and roof work, with needs held
            // satisfied: the whole room must be scheduled within a day, site after site.
            var coupled = NativeCoupledForecast.Read(map, context);
            var roofWork = coupled.Thermal?.Workers.FirstOrDefault(w => w.Pawn == pawn)
                ?? throw new InvalidOperationException("The coupled read has no thermal worker row for the builder.");
            if (!roofWork.RoofCellWorkTicks.IsKnown) throw new InvalidOperationException("The builder's roof cell work is unknown.");
            var horizon = (double)GenDate.TicksPerDay;
            var forecastFacts = PlannedRoomFacts(facts, pawn, context, roofWork);
            var request = PolicyFunctions.CoupledForecastRequestOf(new("world/map/load", context.Tick), horizon / GenDate.TicksPerDay,
                forecastFacts, new(), new() { Work = PolicyFunctions.ForecastWorkInputsOf(forecastFacts) }, [], new());
            var result = PolicyFunctions.CompareActivityOutcomes(request, forecastFacts.CoupledAlternatives!, 16384);
            var why = string.Join(";", forecastFacts.CoupledAlternatives!.Findings.Concat(result.Selection.Findings.Select(f => f.ToString())));
            var outcome = result.Selected.PlannedRooms.SingleOrDefault(r => r.Room == 1)
                ?? throw new InvalidOperationException("The forecast completed no site of the room: " + why);
            var missing = sites.Select(s => s.Id).Where(id => !outcome.CompletedSites.Contains(id)).ToArray();
            if (missing.Length > 0 || outcome.Complete.Count == 0)
                throw new InvalidOperationException("The forecast left the room partial; missing " + string.Join(",", missing) + "; " + why);
            var planned = result.Selected.Plan.Allocations.Where(a => a.Activity.StartsWith("planned/room1/", StringComparison.Ordinal))
                .OrderBy(a => a.Depart).ToArray();
            if (planned.Any(a => a.Pawn != pawn) || planned.Zip(planned.Skip(1), (a, b) => b.Depart < a.End).Any(o => o) || planned[^1].End > horizon)
                throw new InvalidOperationException("The room is not scheduled site after site for the builder within the day: "
                    + string.Join("; ", planned.Select(a => a.Activity + "@" + a.Pawn + " " + a.Depart + ".." + a.End)));
            return new
            {
                success = true, builder = pawn,
                sites = facts.Sites.Select(s => new { site = s.Id, rows = s.Options.Count, positions = s.Positions.Count }).ToArray(),
                probes = asked.Count, routes = facts.Routes.Count,
                complete = outcome.Complete.ToArray(),
                schedule = planned.Select(a => new { activity = a.Activity, depart = a.Depart, start = a.Start, end = a.End, travel = a.TravelTicks }).ToArray(),
                limitations = "Needs are held satisfied and nothing else competes; proves native rows and routes let one builder chain the room, not live selection.",
            };
        }, ct).ConfigureAwait(false);

    // The round's facts for this one builder (the shape of WorkResourceTestFacts with the planned room): every hour
    // workable, food and rest full and not falling, only the planned sites and the builder's roof work observed.
    private static RoundsFacts PlannedRoomFacts(PlannedSiteFacts planned, string pawn, Common.ObservationContext context, Obs.ThermalWorker roofWork)
    {
        var tick = (long)context.Tick;
        var held = planned.Sites.SelectMany(s => s.Options).Where(o => o.Input.Length > 0 && o.AvailableInput.IsKnown).DistinctBy(o => o.Input)
            .GroupBy(o => o.Resource).Select(g => new Amount(new Resource(g.Key), (long)g.Sum(o => o.AvailableInput.Value)));
        var facts = new RoundsFacts
        {
            Tick = new(tick),
            Resources = Known(Domain.EquatableArray.From(held)),
            ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
            CurrentConstruction = Known(new CurrentConstruction { Colony = true }),
            BillReservations = Known(default(Domain.EquatableArray<IngredientReservation>)),
            WorkResources = Known<Domain.EquatableArray<Obs.WorkResourcePawn>>([new(pawn, "", 0, "", "", "", 0) { LocalHour = 6 }]),
            WorkAdmission = new()
            {
                Buildings = Known<Domain.EquatableArray<WorkBuildingCost>>([]),
                Recipes = Known<Domain.EquatableArray<ProductionRecipeInput>>([]),
                Bills = Known(default(Domain.EquatableArray<ProductionBatchState>)),
            },
            PlannedSites = Known(planned),
            ForecastObserved = new()
            {
                WorkPawns = Known<Domain.EquatableArray<WorkPawn>>([new() { Id = new(pawn), Available = Known(true), Applies = Known(true),
                    Job = Known(new PawnJob()), Schedule = Known(Domain.EquatableArray.From(Enumerable.Repeat(PolicyConstants.ScheduleAnything, 24))) }]),
                Needs = new() { Tick = tick, Pawns = [new()
                {
                    Pawn = pawn, State = "eligible", Mood = Known(.5), MoodTarget = Known(.5),
                    MoodUnclampedTarget = Known(.5), LocalHour = Known(6d), Awake = Known(true),
                    MoodRisePerTick = Known(.12 / GenDate.TicksPerHour), MoodFallPerTick = Known(.08 / GenDate.TicksPerHour),
                    MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
                    ConditionalThoughts = Known<Domain.EquatableArray<Obs.NeedConditionalThought>>([]),
                    Memories = Known<Domain.EquatableArray<Obs.NeedMemory>>([]),
                    Needs = [new("food", Known(1d), Known(0d), Known(.3), Known(1d)), new("rest", Known(1d), Known(0d), Known(.3), Known(1d))],
                }] },
                Coupled = new()
                {
                    Tick = tick,
                    Food = new() { Context = context, Known = true },
                    Construction = new() { Context = context, Known = true },
                    Thermal = new() { Workers = [roofWork] },
                },
            },
        };
        facts = PolicyFunctions.ReviewWorkResources(facts, Known<Domain.EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = PolicyFunctions.GenerateCoupledActivities(facts, GenDate.TicksPerDay) };
    }
}
