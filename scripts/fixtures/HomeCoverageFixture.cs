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
        [Tool("test/extent_home_prepare", Description = "Prepare a ready Home census with one missing observed corridor cell for the colony extent smoke.")]
        public async Task<object> PrepareExtent(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var cell = new IntVec3(x, 0, z);
                var scopes = HomeCoverage.Targets(map).Select(id => HomeCoverage.FullScope(map, id)).ToList();
                if (scopes.Any(cells => cells == null) || !scopes.Any(cells => cells.Contains(cell)))
                    throw new InvalidOperationException("Complete observed corridor required.");
                foreach (var cells in scopes) foreach (var c in cells) map.areaManager.Home[c] = true;
                map.areaManager.Home[cell] = false;
                return new { success = true, x, z, targets = scopes.Count };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/home_mask", Description = "Read every native Home bit in cell-index order without changing the map.")]
        public async Task<object> Mask(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var bytes = new byte[(map.cellIndices.NumGridCells + 7) / 8];
                foreach (var cell in map.areaManager.Home.ActiveCells) {
                    int index = map.cellIndices.CellToIndex(cell);
                    bytes[index / 8] |= (byte)(1 << (index % 8));
                }
                return new { success = true, mask = Convert.ToBase64String(bytes) };
            }, cancellationToken).ConfigureAwait(false);
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
        }
}
