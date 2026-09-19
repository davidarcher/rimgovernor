using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages the late-summer precondition
    // for sustained/winter (#251): the loaded colony's calendar is moved to
    // the last hours of the tile's growing season, and its larder is stocked
    // to a nutrition the case names, so a serve run opens on a colony that
    // has done its summer's work and the winter that follows is the
    // stretch under test. Nothing here plans, orders or feeds anything.
    public sealed class WinterFixture
    {
        [Tool("test/winter_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: move the loaded colony's calendar (the game's absolute tick origin, never its game tick) forward to hoursBeforeFrost hours before the tile's seasonal temperature next leaves the crop growth range, so the next day is the first of the non-growing stretch. Refuses on a tile that never leaves or never enters the range.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int hoursBeforeFrost = 6)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (hoursBeforeFrost < 1 || hoursBeforeFrost > GenDate.HoursPerDay)
                    return Refuse("hoursBeforeFrost must be between 1 and " + GenDate.HoursPerDay + ".");
                if (Find.TickManager.gameStartAbsTick <= 0) return Refuse("The game has no absolute tick origin yet.");
                // Walk the seasonal temperature an hour at a time for one year
                // and land on the first hour that is inside the crop range with
                // the range left hoursBeforeFrost hours later: the daily walk
                // the colony facts read then reports one growing day left
                // and the census flips to a wait within the case's window.
                var abs = Find.TickManager.TicksAbs;
                Func<int, bool> growing = at => {
                    var t = GenTemperature.GetTemperatureFromSeasonAtTile(at, map.Tile);
                    return t >= Plant.DefaultMinOptimalGrowthTemperature && t <= Plant.DefaultMaxOptimalGrowthTemperature;
                };
                var hours = GenDate.DaysPerYear * GenDate.HoursPerDay;
                var shiftHours = -1;
                for (var h = 1; h <= hours && shiftHours < 0; h++)
                    if (h >= hoursBeforeFrost && growing(abs + (h - 1) * GenDate.TicksPerHour) && !growing(abs + h * GenDate.TicksPerHour)
                        && Enumerable.Range(h - hoursBeforeFrost, hoursBeforeFrost).All(k => growing(abs + k * GenDate.TicksPerHour)))
                        shiftHours = h - hoursBeforeFrost;
                if (shiftHours < 0) return Refuse("The tile's seasonal temperature never leaves the crop growth range within a year of now.");
                var shift = shiftHours * GenDate.TicksPerHour;
                Find.TickManager.gameStartAbsTick += shift;
                // The tile temperature cache is keyed on the game tick, which
                // did not move: drop it so every read carries the new season.
                Find.World.tileTemperatures.ClearCaches();
                var calendar = ColonyFactsTools.Calendar(map);
                var remaining = ColonyFactsTools.GrowingDaysRemaining(map);
                var until = ColonyFactsTools.GrowingDaysUntil(map);
                if (remaining != 1 || until != 0)
                    return Refuse("Calendar landed on growing days remaining " + remaining + " / until " + until + ", expected 1 / 0.");
                // The wait the census will report once the frost is in: the
                // same daily walk from the first non-growing day to re-entry.
                var nonGrowingDays = GenDate.DaysPerYear;
                for (var d = 1; d < GenDate.DaysPerYear; d++)
                    if (growing(Find.TickManager.TicksAbs + d * GenDate.TicksPerDay)) { nonGrowingDays = d - 1; break; }
                return new {
                    success = true, tick = Find.TickManager.TicksGame, ticksAbs = Find.TickManager.TicksAbs, shiftedTicks = shift,
                    hoursBeforeFrost, season = calendar.season, dayOfYear = calendar.dayOfYear,
                    growingDaysRemaining = remaining, growingDaysUntil = until, nonGrowingDays,
                    outdoorTemperatureC = map.mapTemperature.OutdoorTemp,
                    seasonalTemperatureC = GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, map.Tile),
                    setup = "Test-only calendar shift; the larder, fields and every order remain the controller's own work.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/winter_stock", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: register one food stockpile zone near the colonist centroid holding at least nutrition units of foodDef (unforbidden, player-owned, full stacks), the larder a colony that met its seasonal food target before the frost would hold. Never touches pawns, time or existing stock.")]
        public async Task<object> Stock(IRimBridgeContext ctx, CancellationToken cancellationToken, double nutrition = 0, string foodDef = "MealSurvivalPack")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!(nutrition > 0) || double.IsInfinity(nutrition)) return Refuse("nutrition must be a positive number.");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(foodDef);
                if (def == null || !def.IsNutritionGivingIngestible || def.IsDrug) return Refuse("foodDef must name an edible definition: " + foodDef);
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) return Refuse("No free colonists on the map.");
                var perUnit = def.GetStatValueAbstract(StatDefOf.Nutrition);
                if (!(perUnit > 0)) return Refuse(foodDef + " carries no nutrition.");
                var units = (int)Math.Ceiling(nutrition / perUnit);
                var stacks = (units + def.stackLimit - 1) / def.stackLimit;
                var side = (int)Math.Ceiling(Math.Sqrt(stacks));
                if (side > 12) return Refuse("Fixture larder would need " + stacks + " stacks; at most 144 fit.");
                // A square clearing near the centroid, inside the 22-cell
                // planning region, on open reachable ground.
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var walker = people.Where(p => !p.Downed).OrderBy(p => p.thingIDNumber).FirstOrDefault() ?? people[0];
                var origin = GenRadial.RadialCellsAround(center, 16, true).FirstOrDefault(c =>
                    c.x >= center.x - 20 && c.x + side - 1 <= center.x + 20 && c.z >= center.z - 20 && c.z + side - 1 <= center.z + 20
                    && new CellRect(c.x, c.z, side, side).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && cell.Standable(map)
                        && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building
                            || t.def.category == ThingCategory.Item || t is Blueprint || t is Frame))
                    && walker.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable " + side + "x" + side + " area near the colonist centroid for the fixture larder.");
                var rect = new CellRect(origin.x, origin.z, side, side);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.settings.filter.SetDisallowAll();
                zone.settings.filter.SetAllow(def, true);
                foreach (var cell in rect.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    zone.AddCell(cell);
                    map.areaManager.Home[cell] = true;
                }
                var placed = new List<object>();
                var cells = rect.Cells.GetEnumerator();
                for (var remaining = units; remaining > 0; remaining -= def.stackLimit)
                {
                    if (!cells.MoveNext()) return Refuse("Fixture larder ran out of cells.");
                    var stack = ThingMaker.MakeThing(def);
                    stack.stackCount = Math.Min(remaining, def.stackLimit);
                    GenSpawn.Spawn(stack, cells.Current, map);
                    stack.SetForbidden(false, false);
                    placed.Add(new { id = stack.GetUniqueLoadID(), count = stack.stackCount, x = cells.Current.x, z = cells.Current.z });
                }
                return new {
                    success = true, tick = Find.TickManager.TicksGame, foodDef, nutritionPerUnit = perUnit, units, stacks = placed.Count,
                    nutritionPlaced = units * perUnit, zoneId = zone.ID,
                    rect = new { minX = rect.minX, minZ = rect.minZ, maxX = rect.maxX, maxZ = rect.maxZ },
                    placed,
                    setup = "Test-only larder; hauling, roofing and every order remain the controller's own work.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
