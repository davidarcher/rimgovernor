#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The published planning-window view (#650). The game thread captures a
    // root of detached primitive cell values (PlanningWindowViewCapture);
    // the publisher swaps it in whole; workers project it to the wire. No
    // type here references the game: a root holds strings, numbers and
    // arrays it alone owns, and nothing mutates it after construction.
    // Contract: docs/developers/contracts/planning-window-view.md.

    /// <summary>One detached cell. Flags carry CompactCellEncoding's bits;
    /// Fogged means the row reveals nothing else.</summary>
    internal readonly struct PlanningViewCell
    {
        internal const int Walkable = 4, Passable = 8, Occupied = 16, Doorway = 32, SupportsLight = 64,
            StorageEmpty = 128, Indoors = 256, Polluted = 512, HasFertility = 1024;

        internal readonly bool Fogged;
        internal readonly int Flags;
        internal readonly string? Roof, ZoneId, RoomId;
        internal readonly double Glow, Fertility;

        internal PlanningViewCell(bool fogged, int flags, string? roof, string? zoneId, string? roomId, double glow, double fertility)
        { Fogged = fogged; Flags = flags; Roof = roof; ZoneId = zoneId; RoomId = roomId; Glow = glow; Fertility = fertility; }

        internal static PlanningViewCell Fog => new PlanningViewCell(true, 0, null, null, null, 0, 0);
    }

    /// <summary>
    /// A band of whole region rows with its own content revision, capture
    /// tick and last validation tick. The cell array is owned: the
    /// constructor copies the builder's, nothing exposes it, and a
    /// revalidated chunk shares it read-only.
    /// </summary>
    internal sealed class PlanningViewChunk
    {
        internal readonly int MinZ, MaxZ;
        internal readonly ulong Revision;
        internal readonly long CapturedTick, ValidatedTick;
        private readonly PlanningViewCell[] cells;

        internal PlanningViewChunk(int minZ, int maxZ, ulong revision, long capturedTick, long validatedTick, PlanningViewCell[] built)
            : this(minZ, maxZ, revision, capturedTick, validatedTick, built, copy: true) { }

        private PlanningViewChunk(int minZ, int maxZ, ulong revision, long capturedTick, long validatedTick, PlanningViewCell[] source, bool copy)
        {
            if (maxZ < minZ || validatedTick < capturedTick || source == null || source.Length == 0 || source.Length % (maxZ - minZ + 1) != 0)
                throw new ArgumentException("Invalid planning view chunk.");
            MinZ = minZ; MaxZ = maxZ; Revision = revision; CapturedTick = capturedTick; ValidatedTick = validatedTick;
            cells = copy ? (PlanningViewCell[])source.Clone() : source;
        }

        internal int Count => cells.Length;
        internal PlanningViewCell this[int index] => cells[index];

        /// The same content confirmed unchanged at tick.
        internal PlanningViewChunk Revalidated(long tick) => new PlanningViewChunk(MinZ, MaxZ, Revision, CapturedTick, Math.Max(tick, ValidatedTick), cells, copy: false);
    }

    /// <summary>A fully constructed, immutable view root.</summary>
    internal sealed class PlanningViewRoot
    {
        /// The only field mask a view carries today: the planning fields.
        internal const string PlanningMask = "planning";

        internal readonly string ColonyId, LoadToken, Mask;
        internal readonly int MapId, MapWidth, MapHeight, MinX, MinZ, MaxX, MaxZ;
        internal readonly ulong Incarnation, Revision, NativeGeneration;
        internal readonly long PublishedTick;
        internal readonly bool Complete;
        private readonly PlanningViewChunk[] chunks;

        internal PlanningViewRoot(Common.Identity identity, ulong nativeGeneration, PlanningWindowViewPublisher.Ticket ticket, int mapWidth, int mapHeight,
            int minX, int minZ, int maxX, int maxZ, string mask, long publishedTick, PlanningViewChunk[] built)
        {
            ColonyId = identity.ColonyId; LoadToken = identity.LoadToken; MapId = identity.MapId; NativeGeneration = nativeGeneration;
            Incarnation = ticket.Incarnation; Revision = ticket.Revision; MapWidth = mapWidth; MapHeight = mapHeight;
            MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ; Mask = mask; PublishedTick = publishedTick; chunks = (PlanningViewChunk[])built.Clone();
            // Complete: the chunks tile the rows in order, each row the
            // region's width, and no chunk postdates publication.
            var z = minZ; var complete = chunks.Length > 0;
            foreach (var chunk in chunks)
            {
                complete &= chunk.MinZ == z && chunk.Count == (chunk.MaxZ - chunk.MinZ + 1) * (maxX - minX + 1)
                    && chunk.ValidatedTick <= publishedTick && chunk.Revision <= ticket.Revision;
                z = chunk.MaxZ + 1;
            }
            Complete = complete && z == maxZ + 1;
        }

        internal int ChunkCount => chunks.Length;
        internal PlanningViewChunk Chunk(int index) => chunks[index];

        internal bool SameIdentity(Common.Identity identity) => identity.ColonyId == ColonyId && identity.LoadToken == LoadToken && identity.MapId == MapId;

        internal bool Serves(Common.Identity identity, int minX, int minZ, int maxX, int maxZ, string mask)
            => SameIdentity(identity) && minX == MinX && minZ == MinZ && maxX == MaxX && maxZ == MaxZ && mask == Mask;
    }

    /// <summary>
    /// The one view slot. Roots are published with Interlocked and read
    /// with Volatile.Read, once per reader; only the current root is
    /// retained (chunks keep no parent). The incarnation moves on unload
    /// and on every identity change, so a capture begun before either can
    /// never replace the view published after it. Readers are bounded:
    /// projecting a root holds a reader lease.
    /// </summary>
    internal sealed class PlanningWindowViewPublisher
    {
        internal static readonly PlanningWindowViewPublisher Shared = new PlanningWindowViewPublisher(ReplyEncoder.Slots);

        internal readonly struct Ticket
        {
            internal readonly ulong Incarnation, Revision;
            internal Ticket(ulong incarnation, ulong revision) { Incarnation = incarnation; Revision = revision; }
        }

        private PlanningViewRoot? current;
        private long incarnation = 1, revision;
        private string? identity;
        private readonly object identityLock = new object();
        private readonly SemaphoreSlim readers;

        internal PlanningWindowViewPublisher(int readerSlots) { readers = new SemaphoreSlim(readerSlots, readerSlots); }

        internal ulong Incarnation => (ulong)Interlocked.Read(ref incarnation);
        internal int ReadersAvailable => readers.CurrentCount;

        /// The current root, acquired once by the caller.
        internal PlanningViewRoot? Acquire() => Volatile.Read(ref current);

        /// On the game thread, before a capture: a new identity invalidates
        /// the view and its pending captures, then a revision is allocated.
        internal Ticket Begin(Common.Identity scope)
        {
            var key = scope.ColonyId + "/" + scope.LoadToken + "/" + scope.MapId;
            lock (identityLock)
            {
                if (identity != key) { if (identity != null) Invalidate(); identity = key; }
            }
            return new Ticket(Incarnation, (ulong)Interlocked.Increment(ref revision));
        }

        /// Publishes root when it belongs to the current incarnation and is
        /// newer than the current root; false leaves the current root.
        internal bool TryPublish(PlanningViewRoot root)
        {
            if (!root.Complete) return false;
            while (true)
            {
                var held = Volatile.Read(ref current);
                if (root.Incarnation != Incarnation || held != null && held.Revision >= root.Revision) return false;
                if (Interlocked.CompareExchange(ref current, root, held) != held) continue;
                // An unload between the check and the swap clears it again.
                if (root.Incarnation == Incarnation) return true;
                Interlocked.CompareExchange(ref current, null, root);
                return false;
            }
        }

        /// Unload, reload or rewind: drop the view and every pending capture.
        internal void Invalidate()
        {
            Interlocked.Increment(ref incarnation);
            Interlocked.Exchange(ref current, null);
        }

        /// <summary>A reader's hold on one root; disposing releases its slot exactly once.</summary>
        internal sealed class Reader : IDisposable
        {
            private readonly SemaphoreSlim slots;
            private int released;
            internal readonly PlanningViewRoot Root;
            internal Reader(SemaphoreSlim slots, PlanningViewRoot root) { this.slots = slots; Root = root; }
            public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) slots.Release(); }
        }

        /// A reader slot for root, or null when every slot is held.
        internal Reader? Open(PlanningViewRoot root) => readers.Wait(0) ? new Reader(readers, root) : null;
    }

    /// <summary>Wire projection, on a worker, from a published root alone.</summary>
    internal static class PlanningWindowViewProjection
    {
        internal static Obs.CellFields PlanningFields() => new Obs.CellFields { Terrain = false, Roof = true, Visibility = true, Traversal = true,
            Zone = true, Areas = false, Things = false, Designations = false, Room = true, Growth = true };

        /// <summary>
        /// On a worker: publishes the hop's candidate and projects the root
        /// the hop serves under its own context. A candidate a newer capture
        /// already superseded still describes its hop and is served; one
        /// from an old incarnation is withdrawn (null), as is any with no
        /// reader slot free.
        /// </summary>
        internal static Obs.PlanningWindowView? Serve(PlanningWindowViewPublisher publisher, PlanningViewRoot candidate, Common.ObservationContext context)
        {
            publisher.TryPublish(candidate);
            var current = publisher.Acquire();
            var root = current != null && current.Revision == candidate.Revision ? current : candidate;
            if (root.Incarnation != publisher.Incarnation || !root.Complete) return null;
            using (var reader = publisher.Open(root))
                return reader == null ? null : Project(reader.Root, context);
        }

        internal static Obs.PlanningWindowView Project(PlanningViewRoot root, Common.ObservationContext context)
        {
            var view = new Obs.PlanningWindowView { Context = context.Clone(),
                MapSize = new Obs.MapSize { Width = (uint)root.MapWidth, Height = (uint)root.MapHeight },
                Region = new Obs.Rectangle { Minimum = new Common.Cell { X = root.MinX, Z = root.MinZ }, Maximum = new Common.Cell { X = root.MaxX, Z = root.MaxZ } },
                AppliedFields = PlanningFields(), Incarnation = root.Incarnation, Revision = root.Revision,
                PublishedTick = root.PublishedTick, Complete = root.Complete };
            var width = root.MaxX - root.MinX + 1;
            for (var i = 0; i < root.ChunkCount; i++)
            {
                var chunk = root.Chunk(i);
                view.Chunks.Add(new Obs.PlanningWindowChunk { MinZ = chunk.MinZ, MaxZ = chunk.MaxZ, Revision = chunk.Revision,
                    CapturedTick = chunk.CapturedTick, ValidatedTick = chunk.ValidatedTick, Cells = Compact(chunk, width) });
            }
            return view;
        }

        // CompactCellEncoding's row format over one chunk, tables per chunk.
        private static Obs.CompactCells Compact(PlanningViewChunk chunk, int width)
        {
            var compact = new Obs.CompactCells();
            var strings = new Dictionary<string, uint>();
            var glows = new Dictionary<double, uint>();
            uint StringIndex(string value)
            {
                if (!strings.TryGetValue(value, out var index)) { index = (uint)compact.Strings.Count; strings.Add(value, index); compact.Strings.Add(value); }
                return index;
            }
            var bytes = new List<byte>(width * 4);
            for (var row = 0; row < chunk.Count / width; row++)
            {
                bytes.Clear();
                for (var x = 0; x < width; x++)
                {
                    var cell = chunk[row * width + x];
                    if (cell.Fogged) { bytes.Add(2); bytes.Add(0); continue; }
                    var flags = cell.Flags | (cell.Roof != null ? 2048 : 0) | (cell.ZoneId != null ? 4096 : 0) | (cell.RoomId != null ? 8192 : 0);
                    bytes.Add((byte)flags); bytes.Add((byte)(flags >> 8));
                    if (cell.Roof != null) Varint(bytes, StringIndex(cell.Roof));
                    if (cell.ZoneId != null) Varint(bytes, StringIndex(cell.ZoneId));
                    if (cell.RoomId != null) Varint(bytes, StringIndex(cell.RoomId));
                    if (!glows.TryGetValue(cell.Glow, out var glow)) { glow = (uint)compact.Glow.Count; glows.Add(cell.Glow, glow); compact.Glow.Add(cell.Glow); }
                    Varint(bytes, glow);
                    if ((cell.Flags & PlanningViewCell.HasFertility) != 0) compact.Fertility.Add(cell.Fertility);
                }
                compact.Rows.Add(ByteString.CopyFrom(bytes.ToArray()));
            }
            return compact;
        }

        private static void Varint(List<byte> bytes, uint value)
        {
            while (value >= 128) { bytes.Add((byte)(value | 128)); value >>= 7; }
            bytes.Add((byte)value);
        }
    }
}
