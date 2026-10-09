using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages a small growing zone
    // that grows oak near the colonists with one sown oak at growth 0.8 and
    // one rice plant (a crop) at growth 0.9 standing in it, so the colony
    // read's acquisition census can be checked: the oak is a plantation row
    // carrying its growth fraction and can be designated, the rice never
    // becomes a row. Nothing here designates a cut: that is the service's
    // own write.
    public sealed class PlantationFixture
    {
        [Tool("test/plantation_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: sow one oak (growth 0.8) and one rice plant (growth 0.9) in a small oak growing zone near the colonists and report both plants' ids with whether chop acquisition may take them.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) return Refuse("No free colonists on the map.");
                var cutter = people.FirstOrDefault(p => !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting));
                if (cutter == null) return Refuse("No colonist able to cut plants.");
                if (cutter.workSettings != null && cutter.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) == 0)
                    cutter.workSettings.SetPriority(WorkTypeDefOf.PlantCutting, 1);
                var oakDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeOak");
                var riceDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");
                if (oakDef?.plant == null || riceDef?.plant == null) return Refuse("Plant_TreeOak or Plant_Rice unavailable in this ruleset.");

                const int width = 2, height = 1;
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var origin = GenRadial.RadialCellsAround(center, 12, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building || t is Blueprint || t is Frame))
                    && cutter.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 2x1 plot near the colonist centroid for the fixture zone.");
                foreach (var old in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) old.Delete();
                var zone = new Zone_Growing(map.zoneManager) { label = "Plantation fixture oak" };
                BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").SetValue(zone, oakDef);
                map.zoneManager.RegisterZone(zone);
                var cells = new CellRect(origin.x, origin.z, width, height).Cells.ToList();
                foreach (var c in cells)
                {
                    foreach (var thing in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    zone.AddCell(c);
                }
                if (zone.Cells.Count != width * height) return Refuse("Fixture zone did not take its cells.");
                var oak = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(oakDef), cells[0], map);
                oak.sown = true; oak.Growth = 0.8f;
                var rice = (Plant)GenSpawn.Spawn(ThingMaker.MakeThing(riceDef), cells[1], map);
                rice.sown = true; rice.Growth = 0.9f;

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, zoneId = zone.ID, cutter = cutter.GetUniqueLoadID(),
                    oak = Describe(oak, map), rice = Describe(rice, map),
                    setup = "Test-only sown oak and rice in an oak growing zone; no designation or cut injected.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Describe(Plant plant, Map map) => new {
            id = plant.GetUniqueLoadID(), growth = plant.Growth, plantation = ResourceAcquisitionTools.Plantation(plant),
            eligible = ResourceAcquisitionTools.Eligible(plant, map),
            designatorAccepts = ResourceAcquisitionTools.DesignatorFor(plant).CanDesignateThing(plant).Accepted,
            cell = new { x = plant.Position.x, z = plant.Position.z } };

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
