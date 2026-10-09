#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeSiteExtraction
    {
        internal static Obs.QuestSiteExtraction Read(Map map)
        {
            var row = new Obs.QuestSiteExtraction { CanReform = map.Parent.GetComponent<FormCaravanComp>()?.CanReformNow() == true };
            var crew = Dialog_FormCaravan.AllSendablePawns(map, reform: true).Where(p => p.IsFreeColonist && p.Spawned && !p.Dead && !p.Downed && !p.InMentalState).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            row.CrewIds.Add(crew.Select(p => p.GetUniqueLoadID()));
            if (crew.Count == 0) return row;
            foreach (var cell in CellRect.WholeMap(map).EdgeCells.Where(c => map.exitMapGrid.IsExitCell(c)
                && crew.All(p => p.CanReach(c, PathEndMode.OnCell, Danger.Deadly))).OrderBy(c => crew.Sum(p => p.Position.DistanceToSquared(c))).ThenBy(c => c.x).ThenBy(c => c.z))
                row.ExitCells.Add(new RimGovernor.Protocol.Common.Cell { X = cell.x, Z = cell.z });
            var dialog = NativeCaravanCatalog.BuildDialog(map, reform: true);
            foreach (var group in dialog.transferables)
                if (group.AnyThing is Pawn pawn && crew.Contains(pawn)) group.ForceToDestination(1);
                else if (!(group.AnyThing is Pawn)) group.ForceToDestination(group.things.Where(t => crew.Any(p => p.inventory?.innerContainer.Contains(t) == true || p.carryTracker?.CarriedThing == t)).Sum(t => t.stackCount));
            NativeCaravanCatalog.Call(dialog, "Notify_TransferablesChanged");
            row.CarryCapacity = dialog.MassCapacity;
            row.CarriedMass = crew.Sum(p => MassUtility.GearAndInventoryMass(p));
            row.CrewNutritionPerDay = crew.Where(p => p.needs?.food != null).Sum(p => GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true)));
            var estimate = NativeCaravanCatalog.FoodDays(dialog);
            var food = estimate.days;
            if (!float.IsNaN(estimate.tillRot) && estimate.tillRot >= 0) food = System.Math.Min(food, estimate.tillRot);
            if (!float.IsNaN(food) && !float.IsInfinity(food) && food >= 0) row.InventoryFoodDays = food;
            foreach (var thing in dialog.transferables.Where(t => !(t.AnyThing is Pawn)).SelectMany(t => t.things).Distinct().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                if (thing.def.category != ThingCategory.Item || thing.stackCount <= 0) continue;
                var held = crew.Any(p => p.inventory?.innerContainer.Contains(thing) == true || p.carryTracker?.CarriedThing == thing);
                if (!held && (!thing.Spawned || thing.Position.Fogged(map) || thing.IsForbidden(Faction.OfPlayer)
                    || !crew.Any(p => p.CanReach(thing, PathEndMode.Touch, Danger.None)))) continue;
                var cargo = new Obs.QuestSiteCargo { Id = thing.GetUniqueLoadID(), Def = thing.def.defName, Count = thing.stackCount,
                    UnitMass = thing.GetStatValue(StatDefOf.Mass), MarketValue = thing.MarketValue, Held = held };
                cargo.Nutrition = 0;
                if (thing.TryGetComp<CompRottable>() == null && FoodSupplyFacts.SharedFood(thing) && !thing.def.IsMeat && !HumanFoodFacts.ContainsHumanMeat(thing) && thing.def.ingestible != null
                    && thing.def.ingestible.preferability >= FoodPreferability.MealAwful && crew.All(p => FoodUtility.WillEat(p, thing)))
                    cargo.Nutrition = crew.Min(p => FoodUtility.NutritionForEater(p, thing));
                row.Cargo.Add(cargo);
            }
            foreach (var home in Find.Maps.Where(m => map.Tile.Valid && map.Tile.Layer.IsRootSurface && m.IsPlayerHome && m.Tile.Valid && m.Tile.Layer == map.Tile.Layer).OrderBy(m => m.uniqueID))
            {
                var route = new Obs.QuestSiteHomeRoute { MapId = home.uniqueID, Tile = home.Tile.tileId };
                using var path = map.Tile.Layer.Pather.FindPath(map.Tile, home.Tile, null);
                route.Reachable = path.Found;
                if (path.Found) route.TravelTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, home.Tile, path, 0,
                    crew.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1)), GenTicks.TicksAbs);
                row.HomeRoutes.Add(route);
            }
            return row;
        }
    }
}
