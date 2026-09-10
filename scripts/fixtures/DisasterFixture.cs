using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // Disposable environmental inputs only; never included in production builds.
    public sealed class DisasterFixture
    {
        private static bool configured;
        private static List<Thing> fuelSupplies = new List<Thing>();

        [Tool("test/disaster_supplies", Description = "Change fixture-only fuel accessibility in the disposable acceptance colony.")]
        public async Task<object> Supplies(IRimBridgeContext ctx, CancellationToken cancellationToken, bool available)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                foreach (var t in fuelSupplies.Where(t => t.Spawned)) t.SetForbidden(!available, false);
                return new { success = true, available, count = fuelSupplies.Count(t => t.Spawned) };
            }, cancellationToken);

        [Tool("test/disaster_compound", Description = "Seed a disposable compound crop, fuel and infrastructure disruption. All subsequent repair/refuel/sowing work remains native.")]
        public async Task<object> Compound(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                if (configured) throw new InvalidOperationException("Fixture already configured");
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var origin = GenRadial.RadialCellsAround(pawn.Position, 70, true).First(c =>
                    new CellRect(c.x, c.z, 14, 10).Cells.All(p => p.InBounds(map) && !p.Fogged(map)
                        && p.Standable(map) && p.GetEdifice(map) == null && p.GetZone(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                Thing Spawn(string name, int x, int z) {
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(Faction.OfPlayer);
                    return GenSpawn.Spawn(thing, new IntVec3(origin.x+x, 0, origin.z+z), map);
                }
                foreach (var c in new CellRect(origin.x, origin.z, 14, 10).Cells) map.areaManager.Home[c] = true;
                var walls = new List<Thing>();
                for (int x = 0; x < 7; x++) for (int z = 0; z < 7; z++)
                {
                    var c = new IntVec3(origin.x+x, 0, origin.z+z);
                    if (x == 0 || z == 0 || x == 6 || z == 6)
                        walls.Add(Spawn(x == 3 && z == 6 ? "Door" : "Wall", x, z));
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[c] = true;
                }
                map.areaManager.TryMakeNewAllowed(out var refuge);
                refuge.SetLabel("Disaster fixture roofed refuge");
                for (int x = 1; x < 6; x++) for (int z = 1; z < 6; z++)
                    refuge[new IntVec3(origin.x+x, 0, origin.z+z)] = true;
                int sheltered = 1;
                foreach (var person in map.mapPawns.FreeColonistsSpawned.Take(2))
                    person.Position = new IntVec3(origin.x+sheltered++, 0, origin.z+1);
                var generator = Spawn("WoodFiredGenerator", 8, 1);
                generator.TryGetComp<CompRefuelable>().ConsumeFuel(generator.TryGetComp<CompRefuelable>().Fuel);
                var stove = Spawn("ElectricStove", 4, 3);
                for (int x = 5; x <= 6; x++) Spawn("PowerConduit", x, 1);
                var fire = Spawn("Campfire", 2, 3);
                fire.TryGetComp<CompRefuelable>().ConsumeFuel(fire.TryGetComp<CompRefuelable>().Fuel);
                var damaged = walls.First();
                var hp = damaged.HitPoints;
                damaged.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 40));
                generator.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 50));
                var zone = new Zone_Growing(map.zoneManager) { label = "Disaster recovery rice" };
                var riceDef = DefDatabase<ThingDef>.GetNamed("Plant_Rice");
                BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").SetValue(zone, riceDef);
                map.zoneManager.RegisterZone(zone);
                int lost = 0;
                for (int x = 8; x < 14; x++) for (int z = 4; z < 10; z++)
                {
                    var c = new IntVec3(origin.x+x, 0, origin.z+z);
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    var plant = c.GetPlant(map);
                    plant?.Destroy();
                    zone.AddCell(c);
                    var rice = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(riceDef), c, map);
                    rice.Growth = .8f;
                    rice.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 1000));
                    if (!rice.Spawned) lost++;
                }
                fuelSupplies = map.listerThings.AllThings.Where(t => t.def == ThingDefOf.WoodLog).ToList();
                foreach (var t in fuelSupplies) t.SetForbidden(true, false);
                var flare = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("SolarFlare"), 60000);
                map.gameConditionManager.RegisterCondition(flare);
                var toxic = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"), 1200);
                map.gameConditionManager.RegisterCondition(toxic);
                configured = true;
                return new { success = true, generator = generator.GetUniqueLoadID(), stove = stove.GetUniqueLoadID(),
                    campfire = fire.GetUniqueLoadID(), wall = damaged.GetUniqueLoadID(), wallBefore = hp, wallAfter = damaged.HitPoints,
                    cropLoss = lost, zoneId = zone.ID, refuge = refuge.ID, pawn = pawn.GetUniqueLoadID(),
                    tick = Find.TickManager.TicksGame, setup = "Test-only shelter, two sheltered pawn positions, soil plot, compound damage and crop destruction; no repair, refuel, sowing or service restoration injected" };
            }, cancellationToken);

        [Tool("test/disaster_setup", Description = "Seed a powerless stove and bounded solar flare in a disposable test colony.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (configured) throw new InvalidOperationException("Fixture already configured");
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Load a disposable colony first");
                var definition = DefDatabase<GameConditionDef>.GetNamed("SolarFlare");
                var eclipseDef = DefDatabase<GameConditionDef>.GetNamed("Eclipse");
                var stoveDef = DefDatabase<ThingDef>.GetNamed("ElectricStove");
                var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
                var cell = GenRadial.RadialCellsAround(anchor, 15, true).First(c =>
                    GenAdj.OccupiedRect(c, Rot4.North, stoveDef.size).Cells.All(p =>
                        p.InBounds(map) && !p.Fogged(map) && p.Standable(map)
                        && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                configured = true;
                var stove = ThingMaker.MakeThing(stoveDef);
                stove.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(stove, cell, map);
                var flare = GameConditionMaker.MakeCondition(definition, 600);
                map.gameConditionManager.RegisterCondition(flare);
                var eclipse = GameConditionMaker.MakeCondition(eclipseDef, 600);
                eclipse.Permanent = true;
                map.gameConditionManager.RegisterCondition(eclipse);
                return new { success = true, stove = stove.ThingID, tick = Find.TickManager.TicksGame,
                    temporary = definition.defName, permanent = eclipseDef.defName,
                    setup = "Test-only spawned unpowered stove and registered native conditions. Pawn work and elapsed condition expiry remain native." };
            }, cancellationToken);
        }
    }
}
