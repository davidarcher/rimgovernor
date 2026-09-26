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

        /// The bundle's view section (#650), on the game thread: the
        /// candidate its encoder publishes and serves, timed into the hop's
        /// observation account with the cells actually read and the
        /// refresh's work (#652).
        internal static PlanningViewRoot? ForBundle(Map map, Obs.BundleRequest request, Common.ObservationContext context)
        {
            if (request.PlanningWindowView == null) return null;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var stats = new PlanningViewRefreshStats();
            var root = Capture(map, request.PlanningWindowView, context, stats, out var candidates);
            ObservationWork.Captured("planningWindowView", System.Diagnostics.Stopwatch.GetTimestamp() - began, stats.CellsRead, candidates);
            ObservationWork.PlanningViewRefreshed(stats, root != null);
            return root;
        }

        /// The candidate root, or null when the region is invalid for map
        /// or the cells could not be read (the view is then unavailable for
        /// the hop, never served stale). candidates counts the region's cells.
        internal static PlanningViewRoot? Capture(Map map, Obs.BundlePlanningWindowViewRequest request, Common.ObservationContext context, PlanningViewRefreshStats stats, out long candidates)
        {
            candidates = 0;
            var min = request.Region?.Minimum; var max = request.Region?.Maximum;
            if (min == null || max == null || !min.HasX || !min.HasZ || !max.HasX || !max.HasZ || max.X < min.X || max.Z < min.Z) return null;
            candidates = ((long)max.X - min.X + 1) * ((long)max.Z - min.Z + 1);
            if (candidates > MaxCells || !new IntVec3(min.X, 0, min.Z).InBounds(map) || !new IntVec3(max.X, 0, max.Z).InBounds(map)) return null;
            Install();
            var publisher = PlanningWindowViewPublisher.Shared;
            var ticket = publisher.Begin(context.Identity);
            try
            {
                var tracking = CellTracking.For(map);
                // Rooms rebuild lazily on the first query after a wall
                // changes; rebuild them now, so the topology revision the
                // refresh compares already counts that change.
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                return PlanningViewRefresh.Refresh(tracking.Ledger, ticket, publisher.Acquire(), context.Identity, context.NativeGeneration,
                    map.Size.x, map.Size.z, min.X, min.Z, max.X, max.Z, context.Tick, new MapSource(map, tracking), stats);
            }
            catch (Exception) { return null; }
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
