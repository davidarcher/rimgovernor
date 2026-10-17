using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
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
        }
}
