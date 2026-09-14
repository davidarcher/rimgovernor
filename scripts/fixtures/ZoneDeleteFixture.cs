using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one small walled, explicitly
    // roofed, empty and unzoned 2x2 interior near an existing colonist and
    // reports its exact cells, so zoneaccept can exercise the typed CreateZone
    // dispatch (a stockpile zone needs roofed, walkable, clear, unzoned
    // ground -- NativeZoneCreation.Prepare's own stockpile eligibility) and
    // then the typed DeleteZone dispatch on the zone it creates, without
    // depending on native random colony layout, mirroring BedAssignFixture's
    // own small-enclosure pattern.
    public sealed class ZoneDeleteFixture
    {
        [Tool("test/zone_delete_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one small walled, explicitly roofed, empty and unzoned 2x2 interior near an existing colonist and report its exact cells.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No existing colonist to search near.");

                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, 4, 4).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                if (origin == default) return Refuse("No open area for the fixture zone site.");

                for (var x = 0; x < 4; x++) for (var z = 0; z < 4; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (x == 0 || z == 0 || x == 3 || z == 3)
                    {
                        var wall = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }

                var cells = new System.Collections.Generic.List<object>();
                for (var x = 1; x < 3; x++) for (var z = 1; z < 3; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (!c.Roofed(map)) return Refuse("Fixture interior cell (" + c.x + "," + c.z + ") is not roofed.");
                    if (c.GetEdifice(map) != null) return Refuse("Fixture interior cell (" + c.x + "," + c.z + ") has an edifice: " + c.GetEdifice(map).def.defName);
                    var things = c.GetThingList(map);
                    if (things.Count != 0) return Refuse("Fixture interior cell (" + c.x + "," + c.z + ") has " + things.Count + " thing(s): " + string.Join(",", things.Select(t => t.def.defName)));
                    if (map.zoneManager.ZoneAt(c) != null) return Refuse("Fixture interior cell (" + c.x + "," + c.z + ") is already zoned.");
                    cells.Add(new { x = c.x, z = c.z });
                }

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, cells,
                    setup = "Test-only roofed, walled, empty, unzoned 2x2 interior; native zone creation/deletion dispatch remains native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
