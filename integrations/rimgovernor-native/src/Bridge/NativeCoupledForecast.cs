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
        // Each sub-read as its own cf.fc.* span, with the path searches it added as rows; together they are the coupled forecast.
        var searched = 0;
        void Lap(string name) { var now = System.Diagnostics.Stopwatch.GetTimestamp(); ObservationWork.Detail("cf.fc." + name, now - lap, legs.Searches - searched); lap = now; searched = legs.Searches; }
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
        var hunt = NativeHuntActivity.Read(map); Lap("hunt");
        needs ??= NativeNeedsForecast.Read(map, map.mapPawns.FreeColonistsSpawned.ToList(), legs);
        var families = new[] {
            practice.Workers.Select(w => w.Location),
            food.Options.SelectMany(o => new[] { o.Origin,o.Destination }),
            construction.Options.SelectMany(o => new[] { o.Origin,o.Location }),
            future.Conversions.SelectMany(o => new[] { o.SourceCell,o.BenchCell,o.Destination }),
            clothing.Options.SelectMany(o => new[] { o.Location,o.Destination }.Concat(o.Inputs.Select(i => i.Location))),
            needs.Pawns.SelectMany(p => p.Targets).Where(t => t.Location is not null).Select(t => t.Location!.Value),
            thermal.Cells.Where(c => c.BuildRoof && c.Roof == "none").Select(c => c.Cell),
            support.Workers.Select(w => w.Location).Concat(support.Deliveries.SelectMany(d => new[] { d.Source, d.Destination }))
        };
        var locations = families.SelectMany(f => f).Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).ToArray(); Lap("locations");
        var routes = new List<Obs.CoupledRoute>();
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var observed = new HashSet<(string Pawn, Cell From, Cell To, bool Initial)>();
        // Service dependencies: each demand's filth cells to and from the demand, and between each other.
        foreach (var demand in support.Demands)
        {
            var cells = support.Filth.Where(f => f.Demand == demand.Id).Select(f => f.Location).Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
            foreach (var cell in cells)
            foreach (var pawn in pawns)
            {
                Probe(pawn, cell, demand.Location, false);
                Probe(pawn, demand.Location, cell, false);
            }
            foreach (var from in cells)
            foreach (var to in cells.Where(c => c != from))
            foreach (var pawn in pawns)
                Probe(pawn, from, to, false);
        }
        foreach (var to in locations)
        foreach (var pawn in pawns)
            Probe(pawn, new(pawn.Position.x, pawn.Position.z), to, true);
        // Each available hunter to every prey: a hunt roster is priced by these walks.
        var huntIds = hunt.Hunters.Where(h => h.Available).Select(h => h.Pawn).ToHashSet(StringComparer.Ordinal);
        foreach (var prey in hunt.Prey)
        foreach (var pawn in pawns.Where(p => huntIds.Contains(p.GetUniqueLoadID())))
            Probe(pawn, new(pawn.Position.x, pawn.Position.z), prey.Location, true);
        foreach (var from in locations)
        foreach (var to in locations.Where(c => c != from))
        foreach (var pawn in pawns)
            Probe(pawn, from, to, false);
        Lap("routes");
        void Probe(Pawn pawn, Cell from, Cell to, bool initial)
        {
            if (!observed.Add((pawn.GetUniqueLoadID(), from, to, initial))) return;
            var ticks = legs.Cost(pawn, new(from.X, 0, from.Z), new IntVec3(to.X, 0, to.Z), PathEndMode.OnCell);
            routes.Add(new(pawn.GetUniqueLoadID(), from, to, ticks is { } t ? Fact.Known((double)t) : default) { FromStart = initial });
        }
        return new() { Tick = context.Tick, Practice = practice, Hunt = hunt, Support = support, Thermal = thermal, Food = food, Construction = construction, Clothing = clothing,
            FoodSources = EquatableArray.From(future.Sources), FoodConversions = EquatableArray.From(future.Conversions), FoodUses = EquatableArray.From(future.Uses),
            Routes = EquatableArray.From(routes) };
    }
}
