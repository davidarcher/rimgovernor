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
    internal static Obs.CoupledForecastRead Read(Map map, Common.ObservationContext context, Obs.NeedsForecastRead? needs = null)
    {
        var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.HarvestableNow
            && p.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
        var sites = map.listerThings.AllThings.Where(t => t is IConstructible && t.Faction == Faction.OfPlayer
            && t.def.entityDefToBuild is ThingDef).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
        var food = NativeFoodActivity.Read(new(context.Identity, EquatableArray.From(plants.Take(16).Select(p => p.GetUniqueLoadID()))));
        var construction = NativeConstructionActivity.Read(new(context.Identity, EquatableArray.From(sites.Take(16).Select(t =>
            new RimGovernor.Brain.Domain.Building(t.def.entityDefToBuild.defName, new(t.Position.x, t.Position.z),
                (Rotation)t.Rotation.AsInt, t.Stuff?.defName ?? "")))));
        var clothing = NativeRecipeActivity.Read(new(context.Identity));
        var future = NativeCoupledFood.Read(map, food);
        var thermal = NativeCoupledThermal.Read(map, sites.Take(16).ToArray());
        needs ??= NativeNeedsForecast.Read(map,map.mapPawns.FreeColonistsSpawned.ToList());
        var families = new[] {
            food.Options.SelectMany(o => new[] { o.Origin,o.Destination }),
            construction.Options.SelectMany(o => new[] { o.Origin,o.Location }),
            future.Conversions.SelectMany(o => new[] { o.SourceCell,o.BenchCell,o.Destination }),
            clothing.Options.SelectMany(o => new[] { o.Location,o.Destination }.Concat(o.Inputs.Select(i => i.Location))),
            needs.Pawns.SelectMany(p => p.Targets).Where(t => t.Location is not null).Select(t => t.Location!.Value),
            thermal.Cells.Where(c => c.BuildRoof && c.Roof == "none").Select(c => c.Cell)
        };
        // Interleave family locations before bounding; a large ingredient census must not erase every roof/need route.
        var locations = families.SelectMany(f => f.Distinct().OrderBy(c => c.X).ThenBy(c => c.Z).Select((cell,index) => (Cell: cell,Index: index)))
            .OrderBy(p => p.Index).Select(p => p.Cell).Distinct().Take(33).ToArray();
        var routes = new List<Obs.CoupledRoute>(); var budget = 256;
        var truncated = thermal.Truncated || plants.Length > 16 || sites.Length > 16 || locations.Length > 32;
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(16).ToArray();
        // Interleave destinations/pairs across the roster; one pawn cannot consume every helper's route budget.
        foreach (var to in locations.Take(8))
        foreach (var pawn in pawns)
            Probe(pawn, new(pawn.Position.x, pawn.Position.z), to, true);
        foreach (var from in locations.Take(32))
        foreach (var to in locations.Take(32).Where(c => c != from))
        foreach (var pawn in pawns) Probe(pawn, from, to, false);
        void Probe(Pawn pawn, Cell from, Cell to, bool initial)
        {
            if (budget-- <= 0) { truncated = true; return; }
            using var route = map.pathFinder.FindPathNow(new(from.X, 0, from.Z), new IntVec3(to.X, 0, to.Z),
                TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
            routes.Add(new(pawn.GetUniqueLoadID(), from, to, route.Found ? Fact.Known((double)route.TotalCost) : default) { FromStart = initial });
        }        return new() { Tick = context.Tick, Thermal = thermal, Food = food, Construction = construction, Clothing = clothing,
            FoodSources = EquatableArray.From(future.Sources), FoodConversions = EquatableArray.From(future.Conversions), FoodUses = EquatableArray.From(future.Uses),
            Routes = EquatableArray.From(routes), Truncated = future.Truncated || truncated || food.Truncated || construction.Truncated || clothing.Truncated,
            Findings = ["bounded_routes_missing_pairs_unknown"] };
    }
}
