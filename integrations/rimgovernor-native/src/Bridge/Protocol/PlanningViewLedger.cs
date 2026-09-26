#nullable enable
using System;
using System.Threading;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The planning-window view's change ledger (#652): map-fixed tiles of
    /// TileSize x TileSize cells, each carrying the sequence number of the
    /// last mutation that touched it. The sequence is a counter, never the
    /// game tick, so two edits in one tick stay distinct and a rewound tick
    /// cannot make an old mutation look new. Map-wide and topology changes
    /// are one revision each rather than a loop over tiles, and the
    /// deduplicated dirty-tile list is bounded: a burst past it is an
    /// overflow the next refresh answers with a full rebuild.
    ///
    /// Game thread only: CellTracking owns one per map and forwards its
    /// hooks here in O(1); the capture reads it. Workers never see it, and
    /// nothing here references the game, so the probes drive it directly.
    /// </summary>
    internal sealed class PlanningViewLedger
    {
        internal const int TileSize = 8;
        internal const int MaxDirtyTiles = 1024;

        private static long generations;

        /// Unique per ledger: a chunk counted in another ledger's sequence
        /// (a new map, a reload) cannot be checked against this one.
        internal readonly long Generation;
        private readonly int width, height, tilesX;
        private readonly long[] tileRevision;
        private readonly bool[] dirty;
        private readonly int[] dirtyTiles;
        private int dirtyCount;
        private bool overflowed;
        private long sequence, broadRevision, topologyRevision;

        internal PlanningViewLedger(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Invalid ledger size.");
            Generation = Interlocked.Increment(ref generations);
            this.width = width; this.height = height;
            tilesX = (width + TileSize - 1) / TileSize;
            var tiles = tilesX * ((height + TileSize - 1) / TileSize);
            tileRevision = new long[tiles];
            dirty = new bool[tiles];
            dirtyTiles = new int[Math.Min(tiles, MaxDirtyTiles)];
        }

        /// The last mutation's number; content read now is current as of it.
        internal long Sequence => sequence;
        internal long BroadRevision => broadRevision;
        internal long TopologyRevision => topologyRevision;

        /// One cell's planning facts may have changed.
        internal void Mark(int x, int z)
        {
            if (x < 0 || z < 0 || x >= width || z >= height) return;
            var tile = z / TileSize * tilesX + x / TileSize;
            tileRevision[tile] = ++sequence;
            if (dirty[tile]) return;
            if (dirtyCount == dirtyTiles.Length) { overflowed = true; return; }
            dirty[tile] = true;
            dirtyTiles[dirtyCount++] = tile;
        }

        /// Every cell may have changed (MapFogged and the like).
        internal void MarkBroad() => broadRevision = ++sequence;

        /// Rooms were rebuilt: room ids (and indoors) anywhere may differ.
        internal void MarkTopology() => topologyRevision = ++sequence;

        /// The dirty tiles since the last drain and whether the list
        /// overflowed; both reset. Counts are telemetry; an overflow forces
        /// the caller's resync.
        internal void Drain(out int tiles, out bool overflow)
        {
            tiles = dirtyCount; overflow = overflowed;
            for (var i = 0; i < dirtyCount; i++) dirty[dirtyTiles[i]] = false;
            dirtyCount = 0; overflowed = false;
        }

        /// Whether any tile over the inclusive cell rectangle, or the whole
        /// map, changed after watermark; scanned counts the tiles looked at.
        internal bool Changed(int minX, int minZ, int maxX, int maxZ, long watermark, ref int scanned)
        {
            if (broadRevision > watermark) return true;
            for (var tz = Math.Max(0, minZ) / TileSize; tz <= Math.Min(height - 1, maxZ) / TileSize; tz++)
                for (var tx = Math.Max(0, minX) / TileSize; tx <= Math.Min(width - 1, maxX) / TileSize; tx++)
                {
                    scanned++;
                    if (tileRevision[tz * tilesX + tx] > watermark) return true;
                }
            return false;
        }
    }

    /// <summary>The live cells behind a refresh: the game in production, a
    /// fake grid in the probes.</summary>
    internal interface IPlanningViewSource
    {
        /// The planning fields of one cell now.
        PlanningViewCell Read(int x, int z);

        /// Whether the fields no hook covers (glow, pollution, indoors) still
        /// hold the values held captured; a fogged row is always current,
        /// since fog is hooked.
        bool StillCurrent(int x, int z, in PlanningViewCell held);
    }

    /// <summary>One refresh's work, for the hop's telemetry (#642).</summary>
    internal sealed class PlanningViewRefreshStats
    {
        internal int Chunks, Reused, Validated, Rebuilt, DirtyChunks, TopologyChunks, DirtyTiles, TilesScanned;
        internal long CellsScanned, CellsRead, AgeTicks, RetainedBytes;
        /// Why every chunk was rebuilt, or null when chunks were judged one by one.
        internal string? Resync;
    }

    /// <summary>
    /// The refresh itself (#652), pure over a ledger and a source. Per
    /// chunk of the region's row bands:
    ///   - a root-level resync (bootstrap, region or mask change, another
    ///     incarnation or ledger, a rewound tick, a dirty-list overflow)
    ///     rebuilds every chunk;
    ///   - a chunk whose tiles, the whole map, or room topology changed
    ///     after its watermark is rebuilt;
    ///   - a chunk validated ValidateEveryTicks or more before the tick is
    ///     scanned for the unhooked fields: unchanged it is revalidated,
    ///     otherwise rebuilt;
    ///   - any other chunk is carried over as the same immutable object.
    /// Work follows dirty and expired chunks plus the O(tiles) check per
    /// band; a bootstrap or a map-wide change is O(region).
    /// </summary>
    internal static class PlanningViewRefresh
    {
        internal const int ChunkRows = 8;

        /// The scheduled validation cadence for the unhooked fields: the
        /// controller's tightest planning tolerance (a stopped clock), so a
        /// served view is never older than it.
        internal const long ValidateEveryTicks = 250;

        // Bytes a retained cell costs beyond its strings: flags, the fog
        // bit, two doubles and three references, rounded.
        internal const int CellBytes = 48;

        internal static PlanningViewRoot Refresh(PlanningViewLedger ledger, PlanningWindowViewPublisher.Ticket ticket, PlanningViewRoot? current,
            Common.Identity identity, ulong nativeGeneration, int mapWidth, int mapHeight, int minX, int minZ, int maxX, int maxZ,
            long tick, IPlanningViewSource source, PlanningViewRefreshStats stats)
        {
            ledger.Drain(out stats.DirtyTiles, out var overflow);
            // The watermark is read before any cell: a mutation during or
            // after this refresh numbers past it and dirties the chunk again.
            var watermark = ledger.Sequence;
            stats.Resync = current == null ? "bootstrap"
                : current.Incarnation != ticket.Incarnation ? "incarnation"
                : !current.Serves(identity, minX, minZ, maxX, maxZ, PlanningViewRoot.PlanningMask) ? "region"
                : current.LedgerGeneration != ledger.Generation ? "tracker"
                : tick < current.PublishedTick ? "rewind"
                : overflow ? "overflow"
                : null;
            var width = maxX - minX + 1;
            var bands = (maxZ - minZ) / ChunkRows + 1;
            var chunks = new PlanningViewChunk[bands];
            var oldest = tick;
            for (var i = 0; i < bands; i++)
            {
                var bandMinZ = minZ + i * ChunkRows; var bandMaxZ = Math.Min(bandMinZ + ChunkRows - 1, maxZ);
                var old = stats.Resync == null && i < current!.ChunkCount ? current.Chunk(i) : null;
                PlanningViewChunk? kept = null;
                if (old != null && old.MinZ == bandMinZ && old.MaxZ == bandMaxZ)
                {
                    if (ledger.TopologyRevision > old.Watermark) stats.TopologyChunks++;
                    else if (ledger.Changed(minX, bandMinZ, maxX, bandMaxZ, old.Watermark, ref stats.TilesScanned)) stats.DirtyChunks++;
                    else if (tick - old.ValidatedTick < ValidateEveryTicks) { kept = old; stats.Reused++; }
                    else if (Scan(source, minX, maxX, old, width, stats)) { kept = old.Revalidated(tick, watermark); stats.Validated++; }
                }
                if (kept == null)
                {
                    var cells = new PlanningViewCell[(bandMaxZ - bandMinZ + 1) * width];
                    var next = 0;
                    for (var z = bandMinZ; z <= bandMaxZ; z++)
                        for (var x = minX; x <= maxX; x++) cells[next++] = source.Read(x, z);
                    stats.CellsRead += cells.Length;
                    stats.Rebuilt++;
                    kept = new PlanningViewChunk(bandMinZ, bandMaxZ, ticket.Revision, tick, tick, cells, watermark);
                }
                chunks[i] = kept;
                oldest = Math.Min(oldest, kept.ValidatedTick);
                stats.RetainedBytes += (long)kept.Count * CellBytes;
            }
            stats.Chunks = bands;
            stats.AgeTicks = tick - oldest;
            return new PlanningViewRoot(identity, nativeGeneration, ticket, mapWidth, mapHeight, minX, minZ, maxX, maxZ,
                PlanningViewRoot.PlanningMask, tick, chunks, ledger.Generation);
        }

        // The scan stops at the first changed cell: the band is read in
        // full then anyway.
        private static bool Scan(IPlanningViewSource source, int minX, int maxX, PlanningViewChunk chunk, int width, PlanningViewRefreshStats stats)
        {
            for (var z = chunk.MinZ; z <= chunk.MaxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    stats.CellsScanned++;
                    if (!source.StillCurrent(x, z, chunk[(z - chunk.MinZ) * width + (x - minX)])) return false;
                }
            return true;
        }
    }
}
