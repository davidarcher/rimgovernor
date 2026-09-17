using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable Home-area staging for MaintainHomeCoverage. Native no longer
    // persists player exclusions (the controller acts on player-originated
    // Home state instead of refusing it), so these ops only move the Home
    // mask and bump the coverage revision.
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
                return new { success = true, target, shape = HomeCoverage.Shape(target, cells), count = cells.Count,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_player_remove", Description = "Exercise an external player-style Home removal on one covered facility cell.")]
        public async Task<object> Remove(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var cells = HomeCoverage.Scope(map, target);
                var cell = cells.First(c => map.areaManager.Home[c]);
                map.areaManager.Home[cell] = false;
                return new { success = !map.areaManager.Home[cell], x = cell.x, z = cell.z };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/home_coverage_read", Description = "Independently read actual native Home cells for a disposable facility.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var state = HomeCoverage.State(map); var cells = HomeCoverage.Scope(map, target);
                return new { success = cells != null, target, covered = cells?.Count(c => map.areaManager.Home[c]),
                    total = cells?.Count, revision = state.Revision,
                    outsideHome = map.areaManager.Home.ActiveCells.Count(c => !cells.Contains(c)) };
            }, cancellationToken).ConfigureAwait(false);
    }
}
