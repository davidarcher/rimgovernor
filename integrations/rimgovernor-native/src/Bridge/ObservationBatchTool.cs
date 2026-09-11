using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class ObservationBatchTools
    {
        [Tool("home/observation_batch", Title = "Colony observation batch",
            Description = "Read the standard colony observation sections in one request. No orders, selection or clock changes. Retains section diagnostics and before/after ticks; rejects game/map changes. Sections run sequentially, not as an atomic snapshot.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            Game game = null;
            Map map = null;
            int tick = 0;
            var queue = Stopwatch.StartNew();
            await ctx.MainThread.InvokeAsync<object>(() => {
                game = Current.Game; map = Find.CurrentMap;
                tick = Find.TickManager?.TicksGame ?? 0;
                return null;
            }, cancellationToken).ConfigureAwait(false);
            var initialQueueMs = queue.Elapsed.TotalMilliseconds;
            if (game == null || map == null) return new { success = false, error = "Load a colony first" };
            var sections = new Dictionary<string, object>();
            var timings = new Dictionary<string, double>();
            async Task Read(string name, Func<Task<object>> read)
            {
                var watch = Stopwatch.StartNew();
                sections[name] = await read().ConfigureAwait(false);
                timings[name] = watch.Elapsed.TotalMilliseconds;
            }
            await Read("status_before", () => new HomeStatusTools().Status(ctx, cancellationToken));
            await Read("pawns", () => new HomePawnTools().ListPawns(ctx, cancellationToken,
                colonistsOnly: true, health: true, needs: true, equipment: true));
            await Read("supplies", () => new HomeThingTools().ListThings(ctx, cancellationToken,
                ownership: "ours", excludeChunks: true, maxPositionsPerDef: 0));
            await Read("buildings", () => new HomeBuildingTools().ListBuildings(ctx, cancellationToken, playerOnly: true));
            await Read("rooms", () => new HomeRoomTools().ListRooms(ctx, cancellationToken));
            await Read("zones", () => new HomeZoneTools().ListZones(ctx, cancellationToken));
            await Read("status_after", () => new HomeStatusTools().Status(ctx, cancellationToken));
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (!ReferenceEquals(game, Current.Game) || !ReferenceEquals(map, Find.CurrentMap)
                    || Find.TickManager.TicksGame < tick)
                    return new { success = false, error = "Game/map changed during observation batch" };
                return new { success = true, version = 1, sections,
                    timing = new { initialMainThreadQueueMs = initialQueueMs, sectionAwaitMs = timings } };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
