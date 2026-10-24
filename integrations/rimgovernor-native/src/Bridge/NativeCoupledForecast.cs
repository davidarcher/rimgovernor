#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Common = RimGovernor.Contracts.Wire.Common;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace HomeBridge.BridgeTools;

internal static class NativeCoupledForecast
{
    /// <summary>
    /// The coupled forecast's native rows: every sub-read covers every colonist and candidate, and the routes cover every
    /// pair of locations the sub-reads name, for every free colonist (#3341). One leg cache serves every sub-read, so a
    /// path two of them share is searched once (NativePathLegs).
    /// </summary>
    internal static Obs.CoupledForecastRead Read(Map map, Common.ObservationContext context, Obs.NeedsForecastRead? needs = null)
    {
        var legs = new NativePathLegs(map);
        var lap = System.Diagnostics.Stopwatch.GetTimestamp();
        // Each sub-read as its own cf.fc.* span, with the path searches it added as rows and their time as its .paths
        // span when it searched; together they are the coupled forecast.
        var searched = 0; long searchedTicks = 0;
        void Lap(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            ObservationWork.Detail("cf.fc." + name, now - lap, legs.Searches - searched);
            if (legs.SearchTicks > searchedTicks) ObservationWork.Detail("cf.fc." + name + ".paths", legs.SearchTicks - searchedTicks, legs.Searches - searched);
            lap = now; searched = legs.Searches; searchedTicks = legs.SearchTicks;
        }
        var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.HarvestableNow
            && p.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        // Every placed site is construction work, a floor's terrain blueprint included; only a building's rooms and roofs are thermal.
        var placed = map.listerThings.AllThings.Where(t => t is IConstructible && t.Faction == Faction.OfPlayer
            && t.def.entityDefToBuild is BuildableDef).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var sites = placed.Where(t => t.def.entityDefToBuild is ThingDef).ToArray();
        var food = NativeFoodActivity.Read(new(context.Identity, EquatableArray.From(plants.Select(p => p.GetUniqueLoadID()))), legs); Lap("food");
        var construction = NativeConstructionActivity.Read(new(context.Identity, EquatableArray.From(placed.Select(t =>
            new RimGovernor.Brain.Domain.Building(t.def.entityDefToBuild.defName, new(t.Position.x, t.Position.z),
                (Rotation)t.Rotation.AsInt, t.Stuff?.defName ?? "")))), legs); Lap("construction");
        var clothing = NativeRecipeActivity.Read(context.Identity, legs); Lap("clothing");
        var future = NativeCoupledFood.Read(map, food, legs); Lap("future");
        var thermal = NativeCoupledThermal.Read(map, sites, legs); Lap("thermal");
        var support = NativeSupportActivities.Read(map, legs); Lap("support");
        var practice = NativePracticeActivities.Read(map, legs); Lap("practice");
        var research = NativeResearchActivities.Read(map); Lap("research");
        var hunt = NativeHuntActivity.Read(map); Lap("hunt");
        var sowing = NativeSowingActivities.Read(map, legs); Lap("sowing");
        needs ??= NativeNeedsForecast.Read(map, map.mapPawns.FreeColonistsSpawned.ToList(), legs);
        var families = new[] {
            practice.Workers.Select(w => w.Location),
            research.Workers.Select(w => w.Location),
            food.Options.SelectMany(o => new[] { o.Origin,o.Destination }),
            construction.Options.SelectMany(o => new[] { o.Origin,o.Location }),
            future.Conversions.SelectMany(o => new[] { o.SourceCell,o.BenchCell,o.Destination }),
            clothing.Options.SelectMany(o => new[] { o.Location,o.Destination }.Concat(o.Inputs.Select(i => i.Location))),
            needs.Pawns.SelectMany(p => p.Targets).Select(t => t.Location),
            thermal.Cells.Where(c => c.BuildRoof && c.Roof == "none").Select(c => c.Cell),
            support.Workers.Select(w => w.Location).Concat(support.Deliveries.SelectMany(d => new[] { d.Source, d.Destination }))
        };
        var locations = families.SelectMany(f => f).Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).ToArray(); Lap("locations");
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        // Each pawn's id, cell and move ticks once: a route between two cells is one leg, found once and priced per
        // pawn (NativePathLegs.Found), not a lookup per pawn (#3556). The routes are built batched after the floods: the
        // first run notes the legs the store still lacks, one batched search finds them, the second run prices them.
        var ids = pawns.Select(p => p.GetUniqueLoadID()).ToArray();
        var starts = pawns.Select(p => new Cell(p.Position.x, p.Position.z)).ToArray();
        var moves = pawns.Select(NativePathLegs.MoveTicks).ToArray();
        var huntIds = hunt.Hunters.Where(h => h.Available).Select(h => h.Pawn).ToHashSet(StringComparer.Ordinal);
        var hunters = Enumerable.Range(0, pawns.Length).Where(i => huntIds.Contains(ids[i])).ToArray();
        // The route matrix by one flood per origin (#3685): each pawn's start to every location and its prey, and every
        // location to every other, each origin flooded for the first pawn that asks its legs, as the batched read would
        // search them. The rows below then find these legs in the store; the hunt's legs from the prey are searched.
        var origins = new Dictionary<IntVec3, (Pawn Pawn, List<IntVec3> To)>();
        void Want(int i, Cell from, Cell to)
        {
            var cell = new IntVec3(from.X, 0, from.Z);
            if (!origins.TryGetValue(cell, out var origin)) origins[cell] = origin = (pawns[i], new());
            origin.To.Add(new IntVec3(to.X, 0, to.Z));
        }
        for (var i = 0; i < pawns.Length; i++)
            foreach (var to in locations) Want(i, starts[i], to);
        foreach (var prey in hunt.Prey)
            foreach (var i in hunters) Want(i, starts[i], prey.Location);
        if (pawns.Length > 0)
            foreach (var from in locations)
                foreach (var to in locations)
                    if (to != from) Want(0, from, to);
        var flooded = legs.FloodCount; var floodTicks = legs.FloodTicks;
        legs.Floods(origins.Select(o => (o.Value.Pawn, o.Key, (IReadOnlyList<IntVec3>)o.Value.To)).ToList());
        ObservationWork.Detail("cf.fc.routes.floods", legs.FloodTicks - floodTicks, legs.FloodCount - flooded);        var routes = legs.Batched(() =>
        {
            var rows = new List<Obs.CoupledRoute>();
            var observed = new HashSet<(int Pawn, Cell From, Cell To, bool Initial)>();
            NativePathLegs.Found? Leg(int i, Cell from, Cell to) => legs.Find(pawns[i], new(from.X, 0, from.Z), new IntVec3(to.X, 0, to.Z), PathEndMode.OnCell);
            void Row(int i, Cell from, Cell to, bool initial, NativePathLegs.Found? leg) =>
                rows.Add(new(ids[i], from, to, leg is { } found ? new Obs.RouteTicks.Reachable(found.For(moves[i])) : Obs.RouteTicks.None) { FromStart = initial });
            // One pawn over the leg between two cells.
            void One(int i, Cell from, Cell to) { if (observed.Add((i, from, to, false))) Row(i, from, to, false, Leg(i, from, to)); }
            // One pawn over the leg from where it stands.
            void FromStart(int i, Cell to) { if (observed.Add((i, starts[i], to, true))) Row(i, starts[i], to, true, Leg(i, starts[i], to)); }
            // Every pawn over the leg between two cells: found once, priced per pawn.
            void Everyone(Cell from, Cell to)
            {
                var found = false; NativePathLegs.Found? leg = null;
                for (var i = 0; i < pawns.Length; i++)
                {
                    if (!observed.Add((i, from, to, false))) continue;
                    if (!found) { leg = Leg(i, from, to); found = true; }
                    Row(i, from, to, false, leg);
                }
            }
            foreach (var to in locations)
            for (var i = 0; i < pawns.Length; i++)
                FromStart(i, to);
            // Each available hunter to every prey, then from the prey back: with its corpse to the corpse store (a gunner's or
            // a hauler's haul trip) and empty to where the pawn stands (a gunner's return when a retriever takes the corpse).
            // A hunt roster is priced by these walks (#3513).
            foreach (var prey in hunt.Prey)
            foreach (var i in hunters)
            {
                FromStart(i, prey.Location);
                if (prey.CorpseStore is Obs.HuntCorpseStore.Store store) One(i, prey.Location, store.Cell);
                One(i, prey.Location, starts[i]);
            }
            foreach (var from in locations)
            foreach (var to in locations)
                if (to != from) Everyone(from, to);
            return rows;
        });
        Lap("routes");
        return new() { Tick = context.Tick, Practice = practice, Research = research, Hunt = hunt, Sowing = sowing, Support = support, Thermal = thermal, Food = food, Construction = construction, Clothing = clothing,
            FoodSources = EquatableArray.From(future.Sources), FoodConversions = EquatableArray.From(future.Conversions), FoodUses = EquatableArray.From(future.Uses),
            Routes = EquatableArray.From(routes) };
    }
}
