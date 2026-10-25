using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Cell = RimGovernor.Brain.Domain.Cell;
using MapId = RimGovernor.Brain.Domain.MapId;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace HomeBridge.BridgeTools
{
    internal static class NativeSiteExtraction
    {
        // The able crew and the vanilla reform dialog's view of them, read whole; a food estimate the dialog cannot give throws.
        internal static Obs.QuestSiteExtraction Read(Map map)
        {
            var crew = Dialog_FormCaravan.AllSendablePawns(map, reform: true).Where(p => p.IsFreeColonist && p.Spawned && !p.Dead && !p.Downed && !p.InMentalState).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            if (crew.Count == 0) return new Obs.QuestSiteExtraction.NoCrew();
            var exits = CellRect.WholeMap(map).EdgeCells.Where(c => map.exitMapGrid.IsExitCell(c)
                && crew.All(p => p.CanReach(c, PathEndMode.OnCell, Danger.Deadly))).OrderBy(c => crew.Sum(p => p.Position.DistanceToSquared(c))).ThenBy(c => c.x).ThenBy(c => c.z)
                .Select(c => new Cell(c.x, c.z)).ToList();
            var dialog = NativeCaravanCatalog.BuildDialog(map, reform: true);
            foreach (var group in dialog.transferables)
                if (group.AnyThing is Pawn pawn && crew.Contains(pawn)) group.ForceToDestination(1);
                else if (!(group.AnyThing is Pawn)) group.ForceToDestination(group.things.Where(t => crew.Any(p => p.inventory?.innerContainer.Contains(t) == true || p.carryTracker?.CarriedThing == t)).Sum(t => t.stackCount));
            NativeCaravanCatalog.Call(dialog, "Notify_TransferablesChanged");
            var estimate = NativeCaravanCatalog.FoodDays(dialog);
            double food = estimate.days;
            if (!float.IsNaN(estimate.tillRot) && estimate.tillRot >= 0) food = System.Math.Min(food, estimate.tillRot);
            if (double.IsNaN(food) || double.IsInfinity(food) || food < 0)
                throw new InvalidOperationException("The reform dialog gave no food-days estimate for the crew on map " + map.uniqueID + ".");
            var cargo = new List<Obs.QuestSiteCargo>();
            foreach (var thing in dialog.transferables.Where(t => !(t.AnyThing is Pawn)).SelectMany(t => t.things).Distinct().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                if (thing.def.category != ThingCategory.Item || thing.stackCount <= 0) continue;
                var held = crew.Any(p => p.inventory?.innerContainer.Contains(thing) == true || p.carryTracker?.CarriedThing == thing);
                if (!held && (!thing.Spawned || thing.Position.Fogged(map) || thing.IsForbidden(Faction.OfPlayer)
                    || !crew.Any(p => p.CanReach(thing, PathEndMode.Touch, Danger.None)))) continue;
                var eaten = thing.TryGetComp<CompRottable>() == null && FoodSupplyFacts.SharedFood(thing) && !thing.def.IsMeat && !HumanFoodFacts.ContainsHumanMeat(thing) && thing.def.ingestible != null
                    && thing.def.ingestible.preferability >= FoodPreferability.MealAwful && crew.All(p => FoodUtility.WillEat(p, thing));
                cargo.Add(new Obs.QuestSiteCargo { Id = thing.GetUniqueLoadID(), Def = thing.def.defName, Count = thing.stackCount,
                    UnitMass = thing.GetStatValue(StatDefOf.Mass), MarketValue = thing.MarketValue, Held = held,
                    Nutrition = eaten ? crew.Min(p => FoodUtility.NutritionForEater(p, thing)) : 0 });
            }
            var routes = new List<Obs.QuestSiteHomeRoute>();
            foreach (var home in Find.Maps.Where(m => map.Tile.Valid && map.Tile.Layer.IsRootSurface && m.IsPlayerHome && m.Tile.Valid && m.Tile.Layer == map.Tile.Layer).OrderBy(m => m.uniqueID))
            {
                using var path = map.Tile.Layer.Pather.FindPath(map.Tile, home.Tile, null);
                Obs.SiteTravel travel = path.Found
                    ? new Obs.SiteTravel.Reachable(CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, home.Tile, path, 0,
                        crew.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1)), GenTicks.TicksAbs))
                    : new Obs.SiteTravel.Unreachable();
                routes.Add(new Obs.QuestSiteHomeRoute { Map = new MapId(home.uniqueID), Tile = home.Tile.tileId, Travel = travel });
            }
            return new Obs.QuestSiteExtraction.Crew(new Obs.ExtractionCrew
            {
                CanReform = map.Parent.GetComponent<FormCaravanComp>()?.CanReformNow() == true,
                Crew = [.. crew.Select(p => new PawnId(p.GetUniqueLoadID()))],
                CarryCapacity = dialog.MassCapacity,
                CarriedMass = crew.Sum(p => MassUtility.GearAndInventoryMass(p)),
                InventoryFoodDays = food,
                CrewNutritionPerDay = crew.Where(p => p.needs?.food != null).Sum(p => GameTime.PerDay(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true))),
                Cargo = [.. cargo],
                HomeRoutes = [.. routes],
                ExitCells = [.. exits],
            });
        }
    }
}
