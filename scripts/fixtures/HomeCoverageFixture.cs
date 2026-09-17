using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
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
                    foreach (var c in cells) { map.areaManager.Home[c] = false; state.Excluded[c] = false; }
                } finally { HomeCoverage.PreparingFixture = false; }
                state.Revision++;
                return new { success = true, target, shape = HomeCoverage.Shape(target, cells), count = cells.Count,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_player_remove", Description = "Exercise an external player-style Home removal on one covered facility cell.")]
        public async Task<object> Remove(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                var cell = cells.First(c => map.areaManager.Home[c]);
                map.areaManager.Home[cell] = false;
                return new { success = state.Excluded[cell] && !map.areaManager.Home[cell], x = cell.x, z = cell.z };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_coverage_read", Description = "Independently read actual native Home cells and persisted player exclusions for a disposable facility.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                return new { success = cells != null, target, covered = cells?.Count(c => map.areaManager.Home[c]),
                    excluded = cells?.Count(c => state.Excluded[c]), total = cells?.Count,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_bulk_edits", Description = "Exercise Home Clear and Invert hooks in a disposable colony; changes the map Home mask as declared test input.")]
        public async Task<object> Bulk(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                map.areaManager.Home.Clear();
                bool clear = state.Excluded.TrueCount == map.Size.x * map.Size.z && map.areaManager.Home.TrueCount == 0;
                state.Excluded = new BoolGrid(map);
                try { HomeCoverage.PreparingFixture = true; foreach (var c in cells) map.areaManager.Home[c] = true; }
                finally { HomeCoverage.PreparingFixture = false; }
                map.areaManager.Home.Invert();
                bool invert = cells.All(c => !map.areaManager.Home[c] && state.Excluded[c]);
                return new { success = clear && invert, clear, invert };
            }, cancellationToken).ConfigureAwait(false);
    }
}
