using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;

// The dirty-chunk refresh (#652), driven over a fake grid through the
// production ledger and refresh: which chunks a mutation rebuilds, what a
// stable window costs, the scheduled scan for unhooked fields, every
// resync reason, redirtying between capture and publication, and parity
// with a full read once the refresh catches up. Work is asserted in
// counts, never in elapsed time.
internal static partial class NativePlanningWindowViewProbe
{
    // A 64x64 map; the region spans x 10..29, z 16..39: three map-aligned
    // bands (16-23, 24-31, 32-39), each one tile row.
    private const int MapSize = 64, RMinX = 10, RMinZ = 16, RMaxX = 29, RMaxZ = 39;

    private sealed class FakeWorld : IPlanningViewSource
    {
        internal readonly PlanningViewCell[,] Cells = new PlanningViewCell[MapSize, MapSize];
        internal int Reads, Scans;
        internal FakeWorld()
        {
            for (var x = 0; x < MapSize; x++)
                for (var z = 0; z < MapSize; z++) Cells[x, z] = new PlanningViewCell(false, PlanningViewCell.Walkable, null, null, "1", 0.5, 0);
        }
        public PlanningViewCell Read(int x, int z) { Reads++; return Cells[x, z]; }
        public bool StillCurrent(int x, int z, in PlanningViewCell held)
        {
            Scans++;
            var live = Cells[x, z];
            const int unhooked = PlanningViewCell.Polluted | PlanningViewCell.Indoors;
            return held.Fogged || live.Glow == held.Glow && (live.Flags & unhooked) == (held.Flags & unhooked);
        }
        // A hooked change: the cell's value and its ledger mark together.
        internal void Set(PlanningViewLedger ledger, int x, int z, PlanningViewCell cell) { Cells[x, z] = cell; ledger.Mark(x, z); }
    }

    private static PlanningViewCell Cell(string zone, double glow = 0.5, int flags = PlanningViewCell.Walkable) => new PlanningViewCell(false, flags, null, zone, "1", glow, 0);

    private static PlanningViewRoot Refresh(PlanningWindowViewPublisher publisher, PlanningViewLedger ledger, FakeWorld world, long tick, out PlanningViewRefreshStats stats,
        PlanningViewRoot current = null, int maxZ = RMaxZ, bool publish = true, int minZ = RMinZ, int minX = RMinX, int maxX = RMaxX)
    {
        stats = new PlanningViewRefreshStats();
        var identity = Identity("load");
        var root = PlanningViewRefresh.Refresh(ledger, publisher.Begin(identity), current ?? publisher.Acquire(), identity, 1, MapSize, MapSize, minX, minZ, maxX, maxZ, tick, world, stats);
        if (publish) Check(publisher.TryPublish(root), "refreshed root publishes");
        return root;
    }

    // Parity: every retained cell equals what a full read of the world
    // gives now.
    private static bool Current(PlanningViewRoot root, FakeWorld world)
    {
        var width = root.MaxX - root.MinX + 1;
        for (var i = 0; i < root.ChunkCount; i++)
        {
            var chunk = root.Chunk(i);
            for (var j = 0; j < chunk.Count; j++)
            {
                var held = chunk[j]; var live = world.Cells[root.MinX + j % width, chunk.MinZ + j / width];
                if (held.Fogged != live.Fogged || held.Flags != live.Flags || held.ZoneId != live.ZoneId || held.RoomId != live.RoomId || held.Glow != live.Glow) return false;
            }
        }
        return true;
    }

    private static void RefreshFollowsTheLedger()
    {
        StableWindowCostsNothing();
        LocalChangeRebuildsItsBand();
        BroadAndTopologyChangesRebuildAll();
        UnhookedFieldsWaitForTheScan();
        ResyncReasons();
        RedirtyBetweenCaptureAndPublication();
        PannedWindowReusesItsBands();
    }

