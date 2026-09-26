#nullable enable
using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The game-thread half of the planning-window view (#650, #652): the
    /// map's change ledger (CellTracking.Ledger) decides which bands of the
    /// published root are carried over, scanned for the unhooked fields or
    /// read again (PlanningViewRefresh); a read band is read as get_cells
    /// reads the planning fields, under the same visibility and absence
    /// rules. The candidate is published by the worker that projects it.
    /// </summary>
    internal static class PlanningWindowViewCapture
    {
        internal const int MaxCells = NativeObservationTools.CellsPageLimit;
        internal const int ChunkRows = PlanningViewRefresh.ChunkRows;

        /// <summary>What a bundle hop serves for its view section (#654).</summary>
        internal struct Served
        {
            /// The root to project: the refresh this hop finished, or the
            /// last complete root for the same region while a newer one is
            /// still being captured. Null when neither exists.
            internal PlanningViewRoot? Root;
            /// Why no root is served ("capturing", "saturated"), or null.
            internal string? Pending;
            /// A newer capture of the region is still running.
            internal bool Refreshing;
        }

        /// The bundle's view section (#650), on the game thread. The refresh
        /// is a resumable job (#654): equivalent requests join the running
        /// job, this hop runs its units while the frame's shared allowance
        /// lasts, and the frame boundary carries the rest over later frames.
        /// A job this hop cannot finish serves the last complete root for
        /// the region, honestly aged, or an explicit pending status; never a
        /// synchronous full capture. The hop's observation account gets the
        /// cells this hop read and the refresh's cumulative work (#652).
        internal static Served ForBundle(Map map, Obs.BundleRequest request, Common.ObservationContext context, Func<bool> cancelled)
        {
            var served = new Served();
            if (request.PlanningWindowView == null) return served;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var cellsBefore = 0L;
            PlanningViewRefreshStats? stats = null;
            long candidates = 0;
            try
            {
                if (!Region(map, request.PlanningWindowView, out var minX, out var minZ, out var maxX, out var maxZ, out candidates)) return served;
                Install();
                var publisher = PlanningWindowViewPublisher.Shared;
                var scheduler = ObservationScheduling.Shared;
                ObservationScheduling.Join();
                var identity = context.Identity;
                var job = active;
                if (job != null && (job.Map != map || !job.Refresh.Serves(identity, minX, minZ, maxX, maxZ)))
                {
                    scheduler.Remove(job, "superseded");
                    job = null;
                }
                if (job == null)
                {
                    job = Start(map, identity, context, minX, minZ, maxX, maxZ);
                    if (scheduler.TryAdd(job)) active = job;
                    else { job = null; served.Pending = "saturated"; }
                }
                if (job != null)
                {
                    stats = job.Refresh.Stats;
                    cellsBefore = stats.CellsRead;
                    scheduler.RunInline(job, cancelled);
                    if (job.Published) { served.Root = job.Refresh.Root; return served; }
                    served.Refreshing = active == job;
                }
                // Not finished this hop: the last complete root for the
                // region, aged by its own chunk ticks.
                var held = publisher.Acquire();
                if (Holds(held, publisher, identity, minX, minZ, maxX, maxZ) && held!.PublishedTick <= context.Tick) served.Root = held;
                else if (served.Pending == null) served.Pending = "capturing";
                return served;
            }
            catch (Exception) { return new Served(); }
            finally
            {
                ObservationWork.Captured("planningWindowView", System.Diagnostics.Stopwatch.GetTimestamp() - began, stats != null ? stats.CellsRead - cellsBefore : 0, candidates);
                if (stats != null) ObservationWork.PlanningViewRefreshed(stats, served.Root != null);
            }
        }

        private static bool Holds(PlanningViewRoot? held, PlanningWindowViewPublisher publisher, Common.Identity identity, int minX, int minZ, int maxX, int maxZ)
            => held != null && held.Incarnation == publisher.Incarnation && held.Serves(identity, minX, minZ, maxX, maxZ, PlanningViewRoot.PlanningMask);

        /// The job answering the current region, or null. Game thread only.
        private static CaptureJob? active;

        private static bool Region(Map map, Obs.BundlePlanningWindowViewRequest request, out int minX, out int minZ, out int maxX, out int maxZ, out long candidates)
        {
            minX = minZ = maxX = maxZ = 0; candidates = 0;
            var min = request.Region?.Minimum; var max = request.Region?.Maximum;
            if (min == null || max == null || !min.HasX || !min.HasZ || !max.HasX || !max.HasZ || max.X < min.X || max.Z < min.Z) return false;
            candidates = ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1);
            if (candidates > MaxCells || !new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return false;
            minX = min.X; minZ = min.Z; maxX = max.X; maxZ = max.Z;
            return true;
        }

        private static CaptureJob Start(Map map, Common.Identity identity, Common.ObservationContext context, int minX, int minZ, int maxX, int maxZ)
        {
            var publisher = PlanningWindowViewPublisher.Shared;
            var ticket = publisher.Begin(identity);
            var tracking = CellTracking.For(map);
            // Rooms rebuild lazily on the first query after a wall
            // changes; rebuild them now, so the topology revision the
            // refresh compares already counts that change.
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            var refresh = new PlanningViewRefreshJob(tracking.Ledger, ticket, publisher.Acquire(), identity.Clone(), context.NativeGeneration,
                map.Size.x, map.Size.z, minX, minZ, maxX, maxZ, context.Tick, new MapSource(map, tracking), new PlanningViewRefreshStats());
            return new CaptureJob(map, tracking, refresh);
        }

        /// <summary>
        /// One planning-view refresh on the scheduler (#654): a band per
        /// unit, with the publisher incarnation, the map, its change ledger
        /// and the tick checked before each. The finished root is detached
        /// and is published here, on the game thread, before any worker
        /// sees it; an obsolete job is discarded unpublished.
        /// </summary>
        private sealed class CaptureJob : IObservationJob
        {
            internal readonly Map Map;
            private readonly CellTracking tracking;
            internal readonly PlanningViewRefreshJob Refresh;
            private long lastTick;
            internal bool Published;

            internal CaptureJob(Map map, CellTracking tracking, PlanningViewRefreshJob refresh)
            { Map = map; this.tracking = tracking; Refresh = refresh; lastTick = refresh.StartTick; }

            public string? Obsolete
            {
                get
                {
                    if (Refresh.Ticket.Incarnation != PlanningWindowViewPublisher.Shared.Incarnation) return "incarnation";
                    if (Current.Game == null || Find.Maps == null || !Find.Maps.Contains(Map)) return "unloaded";
                    if (CellTracking.For(Map) != tracking || tracking.Ledger != Refresh.Ledger) return "tracker";
                    if (Find.TickManager.TicksGame < lastTick) return "rewind";
                    return null;
                }
            }

            public bool Step()
            {
                var tick = (long)Find.TickManager.TicksGame;
                lastTick = tick;
                Map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                if (!Refresh.Step(tick)) return false;
                var publisher = PlanningWindowViewPublisher.Shared;
                Published = publisher.TryPublish(Refresh.Root!) || publisher.Acquire() == Refresh.Root;
                if (active == this) active = null;
                return true;
            }

            public void Abandon(string reason)
            {
                if (active == this) active = null;
            }
        }

        /// The live map as the refresh reads it.
        private sealed class MapSource : IPlanningViewSource
        {
            private readonly Map map;
            private readonly CellTracking tracking;
            internal MapSource(Map map, CellTracking tracking) { this.map = map; this.tracking = tracking; }

            public PlanningViewCell Read(int x, int z) => PlanningWindowViewCapture.Read(map, tracking, new IntVec3(x, 0, z));

            // Glow, pollution and indoors have no complete event stream:
            // compare their live values with the held row.
            public bool StillCurrent(int x, int z, in PlanningViewCell held)
            {
                if (held.Fogged) return true;
                var cell = new IntVec3(x, 0, z);
                var glow = (double)map.glowGrid.GroundGlowAt(cell);
                var polluted = ModsConfig.BiotechActive && map.pollutionGrid.IsPolluted(cell);
                return glow == held.Glow && polluted == ((held.Flags & PlanningViewCell.Polluted) != 0)
                    && CellTracking.Indoors(cell.GetRoom(map)) == ((held.Flags & PlanningViewCell.Indoors) != 0);
            }
        }

        // The planning fields of one cell, as NativeObservationTools.ReadCells
        // emits them: a fogged cell carries only its fog.
        private static PlanningViewCell Read(Map map, CellTracking tracking, IntVec3 cell)
        {
            if (cell.Fogged(map)) return PlanningViewCell.Fog;
            var flags = 0;
            string? roofName = null, zoneId = null, roomId = null;
            var roof = cell.GetRoof(map);
            if (roof != null) roofName = Identifier(roof.defName);
            if (cell.Walkable(map)) flags |= PlanningViewCell.Walkable;
            if (!cell.Impassable(map)) flags |= PlanningViewCell.Passable;
            if (NativeObservationTools.CellOccupied(map, cell)) flags |= PlanningViewCell.Occupied;
            if (NativeObservationTools.CellDoorway(map, cell)) flags |= PlanningViewCell.Doorway;
            if (cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)) flags |= PlanningViewCell.SupportsLight;
            var zone = map.zoneManager.ZoneAt(cell);
            if (zone != null) zoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (NativeZoneCreation.StorageEmpty(cell, map)) flags |= PlanningViewCell.StorageEmpty;
            var room = cell.GetRoom(map);
            tracking.NoteRoom(cell, room);
            if (room != null) roomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (CellTracking.Indoors(room)) flags |= PlanningViewCell.Indoors;
            var polluted = ModsConfig.BiotechActive && map.pollutionGrid.IsPolluted(cell);
            if (polluted) flags |= PlanningViewCell.Polluted;
            var groundGlow = map.glowGrid.GroundGlowAt(cell);
            var glow = Finite(groundGlow);
            tracking.NoteGrowth(cell, polluted, (float)glow);
            var fertility = map.fertilityGrid.FertilityAt(cell);
            double fertile = 0;
            if (fertility > 0f) { fertile = Finite(fertility); flags |= PlanningViewCell.HasFertility; }
            return new PlanningViewCell(false, flags, roofName, zoneId, roomId, glow, fertile);
        }

        private static bool installed;

        // Game.Dispose runs on every return to the menu and before every
        // load of another save: the view and its pending captures end there.
        private static void Install()
        {
            if (installed) return;
            installed = true;
            new Harmony("rimgovernor.planning-window-view").Patch(AccessTools.Method(typeof(Game), nameof(Game.Dispose), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(PlanningWindowViewCapture), nameof(Unloaded)));
        }

        private static void Unloaded() => PlanningWindowViewPublisher.Shared.Invalidate();

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
    }
}
