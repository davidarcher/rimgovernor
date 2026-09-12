using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class ComfortFixture
    {
        [Tool("test/comfort_foothold", Description = "UNSAFE FOR MODEL EXECUTION. Prepare a disposable existing roofed room, ordinary sleeping/cooking/storage, food, rice field and healthy workers so optional comfort can be tested after startup. Does not create tables, chairs, recreation furniture or their use.")]
        public async Task<object> Foothold(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var center = new IntVec3(x, 0, z);
                var room = center.GetRoom(map);
                if (room == null || !room.ProperRoom || room.OpenRoofCount != 0) throw new InvalidOperationException("Existing roofed fixture room required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count < 1 || people.Count > 4) throw new InvalidOperationException("Require 1..4 fixture colonists.");
                var requiredConstruction = new[] { "Table1x2c", "DiningChair", "HorseshoesPin" }
                    .Select(d => DefDatabase<ThingDef>.GetNamed(d).constructionSkillPrerequisite).Max();
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) throw new InvalidOperationException("Fixture needs a construction-capable pawn.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(requiredConstruction, builder.skills.GetSkill(SkillDefOf.Construction).Level);
                var free = room.Cells.Where(c => c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null)
                    .OrderByDescending(c => c.z).ThenBy(c => c.x).ToList();
                if (free.Count < 20) throw new InvalidOperationException("Fixture room lacks service space.");
                var index = 0;
                foreach (var pawn in people) {
                    foreach (var h in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(h);
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                    pawn.needs.food.CurLevelPercentage = .8f;
                    pawn.needs.rest.CurLevelPercentage = .95f;
                    pawn.needs.joy.CurLevelPercentage = .5f;
                    for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    if (pawn.equipment.Primary == null) {
                        var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
                        pawn.equipment.AddEquipment(weapon);
                    }
                    var bed = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("SleepingSpot"));
                    var bedCell = free.First(c => GenAdj.OccupiedRect(c, Rot4.North, bed.def.size).All(free.Contains));
                    bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, bedCell, map, Rot4.North);
                    free.RemoveAll(c => bed.OccupiedRect().Contains(c));
                }
                var cooker = (Building_WorkTable)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Campfire"));
                cooker.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(cooker, free[index++], map);
                var fuel = cooker.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
                var bill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("CookMealSimple").MakeNewBill();
                bill.repeatMode = BillRepeatModeDefOf.RepeatCount; bill.repeatCount = 1;
                cooker.BillStack.AddBill(bill);
                var mealDef = DefDatabase<ThingDef>.GetNamed("MealSurvivalPack");
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone); zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(mealDef, true);
                for (int i = 0; i < 9; i++) {
                    var cell = free[index++]; zone.AddCell(cell);
                    var meal = ThingMaker.MakeThing(mealDef); meal.stackCount = mealDef.stackLimit;
                    GenSpawn.Spawn(meal, cell, map); meal.SetForbidden(false, false);
                }
                foreach (var item in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item).ToList()) item.SetForbidden(false, false);
                for (int i = 0; i < 4; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog); wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, center, map, ThingPlaceMode.Near); wood.SetForbidden(false, false);
                }
                var rice = DefDatabase<ThingDef>.GetNamed("Plant_Rice");
                var farmCells = GenRadial.RadialCellsAround(center, 45, true).Where(c => c.InBounds(map) && !c.Fogged(map)
                    && !c.Roofed(map) && c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null
                    && c.GetTerrain(map).fertility >= rice.plant.fertilityMin).Take(400).ToList();
                if (farmCells.Count != 400) throw new InvalidOperationException("Fixture lacks bounded fertile field.");
                var farm = new Zone_Growing(map.zoneManager); farm.SetPlantDefToGrow(rice); map.zoneManager.RegisterZone(farm);
                foreach (var cell in farmCells) {
                    foreach (var oldPlant in cell.GetThingList(map).OfType<Plant>().ToList()) oldPlant.Destroy(DestroyMode.Vanish);
                    farm.AddCell(cell);
                    var plant = (Plant)ThingMaker.MakeThing(rice); GenSpawn.Spawn(plant, cell, map); plant.Growth = .5f;
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms(); center.GetRoom(map).Temperature = 21f;
                // The bridge assembly can load after RimWorld caches component types.
                var fixture = map.GetComponent<ComfortNeedsFixture>();
                if (fixture == null) {
                    fixture = new ComfortNeedsFixture(map);
                    map.components.Add(fixture);
                }
                fixture.Arm();
                return new { success = true, tick = Find.TickManager.TicksGame, colonists = people.Count,
                    requiredConstruction, builder = builder.GetUniqueLoadID(),
                    fieldCells = farmCells.Count, comfort = ComfortFacts.Read(map) };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/comfort_inspect", Description = "Read disposable comfort fixture trigger and native recreation access without changing needs or jobs.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var fixture = map.GetComponent<ComfortNeedsFixture>();
                return new { success = true, tick = Find.TickManager.TicksGame, fixture.Armed, fixture.TriggerTick,
                    fixture.TriggerCount, fixture.Facilities,
                    recreation = map.listerBuildings.allBuildingsColonist.Where(b => b.def.building.joyKind != null)
                        .Select(b => new { id = b.GetUniqueLoadID(),
                            watchCells = WatchBuildingUtility.CalculateWatchCells(b.def, b.Position, b.Rotation, map)
                                .Select(c => new { x = c.x, z = c.z }).ToList(),
                            people = map.mapPawns.FreeColonistsSpawned.Select(p => new {
                                id = p.GetUniqueLoadID(), joy = p.needs.joy.CurLevelPercentage,
                                canWatch = WatchBuildingUtility.TryFindBestWatchCell(b, p, false, out _, out _),
                                job = p.CurJob?.def.defName, target = p.CurJob?.targetA.Thing?.GetUniqueLoadID()
                            }).ToList() }).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/comfort_inputs", Description = "Supply disposable construction wood, or prepare hunger/recreation needs after furniture construction. Does not place furniture or order its use.")]
        public async Task<object> Inputs(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Prepare needs only after ordinary furniture construction.", DefaultValue = false)] bool needs = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                var anchor = map.listerBuildings.allBuildingsColonist.First(b => b.def == ThingDefOf.Wall).Position;
                var resource = needs ? ThingDefOf.MealSimple : ThingDefOf.WoodLog;
                for (int i = 0; i < 4; i++) {
                    var item = ThingMaker.MakeThing(resource);
                    item.stackCount = resource.stackLimit;
                    GenPlace.TryPlaceThing(item, anchor, map, ThingPlaceMode.Near);
                    item.SetForbidden(false, false);
                }
                foreach (var p in people) {
                    p.needs.joy.CurLevelPercentage = needs ? .05f : .95f;
                    if (needs) {
                        p.needs.food.CurLevelPercentage = .1f;
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
                return new { success = true, needs, people = people.Select(p => p.GetUniqueLoadID()).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Test-only need preparation fires once after ordinary construction, without
    // a second bridge connection or ordering dining/recreation jobs.
    public sealed class ComfortNeedsFixture : MapComponent
    {
        public bool Armed { get; private set; }
        public int TriggerTick { get; private set; }
        public int TriggerCount { get; private set; }
        public string[] Facilities { get; private set; } = Array.Empty<string>();
        public ComfortNeedsFixture(Map map) : base(map) { }
        public void Arm()
        {
            if (Armed || TriggerCount != 0) throw new InvalidOperationException("Fresh comfort fixture required.");
            Armed = true;
        }
        public override void MapComponentTick()
        {
            if (!Armed) return;
            var definitions = new[] { "Table1x2c", "DiningChair", "HorseshoesPin" };
            var buildings = definitions.Select(d => map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == d)).ToList();
            if (buildings.Any(b => b == null)) return;
            Armed = false;
            TriggerTick = Find.TickManager.TicksGame;
            TriggerCount++;
            Facilities = buildings.Select(b => b.GetUniqueLoadID()).ToArray();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                pawn.needs.food.CurLevelPercentage = .1f;
                pawn.needs.joy.CurLevelPercentage = .05f;
                pawn.needs.rest.CurLevelPercentage = .95f;
            }
        }
    }
}
