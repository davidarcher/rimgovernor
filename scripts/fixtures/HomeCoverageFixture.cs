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
                    foreach (var c in cells) map.areaManager.Home[c] = false;
                } finally { HomeCoverage.PreparingFixture = false; }
                state.Revision++;
                return new { success = true, target, shape = HomeCoverage.Shape(target, cells), count = cells.Count, revision = state.Revision,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_player_remove", Description = "Exercise an external player-style Home removal on one covered facility cell; proves the removal advances the Home revision.")]
        public async Task<object> Remove(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                var cell = cells.First(c => map.areaManager.Home[c]);
                long before = state.Revision;
                map.areaManager.Home[cell] = false;
                return new { success = state.Revision > before && !map.areaManager.Home[cell], x = cell.x, z = cell.z, revision = state.Revision };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_coverage_read", Description = "Independently read actual native Home cells and the persisted Home revision for a disposable facility.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                return new { success = cells != null, target, covered = cells?.Count(c => map.areaManager.Home[c]), total = cells?.Count,
                    revision = state.Revision, outsideHome = cells == null ? (int?)null : map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
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
