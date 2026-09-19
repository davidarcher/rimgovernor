using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Newtonsoft.Json.Linq;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeCoverageFixture
    {
        [Tool("test/home_coverage_setup", Description = "Prepare missing Home over an exact disposable controller-built facility. Does not change its native geometry. Fixture input only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                if (cells == null) return new { success = false, error = "No bounded native scope" };
                try {
                    HomeCoverage.PreparingFixture = true;
                    foreach (var c in cells) map.areaManager.Home[c] = false;
                } finally { HomeCoverage.PreparingFixture = false; }
                state.Revision++;
                return new { success = true, target, shape = HomeCoverage.Shape(target, cells), count = cells.Count, revision = state.Revision,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_remove_cell", Description = "Remove Home on one exact covered fixture cell so autonomous restoration is required.")]
        public async Task<object> Remove(IRimBridgeContext ctx, CancellationToken cancellationToken, string target, int x, int z)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                var cell = new IntVec3(x, 0, z);
                if (cells == null || !cells.Contains(cell) || !map.areaManager.Home[cell]) throw new InvalidOperationException("Covered facility cell required.");
                long before = state.Revision;
                map.areaManager.Home[cell] = false;
                return new { success = state.Revision > before && !map.areaManager.Home[cell], x = cell.x, z = cell.z, revision = state.Revision };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_coverage_read", Description = "Independently read actual native Home cells and the persisted Home revision for a disposable facility.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.FullScope(map, target);
                return new { success = cells != null, target, covered = cells?.Count(c => map.areaManager.Home[c]), total = cells?.Count,
                    revision = state.Revision, outsideHome = cells == null ? (int?)null : map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_cell_read", Description = "Read actual Home membership at one exact fixture cell.")]
        public async Task<object> Cell(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var c = new IntVec3(x, 0, z);
                return new { success = c.InBounds(map), home = c.InBounds(map) && map.areaManager.Home[c] };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_stale_guards", Description = "Paused disposable fixture: prove stale Home revisions and changed connecting geometry refuse dry-run writes; restore the doorway afterwards.")]
        public async Task<object> Stale(IRimBridgeContext ctx, CancellationToken cancellationToken, string target, int doorX, int doorZ)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!Find.TickManager.Paused) throw new InvalidOperationException("Paused fixture required.");
                var cells = HomeCoverage.Scope(map, target);
                if (cells == null) throw new InvalidOperationException("Facility scope required.");
                var state = HomeCoverage.State(map);
                var shape = HomeCoverage.Shape(target, cells); var revision = state.Revision;
                var changed = cells.First(c => map.areaManager.Home[c]);
                map.areaManager.Home[changed] = false;
                bool revisionRefused = !JObject.FromObject(HomeCoverage.Apply(target, shape, revision, true)).Value<bool>("accepted");
                map.areaManager.Home[changed] = true;
                var position = new IntVec3(doorX, 0, doorZ);
                var door = position.GetEdifice(map) as Building_Door;
                if (door == null) throw new InvalidOperationException("Fixture doorway required.");
                var def = door.def; var stuff = door.Stuff; var rotation = door.Rotation;
                door.Destroy(DestroyMode.Vanish);
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
                wall.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(wall, position, map);
                bool geometryRefused;
                try {
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    geometryRefused = !JObject.FromObject(HomeCoverage.Apply(target, shape, state.Revision, true)).Value<bool>("accepted");
                } finally {
                    wall.Destroy(DestroyMode.Vanish);
                    var restored = ThingMaker.MakeThing(def, stuff);
                    restored.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(restored, position, map, rotation);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                }
                return new { success = revisionRefused && geometryRefused, revisionRefused, geometryRefused };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_bulk_edits", Description = "Exercise Home Clear and Invert hooks in a disposable colony; changes the map Home mask as declared test input and proves each bulk edit advances the Home revision.")]
        public async Task<object> Bulk(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                long before = state.Revision;
                map.areaManager.Home.Clear();
                bool clear = state.Revision > before && map.areaManager.Home.TrueCount == 0;
                try { HomeCoverage.PreparingFixture = true; foreach (var c in cells) map.areaManager.Home[c] = true; }
                finally { HomeCoverage.PreparingFixture = false; }
                long beforeInvert = state.Revision;
                map.areaManager.Home.Invert();
                bool invert = state.Revision > beforeInvert && cells.All(c => !map.areaManager.Home[c]);
                return new { success = clear && invert, clear, invert, revision = state.Revision };
            }, cancellationToken).ConfigureAwait(false);
        }
}