    private static void StableWindowCostsNothing()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var first = Refresh(publisher, ledger, world, 100, out var boot);
        Check(boot.Resync == "bootstrap" && boot.Rebuilt == 3 && boot.CellsRead == 20 * 24 && Current(first, world), "bootstrap reads the region");
        Check(boot.RetainedBytes == 20 * 24 * PlanningViewRefresh.CellBytes, "retained bytes are the region's cells");
        world.Reads = 0;
        for (var tick = 110; tick < 100 + PlanningViewRefresh.ValidateEveryTicks; tick += 50)
        {
            var root = Refresh(publisher, ledger, world, tick, out var stats);
            Check(stats.Resync == null && stats.Rebuilt == 0 && stats.Reused == 3 && stats.CellsRead == 0 && stats.CellsScanned == 0 && world.Reads == 0 && world.Scans == 0,
                "a stable window between validations reads and scans nothing");
            Check(Enumerable.Range(0, 3).All(i => ReferenceEquals(root.Chunk(i), first.Chunk(i))), "unchanged chunks are the same immutable objects");
            Check(stats.RetainedBytes == boot.RetainedBytes && stats.AgeTicks == tick - 100, "bounded storage; age is the oldest validation");
        }
    }

    private static void LocalChangeRebuildsItsBand()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var first = Refresh(publisher, ledger, world, 100, out _);
        // z 17 lies in tile row 16-23, band 16-23 only.
        world.Set(ledger, 12, 17, Cell("zone"));
        var second = Refresh(publisher, ledger, world, 100, out var stats);
        Check(stats.Rebuilt == 1 && stats.DirtyChunks == 1 && stats.Reused == 2 && stats.CellsRead == 20 * 8 && stats.DirtyTiles == 1, "one local change rebuilds its band only");
        Check(second.Chunk(0).Revision == second.Revision && ReferenceEquals(second.Chunk(1), first.Chunk(1)) && Current(second, world), "the rebuilt band is current (same tick)");
        // Two edits in one tick after a refresh at that tick: counted by
        // sequence, not by tick, so neither is lost.
        world.Set(ledger, 12, 37, Cell("a"));
        world.Set(ledger, 12, 37, Cell("b"));
        var third = Refresh(publisher, ledger, world, 100, out stats);
        Check(stats.Rebuilt == 1 && third.Chunk(2)[5 * 20 + 2].ZoneId == "b" && Current(third, world), "same-tick edits after a same-tick capture are seen");
        // A mark outside the region dirties nothing in it; bands are tile
        // rows, so a mark on a band's last row rebuilds that band alone.
        world.Set(ledger, 50, 50, Cell("far"));
        Refresh(publisher, ledger, world, 101, out stats);
        Check(stats.Rebuilt == 0, "a change outside the region rebuilds nothing");
        world.Set(ledger, 12, 23, Cell("edge"));
        var fourth = Refresh(publisher, ledger, world, 102, out stats);
        Check(stats.Rebuilt == 1 && stats.Reused == 2 && Current(fourth, world), "a band's edge row rebuilds that band alone");
    }

    private static void BroadAndTopologyChangesRebuildAll()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        Refresh(publisher, ledger, world, 100, out _);
        // Room renumbering: every id may move, far from the wall.
        for (var x = 0; x < MapSize; x++) for (var z = 0; z < MapSize; z++) world.Cells[x, z] = new PlanningViewCell(false, PlanningViewCell.Walkable, null, null, "2", 0.5, 0);
        ledger.MarkTopology();
        var root = Refresh(publisher, ledger, world, 110, out var stats);
        Check(stats.Rebuilt == 3 && stats.TopologyChunks == 3 && stats.Resync == null && Current(root, world), "a room rebuild rebuilds every band; no stale room id survives");
        // MapFogged: one broad revision, no per-tile loop.
        for (var x = 0; x < MapSize; x++) for (var z = 0; z < MapSize; z++) world.Cells[x, z] = PlanningViewCell.Fog;
        ledger.MarkBroad();
        root = Refresh(publisher, ledger, world, 120, out stats);
        Check(stats.Rebuilt == 3 && stats.DirtyChunks == 3 && Current(root, world), "a map-wide change rebuilds every band");
    }

    private static void UnhookedFieldsWaitForTheScan()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var first = Refresh(publisher, ledger, world, 100, out _);
        // Glow moves with no event; pollution and indoors likewise.
        world.Cells[15, 34] = Cell(null, glow: 0.9);
        world.Cells[15, 26] = Cell(null, flags: PlanningViewCell.Walkable | PlanningViewCell.Indoors);
        var early = Refresh(publisher, ledger, world, 100 + PlanningViewRefresh.ValidateEveryTicks - 1, out var stats);
        Check(stats.Rebuilt == 0 && stats.CellsScanned == 0 && early.Chunk(2).ValidatedTick == 100, "before the cadence the view claims only its validation tick");
        var due = 100 + PlanningViewRefresh.ValidateEveryTicks;
        var scanned = Refresh(publisher, ledger, world, due, out stats);
        Check(stats.Validated == 1 && stats.Rebuilt == 2 && stats.CellsScanned > 0 && Current(scanned, world), "the scheduled scan revalidates the clean band and rebuilds the drifted ones");
        Check(scanned.Chunk(0).ValidatedTick == due && scanned.Chunk(0).CapturedTick == 100 && scanned.Chunk(0).Revision == first.Revision, "a scanned band keeps its capture and moves its validation");
        Check(stats.AgeTicks == 0, "after the scan every band is validated at the tick");
    }

    private static void ResyncReasons()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var root = Refresh(publisher, ledger, world, 100, out _);
        Refresh(publisher, ledger, world, 110, out var stats, maxX: RMaxX - 1);
        Check(stats.Resync == "columns" && stats.Rebuilt == 3, "a column change rebuilds");
        Refresh(publisher, ledger, world, 120, out _);
        // A new tracker (a new map object, a reload) cannot vouch for the old watermarks.
        Refresh(publisher, new PlanningViewLedger(MapSize, MapSize), world, 130, out stats);
        Check(stats.Resync == "tracker" && stats.Rebuilt == 3, "another ledger rebuilds");
        var fresh = new PlanningViewLedger(MapSize, MapSize);
        root = Refresh(publisher, fresh, world, 140, out _);
        Refresh(publisher, fresh, world, 139, out stats, publish: false);
        Check(stats.Resync == "rewind" && stats.Rebuilt == 3, "a rewound tick rebuilds");
        // A burst past the bounded dirty list: one more distinct tile than it holds.
        var big = new PlanningViewLedger(PlanningViewLedger.TileSize * 40, PlanningViewLedger.TileSize * 40);
        Refresh(publisher, big, world, 150, out _);
        for (var t = 0; t <= PlanningViewLedger.MaxDirtyTiles; t++) big.Mark(t % 40 * PlanningViewLedger.TileSize, t / 40 * PlanningViewLedger.TileSize);
        Refresh(publisher, big, world, 151, out stats);
        Check(stats.Resync == "overflow" && stats.Rebuilt == 3 && stats.DirtyTiles == PlanningViewLedger.MaxDirtyTiles, "an overflowed dirty list never looks complete");
        Refresh(publisher, big, world, 152, out stats);
        Check(stats.Resync == null && stats.Rebuilt == 0, "after the resync the list is drained");
        // Unload: the incarnation moves and the next capture boots.
        publisher.Invalidate();
        Refresh(publisher, big, world, 160, out stats);
        Check(stats.Resync == "bootstrap", "unload rebuilds from nothing");
    }

    // Hop A captures, a mutation lands, hop B captures from the same held
    // root, and the completions publish out of order: B, then A. A cannot
    // replace B, and the mutation made after B's capture is seen by the
    // next refresh even though B published after it.
    private static void RedirtyBetweenCaptureAndPublication()
    {
        var publisher = new PlanningWindowViewPublisher(4);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var held = Refresh(publisher, ledger, world, 100, out _);
        var a = Refresh(publisher, ledger, world, 101, out _, current: held, publish: false);
        world.Set(ledger, 20, 38, Cell("first"));
        var b = Refresh(publisher, ledger, world, 102, out var stats, current: held, publish: false);
        Check(stats.Rebuilt == 1 && Current(b, world), "the later capture rebuilds the redirtied band");
        world.Set(ledger, 20, 38, Cell("second"));
        Check(publisher.TryPublish(b) && !publisher.TryPublish(a) && ReferenceEquals(publisher.Acquire(), b), "the older completion cannot replace the newer");
        Check(ReferenceEquals(held.Chunk(2), a.Chunk(2)) && held.Chunk(2)[6 * 20 + 10].ZoneId == null, "a held reader's graph is unchanged");
        var next = Refresh(publisher, ledger, world, 103, out stats);
        Check(stats.Rebuilt == 1 && Current(next, world), "a mutation after capture, before publication, is not lost");
    }

    // A panned window (#710). A pure Z pan keeps the map-aligned bands of
    // the rows it still covers: the overlap is carried over by reference,
    // only the new rows and a grown partial edge band are read, and there
    // is no resync. An X pan changes every band's width and rebuilds.
    private static void PannedWindowReusesItsBands()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var ledger = new PlanningViewLedger(MapSize, MapSize);
        var world = new FakeWorld();
        var first = Refresh(publisher, ledger, world, 100, out _);
        // Down one band: 24..47 keeps 24-31 and 32-39, reads 40-47.
        var down = Refresh(publisher, ledger, world, 110, out var stats, minZ: RMinZ + 8, maxZ: RMaxZ + 8);
        Check(stats.Resync == null && stats.Chunks == 3 && stats.Reused == 2 && stats.Rebuilt == 1 && stats.CellsRead == 20 * 8 && Current(down, world),
            "a one-band Z pan reuses the two overlapping bands and reads one (" + stats.Reused + "/" + stats.Rebuilt + ")");
        Check(ReferenceEquals(down.Chunk(0), first.Chunk(1)) && ReferenceEquals(down.Chunk(1), first.Chunk(2)) && down.Complete, "the overlap is the same immutable chunks");
        // Up three rows: 21..44 has partial edge bands 21-23 and 40-44;
        // 24-31 and 32-39 are carried over, the edges read.
        var up = Refresh(publisher, ledger, world, 120, out stats, minZ: RMinZ + 5, maxZ: RMaxZ + 5);
        Check(stats.Resync == null && stats.Chunks == 4 && stats.Reused == 2 && stats.Rebuilt == 2 && stats.CellsRead == 20 * (3 + 5) && up.Complete && Current(up, world),
            "a partial Z pan reads only its edge bands (" + stats.CellsRead + ")");
        Check(up.Chunk(0).MinZ == 21 && up.Chunk(0).MaxZ == 23 && up.Chunk(3).MinZ == 40 && up.Chunk(3).MaxZ == 44, "partial edge bands tile from min_z");
        // Back down: the overlap is still judged by the ledger, and the
        // partial 40-44 band grows to 40-47 and is read.
        world.Set(ledger, 12, 30, Cell("panned"));
        var dirty = Refresh(publisher, ledger, world, 130, out stats, minZ: RMinZ + 8, maxZ: RMaxZ + 8);
        Check(stats.Resync == null && stats.DirtyChunks == 1 && stats.Reused == 1 && stats.Rebuilt == 2 && stats.CellsRead == 20 * 16 && Current(dirty, world),
            "a Z pan over a dirtied band rebuilds it with the grown edge band (" + stats.Reused + "/" + stats.Rebuilt + ")");
        // X pan: every band changes width, one rebuild of the region.
        var side = Refresh(publisher, ledger, world, 140, out stats, minZ: RMinZ + 8, maxZ: RMaxZ + 8, minX: RMinX + 4, maxX: RMaxX + 4);
        Check(stats.Resync == "columns" && stats.Reused == 0 && stats.Rebuilt == 3 && stats.CellsRead == 20 * 24 && Current(side, world),
            "an X pan rebuilds the region (" + stats.CellsRead + ")");
    }
}
