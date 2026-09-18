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
    // Disposable test setup only. Constrains the colony's terrain so that the
    // shelter planner's site window admits only the shell shape a case is
    // after (issues #7, #175). Nothing here designates, builds or orders
    // anything.
    //
    // layout "rows": rows of natural granite every period cells across the
    // colonists' neighbourhood leave strips period-1 cells wide, each row
    // pierced by a walkway every twelfth cell so pawns still reach every
    // strip and the trees beyond. Five-cell strips (the default period of
    // six) fit no hut template and no 9x9 rectangle, so the planner has to
    // grow an irregular shell; an eight-cell strip fits only the low
    // east-west oval, a ten-cell strip the medium one.
    //
    // layout "pocket-l" / "pocket-connector": granite over the whole field
    // but for one clearing shaped to the concave template's cells (an L, or
    // two chambers a cell apart) and a five-wide walkway running south from
    // its door to the field's edge, so the template is the only shell that
    // fits and the builders reach it from outside.
    public sealed class CorridorTerrainFixture
    {
        private const int Reach = 48;    // half-extent of the rock field around the colonists
        private const int WalkwayPeriod = 12; // walkway columns along a row; adjacent rows offset by half

        [Tool("test/corridor_terrain_fixture", Description = "UNSAFE FOR MODEL EXECUTION. Disposable corridor-terrain fixture: setup raises granite around the colonists in rows every period cells (layout rows) or everywhere but an L-shaped or two-chamber clearing with a walkway south (layout pocket-l, pocket-connector) so only the shell shape under test fits; inspect reads one cell's rock, walkability, reservations and room.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0, string layout = "rows", int period = 6)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("Disposable map required.");
                if (action == "inspect") return Inspect(map, new IntVec3(x, 0, z));
                if (action != "setup") throw new InvalidOperationException("Unknown action " + action);
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused map required for setup.");
                if (layout == "rows") {
                    if (period < 4 || period > 16) throw new InvalidOperationException("period must be 4..16");
                    return SetupRows(map, period);
                }
                if (layout == "pocket-l" || layout == "pocket-connector") return SetupPocket(map, layout);
                throw new InvalidOperationException("Unknown layout " + layout);
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
                roof = cell.GetRoof(map)?.defName,
                room = cell.GetRoom(map) is Room room ? new { id = room.ID, proper = room.ProperRoom, outdoors = room.PsychologicallyOutdoors,
                    touchesMapEdge = room.TouchesMapEdge, cells = room.CellCount } : null };
        }

        private static IntVec3 Centre(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            if (people.Count < 1) throw new InvalidOperationException("Require a colonist.");
            QuietStoryteller.Apply(map);
            return new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
        }

        // Raise spawns granite on a cell of open ground, after moving the
        // colonists and their landing supplies off it: colonists step to the
        // nearest open cell the layout keeps, supplies go to the depot,
        // outside the shelter planner's site window, since an item under a
        // wall cell is one the builders must haul aside first (run 14 never
        // built the wall over one). It reports whether rock now stands.
        private static bool Raise(Map map, IntVec3 c, ThingDef granite, Func<IntVec3, bool> kept, IntVec3 depot, ref int moved)
        {
            var terrain = c.GetTerrain(map);
            if (terrain.passability == Traversability.Impassable || terrain.IsWater) return false;
            if (c.GetEdifice(map) != null || c.GetZone(map) != null
                || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame)) return false;
            foreach (var t in c.GetThingList(map).Where(t => t is Pawn || t.def.category == ThingCategory.Item).ToList()) {
                Func<float, IntVec3> near = radius => GenRadial.RadialCellsAround(c, radius, false)
                    .FirstOrDefault(n => n.InBounds(map) && kept(n) && n.Standable(map) && n.GetEdifice(map) == null);
                var beside = near(5.9f);
                if (beside == default(IntVec3)) beside = near(GenRadial.MaxRadialPatternRadius - 1f);
                if (beside == default(IntVec3)) return false; // nowhere to go: the ground stays open here
                if (t is Pawn pawn) { pawn.Position = beside; pawn.Notify_Teleported(true, true); moved++; continue; }
                // Stone chunks are the bulk of what lies on a map: moved to the
                // depot they filled a pocket layout's whole walkway and spilt
                // into the clearing (#175), so they vanish instead.
                if (t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.Chunks)) { t.Destroy(DestroyMode.Vanish); moved++; continue; }
                var at = GenRadial.RadialCellsAround(depot, 9.9f, true)
                    .FirstOrDefault(n => n.InBounds(map) && kept(n) && n.Standable(map) && n.GetEdifice(map) == null
                        && !n.GetThingList(map).Any(o => o.def.category == ThingCategory.Item));
                if (at == default(IntVec3)) return false; // the depot is full: the ground stays open here
                t.DeSpawn();
                GenPlace.TryPlaceThing(t, at, map, ThingPlaceMode.Direct);
                moved++;
            }
            foreach (var t in c.GetThingList(map).Where(t => t is Plant || t is Filth).ToList()) t.Destroy(DestroyMode.Vanish);
            if (c.GetThingList(map).Any(t => t is Pawn || t.def.category == ThingCategory.Item)) return false;
            GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map);
            return true;
        }

        // Open clears a kept cell to open ground: natural rock left in a
        // strip closes a walkway or splits the strip into an enclosed
        // pocket, and a shell whose gap opens onto an enclosed pocket is
        // roofed by the game through the gap, so the repair a case exercises
        // is never owed (#193 run r1). Natural roof goes with the rock, or
        // the cleared cells read as roofed indoor space.
        private static int Open(Map map, IntVec3 c)
        {
            var cleared = 0;
            if (c.GetEdifice(map) is Mineable rock) { rock.Destroy(DestroyMode.Vanish); cleared++; }
            // A chunk lying in the clearing is one the builders would have to
            // haul aside before a wall goes over it; it vanishes.
            foreach (var chunk in c.GetThingList(map).Where(t => t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.Chunks)).ToList()) { chunk.Destroy(DestroyMode.Vanish); cleared++; }
            var roof = c.GetRoof(map);
            if (roof != null && roof.isNatural) map.roofGrid.SetRoof(c, null);
            return cleared;
        }

        private static object SetupRows(Map map, int period)
        {
            var center = Centre(map);
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            var rows = new List<object>();
            var raised = 0;
            var skipped = 0;
            var moved = 0;
            Func<IntVec3, bool> kept = n => ((n.z - center.z) % period + period) % period != 0;
            var cleared = 0;
            for (int z = center.z - Reach; z <= center.z + Reach; z++) {
                if (!kept(new IntVec3(0, 0, z))) continue;
                for (int x = center.x - Reach; x <= center.x + Reach; x++) {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map) || c.Fogged(map)) continue;
                    cleared += Open(map, c);
                }
            }
            for (int row = -Reach / period; row <= Reach / period; row++) {
                var z = center.z + row * period;
                // Walkways every twelfth cell, adjacent rows offset by six, so
                // no two neighbouring rows open at the same column (a seven-row
                // template spans two rock rows and would need both pierced at
                // its own centre column) and a shell grown along a strip,
                // which covers at most two of them, never cuts the strips
                // beside it off from each other (run 12 starved the builders).
                var phase = row % 2 == 0 ? 0 : WalkwayPeriod / 2;
                var placed = 0;
                var depot = new IntVec3(center.x - 30, 0, z + 2);
                for (int x = center.x - Reach; x <= center.x + Reach; x++) {
                    var c = new IntVec3(x, 0, z);
                    if (((x - center.x) % WalkwayPeriod + WalkwayPeriod) % WalkwayPeriod == phase || !c.InBounds(map) || c.Fogged(map)) continue;
                    if (Raise(map, c, granite, kept, depot, ref moved)) placed++; else skipped++;
                }
                rows.Add(new { z, walkwayPhase = phase, placed });
                raised += placed;
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return new { success = true, layout = "rows", tick = Find.TickManager.TicksGame, center = new { x = center.x, z = center.z }, period, walkwayPeriod = WalkwayPeriod, reach = Reach, raised, skipped, moved, cleared, rows };
        }

        // Pocket lists the clearing of a pocket layout around the centre: the
        // cells of the concave template's shell (policy.concaveTemplates,
        // anchored on its bounding box's centre, door south) and a walkway
        // five cells wide from the door's threshold to the field's edge.
        private static HashSet<IntVec3> Pocket(IntVec3 c, string layout)
        {
            var open = new HashSet<IntVec3>();
            void Box(int x0, int z0, int x1, int z1)
            {
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) open.Add(new IntVec3(c.x + x, 0, c.z + z));
            }
            int doorX;
            int walkwayTop;
            if (layout == "pocket-l") {
                // concave-l-ne: a 7x3 arm along the bottom and a 3x7 arm up
                // the left, ring included, minus the notch north-east.
                Box(-4, -4, 4, 0);
                Box(-4, 0, 0, 4);
                doorX = 0;
                walkwayTop = -5;
            } else {
                // connector-ew: two 4x4 chambers with their rings and the
                // one-cell connector's slot between them.
                Box(-6, -3, -1, 2);
                Box(1, -3, 6, 2);
                Box(0, -1, 0, 1);
                doorX = -3;
                walkwayTop = -4;
            }
            Box(doorX - 2, -Reach, doorX + 2, walkwayTop);
            return open;
        }

        private static object SetupPocket(Map map, string layout)
        {
            var center = Centre(map);
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            var pocket = Pocket(center, layout);
            Func<IntVec3, bool> kept = n => pocket.Contains(n);
            var depot = pocket.Where(p => p.z == center.z - Reach).OrderBy(p => p.x).First();
            var raised = 0;
            var skipped = 0;
            var moved = 0;
            var cleared = 0;
            foreach (var c in pocket) if (c.InBounds(map) && !c.Fogged(map)) cleared += Open(map, c);
            for (int z = center.z - Reach; z <= center.z + Reach; z++) {
                for (int x = center.x - Reach; x <= center.x + Reach; x++) {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map) || c.Fogged(map) || pocket.Contains(c)) continue;
                    if (Raise(map, c, granite, kept, depot, ref moved)) raised++; else skipped++;
                }
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            return new { success = true, layout, tick = Find.TickManager.TicksGame, center = new { x = center.x, z = center.z }, reach = Reach, raised, skipped, moved, cleared,
                open = pocket.OrderBy(p => p.z).ThenBy(p => p.x).Select(p => new { p.x, p.z }).ToList() };
        }
    }
}
