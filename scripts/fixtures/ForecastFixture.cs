using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable state setup only, excluded from production and the model gateway.
    public sealed class ForecastFixture
    {
        [Tool("test/routine_temperature_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare sleeping spots and native construction/fuel materials in a disposable roofed room. Initialize the room at actual outdoor temperature once; never create thermal facilities or force pawn construction/refueling jobs.")]
        public async Task<object> RoutineTemperature(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, bool hot = false,
            [ToolParameter(Description = "When the outdoor temperature is too warm for the cold method, register ordinary ColdSnap conditions (ramp already complete) sized on the day's peak so the whole run stays under the cold threshold; no direct temperature edit.", DefaultValue = false)] bool coldSnap = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var coldSnaps = 0;
                if (coldSnap && !hot && map != null && map.mapTemperature.OutdoorTemp >= 12) {
                    var snapDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("ColdSnap");
                    if (snapDef == null) throw new InvalidOperationException("ColdSnap unavailable in this ruleset.");
                    // The sun cycle swings +-7C, so size the snaps on the day's
                    // peak (as FarmEnvironmentFixture does): each snap is -20C.
                    var peak = map.mapTemperature.OutdoorTemp - GenTemperature.OffsetFromSunCycle(Find.TickManager.TicksAbs, map.Tile) + 7f;
                    var snaps = System.Math.Max(1, System.Math.Min(3, (int)System.Math.Ceiling((peak - 4f) / 20f)));
                    for (var i = 0; i < snaps; i++) {
                        var snap = GameConditionMaker.MakeCondition(snapDef, 4 * 60000 + 12000);
                        map.gameConditionManager.RegisterCondition(snap);
                        // RegisterCondition clamps startTick up to now; move it
                        // back afterwards so the ramp-in is already complete.
                        snap.startTick = Find.TickManager.TicksGame - 12000;
                        coldSnaps++;
                    }
                    // The tile temperature cache is keyed on the game tick and
                    // the game is paused: drop it so OutdoorTemp carries the snaps.
                    Find.World.tileTemperatures.ClearCaches();
                }
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable map required.");
                var center = new IntVec3(x, 0, z);
                var room = center.GetRoom(map);
                if (room == null || !room.ProperRoom || room.UsesOutdoorTemperature || room.OpenRoofCount != 0)
                    throw new InvalidOperationException("Prepared enclosed sleeping room required.");
                var cells = room.Cells.ToList();
                if (cells.Count != 25 || map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Any())
                    throw new InvalidOperationException("Require empty 25-cell room without existing player beds.");
                var outdoor = map.mapTemperature.OutdoorTemp;
                if (hot ? outdoor <= 32 : outdoor >= 12) throw new InvalidOperationException("Native outdoor temperature does not require selected thermal method.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => new[] { "Campfire", "PassiveCooler", "Heater", "Cooler" }.Contains(b.def.defName)))
                    throw new InvalidOperationException("Thermal fixture requires no existing thermal buildings.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var definition = ThingDef.Named(hot ? "PassiveCooler" : "Campfire");
                foreach (var project in definition.researchPrerequisites ?? Enumerable.Empty<ResearchProjectDef>())
                    if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) throw new InvalidOperationException("No capable fixture builder.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(definition.constructionSkillPrerequisite, builder.skills.GetSkill(SkillDefOf.Construction).Level);
                var bedDef = ThingDef.Named("SleepingSpot");
                var occupied = new System.Collections.Generic.HashSet<IntVec3>();
                var beds = new System.Collections.Generic.List<string>();
                foreach (var cell in cells.OrderBy(c => c.z).ThenBy(c => c.x)) {
                    var footprint = GenAdj.OccupiedRect(cell, Rot4.North, bedDef.size).Cells.ToList();
                    if (!footprint.All(c => cells.Contains(c) && !occupied.Contains(c))) continue;
                    var bed = ThingMaker.MakeThing(bedDef); bed.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(bed, cell, map, Rot4.North); bed.SetForbidden(false, false);
                    beds.Add(bed.GetUniqueLoadID()); foreach (var c in footprint) occupied.Add(c);
                    if (beds.Count == people.Count) break;
                }
                if (beds.Count != people.Count) throw new InvalidOperationException("Insufficient fixture sleeping footprints.");
                var supplies = definition.CostListAdjusted(null, false).ToList();
                supplies.Add(new ThingDefCountClass(ThingDefOf.WoodLog, 100));
                var stockCells = GenRadial.RadialCellsAround(center, 12, true).Where(c => c.InBounds(map) && c.Standable(map) && !cells.Contains(c)
                    && c.GetThingList(map).All(t => t is Plant) && map.zoneManager.ZoneAt(c) == null).Take(16).ToList();
                var slot = 0;
                foreach (var cost in supplies.GroupBy(c => c.thingDef)) {
                    var remaining = checked(cost.Sum(c => c.count) * 2);
                    while (remaining > 0) {
                        if (slot >= stockCells.Count) throw new InvalidOperationException("Insufficient fixture material cells.");
                        var stack = ThingMaker.MakeThing(cost.Key); stack.stackCount = Math.Min(stack.def.stackLimit, remaining); remaining -= stack.stackCount;
                        GenSpawn.Spawn(stack, stockCells[slot++], map); stack.SetForbidden(false, false);
                    }
                }
                // Only initial conditions are seeded. Seasonal native weather and
                // normal simulation determine every subsequent room temperature.
                room.Temperature = outdoor;
                return new { success = true, hot, definition = definition.defName, beds = beds.ToArray(),
                    outdoorTemperature = outdoor, coldSnaps, roomTemperature = room.Temperature, requiredConstruction = definition.constructionSkillPrerequisite,
                    cells = cells.Select(c => new { x = c.x, z = c.z }).ToArray(), setupOnly = true };
            }, cancellationToken);
        }
    }
}
