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
    // Disposable test setup only. Constrains the colony's terrain so that no
    // hut template or 9x9 starter rectangle fits anywhere in the shelter
    // planner's site window and the planner has to grow an irregular shell
    // (issue #7): rows of natural granite every sixth cell across the
    // colonists' neighbourhood leave corridors five cells wide, each row
    // pierced by a walkway every twelfth cell so pawns still reach every
    // strip and the trees beyond. Nothing here designates, builds or
    // orders anything.
    public sealed class CorridorTerrainFixture
    {
        private const int Period = 6;    // rock row spacing: strips of five free cells
        private const int Reach = 48;    // half-extent of the rock field around the colonists
        private const int WalkwayPeriod = 12; // walkway columns along a row; adjacent rows offset by half

        [Tool("test/corridor_terrain_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable corridor-terrain fixture: setup raises rows of granite every sixth cell around the colonists so only an irregular grown shell fits; inspect reads one cell's rock and walkability.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (action == "inspect") return Inspect(map, new IntVec3(x, 0, z));
                if (action != "setup") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required for setup.");
                return Setup(map);
            }, cancellationToken);
        }

        private static object Inspect(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return new { success = false, error = "Cell out of bounds" };
            var rock = cell.GetEdifice(map) as Mineable;
            return new { success = true, mineable = rock?.def.defName, walkable = cell.Walkable(map), standable = cell.Standable(map),
                things = cell.GetThingList(map).Select(t => t.def.defName + (t.stackCount > 1 ? "x" + t.stackCount : "")).ToList(),
                forbidden = cell.GetThingList(map).Any(t => t.IsForbidden(Faction.OfPlayerSilentFail)),
                reservations = map.reservationManager.ReservationsReadOnly
                    .Where(r => r.Target.Cell == cell || r.Target.HasThing && r.Target.Thing.Position == cell)
                    .Select(r => new { claimant = r.Claimant?.LabelShort, at = r.Claimant?.Position.ToString(), job = r.Job?.def.defName,
                        jobTarget = r.Claimant?.CurJob?.targetA.ToString(), driver = r.Claimant?.jobs?.curDriver?.GetType().Name,
                        report = r.Claimant?.jobs?.curDriver?.GetReport() }).ToList(),
                blueprint = cell.GetThingList(map).OfType<Blueprint>().Select(b => new { b.def.defName, forbidden = b.IsForbidden(Faction.OfPlayerSilentFail),
                    materials = b is Blueprint_Build bb ? bb.TotalMaterialCost().Select(m => m.thingDef.defName + "x" + m.count).ToList() : null }).FirstOrDefault(),
                frame = cell.GetThingList(map).OfType<Frame>().Select(f => new { f.def.defName, f.workDone, resources = f.resourceContainer.ContentsString }).FirstOrDefault(),
                roof = cell.GetRoof(map)?.defName };
        }

        private static object Setup(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            if (people.Count < 1) throw new InvalidOperationException("Require a colonist.");
            QuietStoryteller.Apply(map);
            var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            var rows = new List<object>();
            var raised = 0;
            var skipped = 0;
            var moved = 0;
            for (int row = -Reach / Period; row <= Reach / Period; row++) {
                var z = center.z + row * Period;
                // Walkways every twelfth cell, adjacent rows offset by six, so
                // no two neighbouring rows open at the same column (a seven-row
                // template spans two rock rows and would need both pierced at
                // its own centre column) and a shell grown along a strip,
                // which covers at most two of them, never cuts the strips
                // beside it off from each other (run 12 starved the builders).
                var phase = row % 2 == 0 ? 0 : WalkwayPeriod / 2;
                var placed = 0;
                for (int x = center.x - Reach; x <= center.x + Reach; x++) {
                    var c = new IntVec3(x, 0, z);
                    if (((x - center.x) % WalkwayPeriod + WalkwayPeriod) % WalkwayPeriod == phase || !c.InBounds(map) || c.Fogged(map)) continue;
                    var terrain = c.GetTerrain(map);
                    if (terrain.passability == Traversability.Impassable || terrain.IsWater) { skipped++; continue; }
                    if (c.GetEdifice(map) != null || c.GetZone(map) != null
                        || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame)) { skipped++; continue; }
                    // The colonists and their landing supplies cluster at the
                    // centre, on the rows as much as between them; a row
                    // pierced there is a second walkway (run 9 grew a shell
                    // across one). Colonists step into the strip beside the
                    // row; supplies go to the strip's far end, outside the
                    // shelter planner's 22-cell site window, since an item
                    // under a wall cell is one the builders must haul aside
                    // first (run 14 never built the wall over one).
                    foreach (var t in c.GetThingList(map).Where(t => t is Pawn || t.def.category == ThingCategory.Item).ToList()) {
                        var beside = GenRadial.RadialCellsAround(c, 2.5f, false)
                            .FirstOrDefault(n => n.z != z && n.InBounds(map) && n.Standable(map) && n.GetEdifice(map) == null);
                        if (beside == default(IntVec3)) break; // nowhere to go: the row stays pierced here
                        if (t is Pawn pawn) { pawn.Position = beside; pawn.Notify_Teleported(false, true); moved++; continue; }
                        var far = new IntVec3(center.x - 30, 0, z + 2);
                        var depot = GenRadial.RadialCellsAround(far, 6f, true)
                            .FirstOrDefault(n => n.InBounds(map) && n.Standable(map) && n.GetEdifice(map) == null && (n.z - center.z) % Period != 0
                                && !n.GetThingList(map).Any(o => o.def.category == ThingCategory.Item));
                        if (depot == default(IntVec3)) depot = beside;
                        t.DeSpawn();
                        GenPlace.TryPlaceThing(t, depot, map, ThingPlaceMode.Near);
                        moved++;
                    }
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t is Filth).ToList()) t.Destroy(DestroyMode.Vanish);
                    if (c.GetThingList(map).Any(t => t is Pawn || t.def.category == ThingCategory.Item)) { skipped++; continue; }
                    GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map);
                    placed++;
                }
                rows.Add(new { z, walkwayPhase = phase, placed });
                raised += placed;
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return new { success = true, tick = Find.TickManager.TicksGame, center = new { x = center.x, z = center.z }, period = Period, walkwayPeriod = WalkwayPeriod, reach = Reach, raised, skipped, moved, rows };
        }
    }
}
